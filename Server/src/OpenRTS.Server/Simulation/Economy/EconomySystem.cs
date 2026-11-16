using System.Numerics;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Simulation.Economy;

/// <summary>
/// 자원·채집·건설·생산. 전투 엔진처럼 유닛의 명령(<see cref="UnitOrder.Gather"/>, <see cref="UnitOrder.Build"/>)을 보고
/// 매 틱 실행한다. 비용은 명령을 받을 때 내고(건설은 짓기 전에 취소하면 돌려줌), 자원은 플레이어별로 서버만 안다.
/// </summary>
public sealed class EconomySystem
{
    /// <summary>건설 현장에 다른 유닛이 서 있으면 이만큼 기다린 뒤 포기하고 비용을 돌려준다.</summary>
    public const float PlaceTimeout = 5f;

    /// <summary>일꾼이 같은 목적지로 경로를 다시 찾는 최소 간격(초).</summary>
    private const float RepathInterval = 0.5f;

    /// <summary>광맥이 다 떨어지면 이 거리 안의 다른 광맥으로 옮긴다.</summary>
    private const float FieldSearchRange = 15f;

    private readonly World _world;
    private readonly Dictionary<byte, int> _ore = new();
    private readonly List<Unit> _scratch = new();

    public EconomySystem(World world)
    {
        _world = world;
    }

    /// <summary>시나리오 시작 후 본진에 반납한 자원 합계.</summary>
    public int TotalGathered { get; private set; }

    public int UnitsProduced { get; private set; }

    public int BuildingsCompleted { get; private set; }

    private float Radius => _world.Settings.UnitRadius;

    public int OreOf(byte owner) => _ore.GetValueOrDefault(owner);

    /// <summary>테스트·디버그용.</summary>
    public void SetOre(byte owner, int amount) => _ore[owner] = amount;

    public void Reset()
    {
        _ore.Clear();
        TotalGathered = 0;
        UnitsProduced = 0;
        BuildingsCompleted = 0;
    }

    /// <summary>
    /// Skirmish 시작 기지: 팀 진영(1팀 왼쪽, 2팀 오른쪽)에 본진, 맵 가장자리 쪽 반원에 광맥 8개, 안쪽에 일꾼 4.
    /// 같은 팀 두 번째 플레이어부터는 위·아래로 비켜 놓는다.
    /// </summary>
    public IReadOnlyList<Unit> SpawnBase(byte owner, byte team, int slot)
    {
        var side = team == 1 ? -1f : 1f;
        var y = slot == 0 ? 0f : (slot % 2 == 1 ? 1 : -1) * 24f * ((slot + 1) / 2);
        var hqPos = new Vector2(side * 36f, Math.Clamp(y, -40f, 40f));
        var result = new List<Unit> { _world.Spawn(hqPos, owner: owner, team: team, kind: UnitKind.Headquarters) };

        for (var i = 0; i < EconomyRules.FieldsPerBase; i++)
        {
            var angle = (-75f + 150f * i / (EconomyRules.FieldsPerBase - 1)) * MathF.PI / 180f;
            var dir = new Vector2(MathF.Cos(angle) * side, MathF.Sin(angle));
            _world.Spawn(hqPos + dir * 7f, isBot: true, kind: UnitKind.OreField);
        }

        _world.EnsureNavigation();
        for (var i = 0; i < EconomyRules.StartingWorkers; i++)
        {
            var p = hqPos + new Vector2(-side * 3.5f, (i - (EconomyRules.StartingWorkers - 1) / 2f) * 1.5f);
            var worker = _world.Spawn(p, owner: owner, team: team, kind: UnitKind.Worker);
            worker.Order = UnitOrder.Idle;
            result.Add(worker);
        }

        _ore[owner] = EconomyRules.StartingOre;
        return result;
    }

    // ── 명령 ──────────────────────────────────────────────

    /// <summary>
    /// 채집(우클릭 광맥) / 짓다 만 건물 이어 짓기(우클릭 내 건설 중 건물) / 본진에 반납.
    /// 일꾼만 따르고, 나머지 유닛은 그 자리로 이동한다.
    /// </summary>
    public int Gather(byte owner, List<Unit> group, Unit? target, Vector2 point)
    {
        if (target == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var u in group)
        {
            if (u.Kind != UnitKind.Worker)
            {
                u.Order = UnitOrder.Move;
                _world.MoveUnit(u, point);
                continue;
            }

            if (target.Kind == UnitKind.OreField)
            {
                StartGather(u, target);
            }
            else if (target.Owner == owner && target.UnderConstruction)
            {
                u.Order = UnitOrder.Build;
                u.BuildingId = target.Id;
                u.BuildKind = target.Kind;
                u.BuildPaid = false;
            }
            else if (target.Owner == owner && target.Kind == UnitKind.Headquarters && u.Carrying > 0)
            {
                u.Order = UnitOrder.Gather; // 반납 후 마지막 광맥으로
            }
            else
            {
                continue;
            }

            u.ChaseTimer = 0;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 건설: 선택한 일꾼 중 현장에 가장 가까운 한 명이 간다. 비용을 지금 내고, 짓기 시작하기 전에 다른 명령을 받으면 돌려준다.
    /// 현장은 맵 안이어야 하고, 벽·건물·광맥과 겹치지 않아야 하며, 우리 팀에 보이는 곳이어야 한다.
    /// </summary>
    public int Build(byte owner, List<Unit> group, UnitKind kind, Vector2 site)
    {
        if (!UnitArchetypes.CanBuild(kind))
        {
            return 0;
        }

        var worker = group.Where(u => u.Kind == UnitKind.Worker && !u.IsDead)
            .OrderBy(u => Vector2.DistanceSquared(u.Position, site)).FirstOrDefault();
        var cost = UnitArchetypes.Of(kind).Cost;
        if (worker == null || OreOf(owner) < cost || !_world.CanSee(worker.Team, site) || !IsSiteBuildable(kind, site))
        {
            return 0;
        }

        CancelWork(worker);
        _ore[owner] = OreOf(owner) - cost;
        worker.Windup = 0;
        worker.AttackTargetId = 0;
        worker.Order = UnitOrder.Build;
        worker.BuildKind = kind;
        worker.BuildSite = site;
        worker.BuildingId = 0;
        worker.BuildPaid = true;
        worker.PlaceWait = 0;
        worker.ChaseTimer = 0;
        return 1;
    }

    /// <summary>생산: 고른 건물 중 대기열이 가장 짧은 곳에 하나 넣는다. 비용은 지금 낸다.</summary>
    public int Train(byte owner, List<Unit> group, UnitKind kind)
    {
        var building = group
            .Where(b => b.IsStatic && !b.UnderConstruction && UnitArchetypes.CanProduce(b.Kind, kind) && b.Queue.Count < EconomyRules.MaxQueue)
            .OrderBy(b => b.Queue.Count).FirstOrDefault();
        var cost = UnitArchetypes.Of(kind).Cost;
        if (building == null || OreOf(owner) < cost)
        {
            return 0;
        }

        _ore[owner] = OreOf(owner) - cost;
        building.Queue.Add(kind);
        return 1;
    }

    /// <summary>다른 명령을 받기 전에 호출: 아직 짓기 시작하지 않은 건설이면 비용을 돌려준다.</summary>
    public void CancelWork(Unit u)
    {
        if (u.Order == UnitOrder.Build && u.BuildingId == 0 && u.BuildPaid)
        {
            _ore[u.Owner] = OreOf(u.Owner) + UnitArchetypes.Of(u.BuildKind).Cost;
        }

        u.BuildPaid = false;
        u.WorkTimer = 0;
    }

    /// <summary>현장이 맵 안이고 벽·건물·광맥과 겹치지 않는지 (유닛은 보지 않음 — 짓기 시작할 때 확인).</summary>
    public bool IsSiteBuildable(UnitKind kind, Vector2 site)
    {
        var half = UnitArchetypes.Of(kind).Footprint;
        var h = SimConstants.MapHalfSize - 1f;
        if (MathF.Abs(site.X) + half > h || MathF.Abs(site.Y) + half > h)
        {
            return false;
        }

        var rect = new WallRect(site - new Vector2(half), site + new Vector2(half));
        foreach (var wall in _world.MapWalls)
        {
            if (Overlaps(rect, wall, margin: Radius * 2))
            {
                return false;
            }
        }

        foreach (var u in _world.Units)
        {
            if (u.IsStatic && Overlaps(rect, u.Footprint, margin: Radius * 2))
            {
                return false; // 건물 사이로 유닛이 지나갈 틈을 남긴다
            }
        }

        return true;
    }

    /// <summary>생산 대기열 (PlayerState 메시지용).</summary>
    public List<ProductionQueue> QueuesOf(byte owner)
    {
        var result = new List<ProductionQueue>();
        foreach (var u in _world.Units)
        {
            if (u.Owner == owner && u.IsStatic && u.Queue.Count > 0)
            {
                result.Add(new ProductionQueue
                {
                    BuildingId = u.Id,
                    Items = u.Queue.ToArray(),
                    Progress = u.QueueProgress / UnitArchetypes.Of(u.Queue[0]).BuildTime,
                });
            }
        }

        return result;
    }

    // ── 매 틱 ─────────────────────────────────────────────

    public void Update(float dt)
    {
        _scratch.Clear();
        _scratch.AddRange(_world.Units); // 생산으로 유닛이 늘어나므로 복사본을 돈다
        foreach (var u in _scratch)
        {
            if (u.IsDead)
            {
                continue;
            }

            if (u.Kind == UnitKind.Worker)
            {
                u.ChaseTimer -= dt;
                if (u.Order == UnitOrder.Gather)
                {
                    UpdateGatherer(u, dt);
                }
                else if (u.Order == UnitOrder.Build)
                {
                    UpdateBuilder(u, dt);
                }
            }
            else if (u.IsStatic && u.Team != UnitArchetypes.NeutralTeam && !u.UnderConstruction && u.Queue.Count > 0)
            {
                UpdateProduction(u, dt);
            }
        }
    }

    private void UpdateGatherer(Unit u, float dt)
    {
        if (u.Carrying > 0)
        {
            var hq = NearestDropOff(u);
            if (hq == null)
            {
                Idle(u);
                return;
            }

            if (_world.Combat.EdgeDistance(u, hq) <= EconomyRules.WorkReach)
            {
                _ore[u.Owner] = OreOf(u.Owner) + u.Carrying;
                TotalGathered += u.Carrying;
                u.Carrying = 0;
                u.ChaseTimer = 0;
                return; // 다음 틱부터 광맥으로
            }

            GoTo(u, hq);
            return;
        }

        var field = _world.Find(u.GatherFieldId);
        if (field is not { Kind: UnitKind.OreField, Ore: > 0 })
        {
            field = NearestField(u.GatherFieldPos);
            if (field == null)
            {
                Idle(u);
                return;
            }

            u.GatherFieldId = field.Id;
            u.GatherFieldPos = field.Position;
        }

        if (_world.Combat.EdgeDistance(u, field) <= EconomyRules.WorkReach)
        {
            if (u.IsMoving)
            {
                u.Stop();
            }

            u.WorkTimer += dt;
            if (u.WorkTimer >= EconomyRules.GatherTime)
            {
                var amount = Math.Min(EconomyRules.GatherAmount, field.Ore);
                field.Ore -= amount;
                u.Carrying = amount;
                u.WorkTimer = 0;
                u.ChaseTimer = 0;
                if (field.Ore <= 0)
                {
                    _world.Remove(field.Id); // 다 캔 광맥은 사라진다 (길이 열린다)
                }
            }

            return;
        }

        u.WorkTimer = 0;
        GoTo(u, field);
    }

    private void UpdateBuilder(Unit u, float dt)
    {
        if (u.BuildingId != 0)
        {
            var b = _world.Find(u.BuildingId);
            if (b == null || !b.UnderConstruction)
            {
                Idle(u); // 완성됐거나 부서졌다
                return;
            }

            if (_world.Combat.EdgeDistance(u, b) <= EconomyRules.WorkReach + 0.3f)
            {
                if (u.IsMoving)
                {
                    u.Stop();
                }

                var stats = b.Stats;
                b.BuildProgress = MathF.Min(1f, b.BuildProgress + dt / stats.BuildTime);
                b.Hp = MathF.Min(stats.MaxHp, b.Hp + stats.MaxHp * dt / stats.BuildTime);
                if (b.BuildProgress >= 1f)
                {
                    BuildingsCompleted++;
                    Idle(u);
                }

                return;
            }

            GoTo(u, b);
            return;
        }

        // 아직 짓기 전: 현장 가장자리까지 가서, 현장이 비어 있으면 건물을 놓는다.
        var half = UnitArchetypes.Of(u.BuildKind).Footprint;
        var rect = new WallRect(u.BuildSite - new Vector2(half), u.BuildSite + new Vector2(half));
        var gap = Vector2.Distance(u.Position, rect.ClosestPoint(u.Position)) - Radius;
        if (gap > EconomyRules.WorkReach + 0.5f)
        {
            GoTo(u, rect);
            return;
        }

        if (u.IsMoving)
        {
            u.Stop();
        }

        if (!IsSiteBuildable(u.BuildKind, u.BuildSite) || IsOccupied(rect, u))
        {
            u.PlaceWait += dt;
            if (u.PlaceWait > PlaceTimeout)
            {
                CancelWork(u); // 돌려준다
                Idle(u);
            }

            return;
        }

        var building = _world.Spawn(u.BuildSite, owner: u.Owner, team: u.Team, kind: u.BuildKind);
        building.BuildProgress = 0.01f;
        building.Hp = MathF.Max(1f, building.Stats.MaxHp * 0.01f);
        building.Order = UnitOrder.Idle;
        u.BuildingId = building.Id;
        u.BuildPaid = false; // 이제 취소해도 돌려주지 않는다 (짓다 만 건물이 남는다)
    }

    private void UpdateProduction(Unit b, float dt)
    {
        var kind = b.Queue[0];
        b.QueueProgress += dt;
        if (b.QueueProgress < UnitArchetypes.Of(kind).BuildTime)
        {
            return;
        }

        // 맵 가운데 쪽 발판 바깥에 내보낸다.
        var toCenter = -b.Position;
        toCenter = toCenter.LengthSquared() > 1e-4f ? Vector2.Normalize(toCenter) : Vector2.UnitX;
        var exit = b.Position + toCenter * (b.Stats.Footprint + Radius + 0.4f) + new Vector2(-toCenter.Y, toCenter.X) * ((UnitsProduced % 5) - 2) * 0.6f;
        var unit = _world.Spawn(exit, owner: b.Owner, team: b.Team, kind: kind);
        unit.Order = UnitOrder.Idle;
        b.Queue.RemoveAt(0);
        b.QueueProgress = 0;
        UnitsProduced++;

        if (kind == UnitKind.Worker && NearestField(b.Position) is { } field)
        {
            StartGather(unit, field); // 새 일꾼은 바로 근처 광맥으로
        }
    }

    private static void StartGather(Unit u, Unit field)
    {
        u.Order = UnitOrder.Gather;
        u.GatherFieldId = field.Id;
        u.GatherFieldPos = field.Position;
        u.WorkTimer = 0;
        u.ChaseTimer = 0;
    }

    private void Idle(Unit u)
    {
        u.Order = UnitOrder.Idle;
        u.Stop();
        u.LeashAnchor = u.Position;
        u.WorkTimer = 0;
        u.BuildingId = 0;
    }

    /// <summary>발판 사각형 바깥, 유닛 쪽 가장자리로 간다 (같은 목적지면 경로를 자주 다시 찾지 않는다).</summary>
    private void GoTo(Unit u, Unit structure) => GoTo(u, structure.Footprint);

    private void GoTo(Unit u, WallRect rect)
    {
        if (u.ChaseTimer > 0 && u.IsMoving)
        {
            return;
        }

        var edge = rect.ClosestPoint(u.Position);
        var away = u.Position - edge;
        if (away.LengthSquared() < 1e-6f)
        {
            away = u.Position - rect.Center;
        }

        var dest = edge + (away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitX) * (Radius + 0.15f);
        if (u.IsMoving && u.Target is { } t && Vector2.DistanceSquared(t, _world.Standable(dest)) < 0.25f)
        {
            return;
        }

        _world.MoveUnit(u, dest);
        u.ChaseTimer = RepathInterval;
    }

    private bool IsOccupied(WallRect rect, Unit except)
    {
        foreach (var other in _world.Units)
        {
            if (other == except || other.IsStatic)
            {
                continue;
            }

            if (Vector2.Distance(other.Position, rect.ClosestPoint(other.Position)) < Radius)
            {
                return true;
            }
        }

        return false;
    }

    private Unit? NearestDropOff(Unit u)
    {
        Unit? best = null;
        var bestDist = float.MaxValue;
        foreach (var b in _world.Units)
        {
            if (b.Kind == UnitKind.Headquarters && b.Owner == u.Owner && !b.UnderConstruction && !b.IsDead)
            {
                var d = Vector2.DistanceSquared(b.Position, u.Position);
                if (d < bestDist)
                {
                    best = b;
                    bestDist = d;
                }
            }
        }

        return best;
    }

    private Unit? NearestField(Vector2 near)
    {
        Unit? best = null;
        var bestDist = FieldSearchRange * FieldSearchRange;
        foreach (var f in _world.Units)
        {
            if (f.Kind == UnitKind.OreField && f.Ore > 0)
            {
                var d = Vector2.DistanceSquared(f.Position, near);
                if (d < bestDist)
                {
                    best = f;
                    bestDist = d;
                }
            }
        }

        return best;
    }

    private static bool Overlaps(WallRect a, WallRect b, float margin) =>
        a.Min.X < b.Max.X + margin && a.Max.X > b.Min.X - margin && a.Min.Y < b.Max.Y + margin && a.Max.Y > b.Min.Y - margin;
}
