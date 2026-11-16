using System.Numerics;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Server.Simulation.Navigation;
using OpenRTS.Shared;
using OpenRTS.Shared.Abilities;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Simulation.Abilities;

/// <summary>
/// 능력(스킬) 엔진. 능력 데이터(<see cref="AbilityDefinition"/>)를 해석해 시전·발동·전달·효과를 처리한다.
///
/// <para><b>시전 상태</b> (유닛마다 <see cref="ActiveCast"/> 하나)</para>
/// <code>
/// TryBegin ─▶ Approach(사거리 밖이면 걸어감) ─▶ Windup(선딜, 서 있음) ─▶ 발동(재사용 대기 시작)
///                                                              ├─ Instant / Area / Projectile ─▶ 끝
///                                                              ├─ Channel(주기 범위 효과) ─────▶ 끝
///                                                              └─ Dash(돌진, 도착 시 범위) ────▶ 끝
/// 기절·사망·대상 상실·다른 명령 ─▶ 취소 (발동 전이면 재사용 대기 없음)
/// </code>
/// 끝나거나 취소되면 시전 전 명령(대기·공격·공격 이동·이동)으로 돌아간다.
///
/// <para>참고 서버(spells.md 10장)에서 확인된 문제를 피한다: 효과는 데이터에서 오고(스크립트 하드코딩 아님),
/// 정신 집중은 선딜 0 이어도 시작되며, 직선 투사체는 한 틱 이동 구간 전체를 훑어(터널링 없음) 벽에 막히고 관통 수를 지킨다.</para>
///
/// 틱 순서: <see cref="Update"/> 는 이동 계산 전(시전·정신 집중·지연 범위·상태 효과),
/// <see cref="UpdateAfterMovement"/> 는 이동 뒤(돌진 도착, 투사체 명중 — 이번 틱 위치로).
/// </summary>
public sealed class AbilitySystem
{
    /// <summary>사거리 밖 대상에게 다가갈 때 경로를 다시 찾는 간격(초).</summary>
    public const float ApproachRepathInterval = 0.4f;

    /// <summary>선딜 지연 보상 상한: 선딜의 이 비율까지.</summary>
    public const float MaxWindupCreditRatio = 0.5f;

    /// <summary>선딜 지연 보상 상한: 최대 이 시간(초)까지.</summary>
    public const float MaxWindupCreditSeconds = 0.1f;

    /// <summary>
    /// 명령 지연 <paramref name="commandDelay"/> 중 선딜에서 빼 줄 수 있는 양.
    /// min(지연, 선딜 × 0.5, 0.1초). 선딜이 0 이면 0.
    /// </summary>
    public static float WindupCredit(float castTime, float commandDelay) =>
        castTime <= 0 || !(commandDelay > 0)
            ? 0f
            : MathF.Min(commandDelay, MathF.Min(castTime * MaxWindupCreditRatio, MaxWindupCreditSeconds));

    /// <summary>타이머 비교 여유 (틱 길이 1/30 을 여러 번 빼면 생기는 부동소수 오차).</summary>
    private const float TimeEpsilon = 1e-4f;

    /// <summary>마지막으로 선딜에 적용한 보상(초, 확인용).</summary>
    public float LastWindupCredit { get; private set; }

    /// <summary>시전 중 지점이 이만큼 넘게 맵 밖이면 맵 안으로 자른다.</summary>
    private const float MapMargin = 0.5f;

    private readonly World _world;
    private readonly List<LineProjectile> _lines = new();
    private readonly List<HomingProjectile> _homing = new();
    private readonly List<PendingArea> _areas = new();
    private readonly List<AbilityEvent> _events = new();
    private readonly List<(Vector2 Caster, Vector2 Point)> _eventPositions = new();
    private readonly Dictionary<AbilityId, int> _releases = new();
    private readonly List<(float T, Unit Unit)> _hits = new();

    private sealed class LineProjectile
    {
        public int CasterId;
        public byte Team;
        public AbilityDefinition Definition = null!;
        public Vector2 Position;
        public Vector2 Direction;
        public float Remaining;
        public int PierceLeft;
        public readonly HashSet<int> Hit = new();
    }

    private sealed class HomingProjectile
    {
        public int CasterId;
        public byte Team;
        public AbilityDefinition Definition = null!;
        public Vector2 Position;
        public int TargetId;
    }

    private sealed class PendingArea
    {
        public int CasterId;
        public byte Team;
        public AbilityDefinition Definition = null!;
        public Vector2 Center;
        public float Delay;
    }

    public AbilitySystem(World world)
    {
        _world = world;
    }

    /// <summary>능력별 서버 스크립트 (선택).</summary>
    public AbilityScriptRegistry Scripts { get; } = new();

    /// <summary>쌓인 연출 이벤트. 서버가 스냅샷과 함께 보내고 <see cref="ClearEvents"/> 한다.</summary>
    public IReadOnlyList<AbilityEvent> Events => _events;

    /// <summary><see cref="Events"/> 와 같은 순서의 시전자 위치·지점 (시야 필터용).</summary>
    public IReadOnlyList<(Vector2 Caster, Vector2 Point)> EventPositions => _eventPositions;

    /// <summary>시나리오 시작 후 능력별 발동 횟수 (확인용).</summary>
    public IReadOnlyDictionary<AbilityId, int> Releases => _releases;

    public int ActiveProjectiles => _lines.Count + _homing.Count;

    public int PendingAreas => _areas.Count;

    public void ClearEvents()
    {
        _events.Clear();
        _eventPositions.Clear();
    }

    public void Reset()
    {
        _lines.Clear();
        _homing.Clear();
        _areas.Clear();
        _releases.Clear();
        ClearEvents();
    }

    // ── 명령 ──────────────────────────────────────────────

    /// <summary>
    /// 플레이어 명령: 고른 유닛 중 그 능력을 가진 유닛이 쓴다.
    /// <see cref="AbilityDefinition.SingleCaster"/> 면 대상에 가장 가까운 준비된 유닛 하나, 아니면 준비된 유닛 모두.
    /// </summary>
    /// <param name="commandDelay">
    /// 명령이 서버에 오기까지 걸린 시간(초). 바로 선딜에 들어가는 시전이면 그만큼 선딜을 줄여
    /// 플레이어가 누른 시점 기준으로 발동하게 한다 (<see cref="WindupCredit"/> 의 상한 안에서).
    /// </param>
    public int Command(List<Unit> group, AbilityId id, int targetUnit, Vector2 point, float commandDelay = 0f)
    {
        if (!AbilityCatalog.TryGet(id, out var def))
        {
            return 0;
        }

        var aim = targetUnit != 0 && _world.Find(targetUnit) is { } t ? t.Position : point;
        var casters = group
            .Where(u => !u.IsStatic && !u.IsDead && u.FindAbility(id) is { IsReady: true })
            .OrderBy(u => Vector2.DistanceSquared(u.Position, aim))
            .ToList();
        if (def.SingleCaster && casters.Count > 1)
        {
            casters.RemoveRange(1, casters.Count - 1);
        }

        var count = 0;
        foreach (var u in casters)
        {
            if (TryBegin(u, id, targetUnit, point, commandDelay))
            {
                count++;
            }
        }

        if (count == 0)
        {
            // 거부(재사용 대기·기절·대상 무효 등): 그 능력을 가진 가장 가까운 유닛 이름으로 취소 이벤트를 보내
            // 클라이언트가 즉시 표시(예측)를 제한 시간까지 기다리지 않고 바로 거두게 한다.
            var owner = group.Where(u => !u.IsStatic && u.FindAbility(id) != null)
                .OrderBy(u => Vector2.DistanceSquared(u.Position, aim)).FirstOrDefault();
            if (owner != null)
            {
                Emit(AbilityEventType.CastCancel, owner, id, targetUnit, point);
            }
        }

        return count;
    }

    /// <summary>
    /// 시전을 시작한다 (검증 포함). 플레이어 명령과 AI 가 같이 쓴다.
    /// 유닛 대상은 시전자 팀에 보여야 하고, 능력의 대상 범위(적·아군·자신)에 맞아야 한다.
    /// <paramref name="commandDelay"/> 는 플레이어 명령의 지연(초) — 사거리 안이라 바로 선딜에 들어갈 때만 보상한다
    /// (걸어가야 하면 선딜은 도착한 뒤에 시작하므로 지연과 무관). AI 는 0.
    /// </summary>
    public bool TryBegin(Unit u, AbilityId id, int targetUnit, Vector2 point, float commandDelay = 0f)
    {
        var ability = u.FindAbility(id);
        if (ability == null || !ability.IsReady || u.IsDead || u.IsStatic || u.IsStunned || u.IsDashing)
        {
            return false;
        }

        var def = ability.Definition;
        if (def.Targeting == AbilityTargeting.Unit)
        {
            var target = _world.Find(targetUnit);
            if (target == null || !IsValidUnitTarget(u, target, def))
            {
                return false;
            }
        }
        else
        {
            targetUnit = 0;
        }

        var h = SimConstants.MapHalfSize - MapMargin;
        point = Vector2.Clamp(point, new Vector2(-h), new Vector2(h));

        Cancel(u, emit: true);
        _world.EconomySystem.CancelWork(u);
        var cast = new ActiveCast(ability, targetUnit, point)
        {
            ResumeOrder = u.Order == UnitOrder.Cast ? UnitOrder.Idle : u.Order,
            ResumeGoal = u.Order == UnitOrder.Move ? u.Target ?? u.Position : u.Goal,
            ResumeTarget = u.AttackTargetId,
        };
        u.Windup = 0;
        u.AttackTargetId = 0;
        u.Order = UnitOrder.Cast;
        u.Cast = cast;

        if (InCastRange(u, cast, TargetOf(cast)))
        {
            EnterWindup(u, cast, WindupCredit(def.CastTime, commandDelay));
        }

        return true;
    }

    /// <summary>진행 중인 시전을 취소한다 (다른 명령·기절·사망·대상 상실). 발동 전이면 재사용 대기 없음.</summary>
    public void Cancel(Unit u, bool emit)
    {
        var cast = u.Cast;
        if (cast == null)
        {
            return;
        }

        u.Cast = null;
        u.DashTarget = null;
        if (emit)
        {
            Emit(AbilityEventType.CastCancel, u, cast.Definition.Id, cast.TargetUnit, cast.Point);
        }

        if (u.Order == UnitOrder.Cast)
        {
            Resume(u, cast);
        }
    }

    // ── 매 틱 ─────────────────────────────────────────────

    /// <summary>이동 계산 전: 재사용 대기·상태 효과 시간, 시전 진행, 지연 범위.</summary>
    public void Update(float dt)
    {
        foreach (var u in _world.Units)
        {
            if (u.IsDead)
            {
                continue;
            }

            foreach (var a in u.Abilities)
            {
                if (a.Cooldown > 0)
                {
                    a.Cooldown = MathF.Max(0, a.Cooldown - dt);
                }
            }

            UpdateStatuses(u, dt);
            if (u.Cast != null)
            {
                UpdateCast(u, u.Cast, dt);
            }
        }

        for (var i = _areas.Count - 1; i >= 0; i--)
        {
            var area = _areas[i];
            area.Delay -= dt;
            if (area.Delay <= 0)
            {
                _areas.RemoveAt(i);
                ApplyArea(area.CasterId, area.Team, area.Definition, area.Center);
            }
        }
    }

    /// <summary>이동 뒤: 돌진 도착, 투사체 이동·명중.</summary>
    public void UpdateAfterMovement(float dt)
    {
        foreach (var u in _world.Units)
        {
            if (u.Cast is { Phase: CastPhase.Dash } cast && u.DashTarget is { } dest)
            {
                cast.Timer -= dt;
                if (Vector2.Distance(u.Position, dest) <= 0.05f || cast.Timer <= 0)
                {
                    u.DashTarget = null;
                    u.Velocity = Vector2.Zero;
                    ApplyArea(u.Id, u.Team, cast.Definition, u.Position); // 도착 효과
                    Finish(u, cast);
                }
            }
        }

        UpdateLineProjectiles(dt);
        UpdateHomingProjectiles(dt);
    }

    private void UpdateCast(Unit u, ActiveCast cast, float dt)
    {
        var def = cast.Definition;
        Unit? target = null;
        if (def.Targeting == AbilityTargeting.Unit && cast.Phase is CastPhase.Approach or CastPhase.Windup)
        {
            target = _world.Find(cast.TargetUnit);
            if (target == null || !IsValidUnitTarget(u, target, def))
            {
                Cancel(u, emit: true); // 대상이 죽었거나 시야 밖으로 나감
                return;
            }
        }

        switch (cast.Phase)
        {
            case CastPhase.Approach:
                if (InCastRange(u, cast, target))
                {
                    EnterWindup(u, cast, credit: 0f);
                    return;
                }

                cast.RepathTimer -= dt;
                if ((cast.RepathTimer <= 0 || !u.IsMoving) && _world.TryMoveUnitBudgeted(u, target?.Position ?? cast.Point))
                {
                    cast.RepathTimer = ApproachRepathInterval;
                }

                return;

            case CastPhase.Windup:
                cast.Timer -= dt;
                if (cast.Timer <= TimeEpsilon) // 0.2초 = 6틱이 부동소수 오차로 7틱이 되지 않게
                {
                    Release(u, cast, target);
                }

                return;

            case CastPhase.Channel:
                cast.TickTimer -= dt;
                if (cast.TickTimer <= 0)
                {
                    ApplyArea(u.Id, u.Team, def, u.Position);
                    cast.TickTimer += def.ChannelInterval;
                }

                cast.Timer -= dt;
                if (cast.Timer <= 0)
                {
                    Finish(u, cast);
                }

                return;
        }
    }

    private void EnterWindup(Unit u, ActiveCast cast, float credit)
    {
        if (u.IsMoving)
        {
            u.Stop();
        }

        cast.Phase = CastPhase.Windup;
        cast.Timer = cast.Definition.CastTime - credit;
        LastWindupCredit = credit;
        var target = TargetOf(cast);
        Emit(AbilityEventType.CastStart, u, cast.Definition.Id, cast.TargetUnit, target?.Position ?? cast.Point);
    }

    /// <summary>발동: 재사용 대기 시작, 전달 방식별 처리.</summary>
    private void Release(Unit u, ActiveCast cast, Unit? target)
    {
        var def = cast.Definition;
        cast.Ability.Cooldown = def.Cooldown;
        _releases[def.Id] = _releases.GetValueOrDefault(def.Id) + 1;

        if (Scripts.Get(def.Id) is { } script && !script.OnRelease(_world, u, cast))
        {
            Finish(u, cast);
            return;
        }

        var aim = target?.Position ?? cast.Point;
        switch (def.Delivery)
        {
            case AbilityDelivery.Instant:
                var recipient = def.Targeting == AbilityTargeting.Unit ? target : u;
                if (recipient != null)
                {
                    ApplyEffect(u.Id, u.Team, recipient, def);
                }

                Finish(u, cast);
                return;

            case AbilityDelivery.Area:
                if (def.IsChanneled)
                {
                    cast.Phase = CastPhase.Channel;
                    cast.Timer = def.ChannelTime;
                    cast.TickTimer = 0; // 첫 판정은 다음 Update 에서 바로
                    Emit(AbilityEventType.ChannelStart, u, def.Id, 0, u.Position);
                    return;
                }

                var center = def.Targeting == AbilityTargeting.Point ? cast.Point : (target?.Position ?? u.Position);
                if (def.AreaDelay > 0)
                {
                    _areas.Add(new PendingArea { CasterId = u.Id, Team = u.Team, Definition = def, Center = center, Delay = def.AreaDelay });
                    Emit(AbilityEventType.AreaTelegraph, u, def.Id, 0, center);
                }
                else
                {
                    ApplyArea(u.Id, u.Team, def, center);
                }

                Finish(u, cast);
                return;

            case AbilityDelivery.Projectile:
                if (def.Homing && target != null)
                {
                    _homing.Add(new HomingProjectile { CasterId = u.Id, Team = u.Team, Definition = def, Position = u.Position, TargetId = target.Id });
                    Emit(AbilityEventType.ProjectileLaunch, u, def.Id, target.Id, target.Position);
                }
                else
                {
                    var dir = aim - u.Position;
                    dir = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : FacingOf(u);
                    _lines.Add(new LineProjectile
                    {
                        CasterId = u.Id,
                        Team = u.Team,
                        Definition = def,
                        Position = u.Position,
                        Direction = dir,
                        Remaining = def.Range,
                        PierceLeft = def.Pierce,
                    });
                    Emit(AbilityEventType.ProjectileLaunch, u, def.Id, 0, u.Position + dir * def.Range);
                }

                Finish(u, cast);
                return;

            case AbilityDelivery.Dash:
                var dest = DashDestination(u, aim, def.Range);
                u.Stop();
                u.DashTarget = dest;
                u.DashSpeed = def.DashSpeed;
                cast.Phase = CastPhase.Dash;
                cast.Timer = Vector2.Distance(u.Position, dest) / MathF.Max(1f, def.DashSpeed) + 0.5f; // 막혀도 끝나게
                Emit(AbilityEventType.DashStart, u, def.Id, 0, dest);
                return;
        }
    }

    private void Finish(Unit u, ActiveCast cast)
    {
        if (u.Cast == cast)
        {
            u.Cast = null;
            if (u.Order == UnitOrder.Cast)
            {
                Resume(u, cast);
            }
        }
    }

    /// <summary>시전 전 명령으로 돌아간다.</summary>
    private void Resume(Unit u, ActiveCast cast)
    {
        switch (cast.ResumeOrder)
        {
            case UnitOrder.Attack when _world.Find(cast.ResumeTarget) is { IsDead: false }:
                u.Order = UnitOrder.Attack;
                u.AttackTargetId = cast.ResumeTarget;
                u.ChaseTimer = 0;
                break;

            case UnitOrder.AttackMove:
                u.Order = UnitOrder.AttackMove;
                u.Goal = cast.ResumeGoal;
                u.ChaseTimer = 0;
                break;

            case UnitOrder.Move:
                u.Order = UnitOrder.Move;
                _world.MoveUnit(u, cast.ResumeGoal);
                break;

            default:
                u.Order = UnitOrder.Idle;
                u.LeashAnchor = u.Position;
                break;
        }
    }

    // ── 전달 ──────────────────────────────────────────────

    private void UpdateLineProjectiles(float dt)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var p = _lines[i];
            var def = p.Definition;
            var travel = MathF.Min(def.ProjectileSpeed * dt, p.Remaining);
            var start = p.Position;
            var end = start + p.Direction * travel;

            // 벽(맵 벽 + 맞힐 수 없는 건물·광맥)에 막히는 지점
            var wallT = 1f;
            var blocked = false;
            foreach (var wall in _world.MapWalls)
            {
                if (wall.SegmentEntry(start, end, out var t) && t < wallT)
                {
                    wallT = t;
                    blocked = true;
                }
            }

            foreach (var s in _world.Units)
            {
                if (s.IsStatic && !Affects(p.Team, p.CasterId, s, def.Affects) && s.Footprint.SegmentEntry(start, end, out var t) && t < wallT)
                {
                    wallT = t;
                    blocked = true;
                }
            }

            var segEnd = start + (end - start) * wallT;

            // 이 구간 전체를 훑어 맞은 유닛을 앞에서부터 (한 틱에 지나쳐도 맞는다)
            _hits.Clear();
            var halfWidth = def.ProjectileWidth / 2;
            var segLen = Vector2.Distance(start, segEnd);
            foreach (var u in _world.Units)
            {
                if (p.Hit.Contains(u.Id) || !Affects(p.Team, p.CasterId, u, def.Affects))
                {
                    continue;
                }

                float along;
                if (u.IsStatic)
                {
                    if (NavGrid.SegmentRectDistance(start, segEnd, u.Footprint) > halfWidth)
                    {
                        continue;
                    }

                    along = Vector2.Dot(u.Footprint.ClosestPoint(start) - start, p.Direction);
                }
                else
                {
                    if (NavGrid.PointSegmentDistance(u.Position, start, segEnd) > halfWidth + _world.Settings.UnitRadius)
                    {
                        continue;
                    }

                    along = Vector2.Dot(u.Position - start, p.Direction);
                }

                _hits.Add((Math.Clamp(along, 0, segLen), u));
            }

            _hits.Sort((a, b) => a.T.CompareTo(b.T));
            var stopped = false;
            foreach (var (along, u) in _hits)
            {
                p.Hit.Add(u.Id);
                ApplyEffect(p.CasterId, p.Team, u, def);
                if (--p.PierceLeft < 0)
                {
                    segEnd = start + p.Direction * along;
                    stopped = true;
                    break;
                }
            }

            p.Remaining -= Vector2.Distance(start, segEnd);
            p.Position = segEnd;
            if (stopped || blocked || p.Remaining <= 1e-4f)
            {
                _lines.RemoveAt(i);
                EmitAt(AbilityEventType.ProjectileEnd, p.CasterId, def.Id, 0, p.Position, p.Position);
            }
        }
    }

    private void UpdateHomingProjectiles(float dt)
    {
        for (var i = _homing.Count - 1; i >= 0; i--)
        {
            var p = _homing[i];
            var target = _world.Find(p.TargetId);
            if (target is null || target.IsDead)
            {
                _homing.RemoveAt(i);
                EmitAt(AbilityEventType.ProjectileEnd, p.CasterId, p.Definition.Id, 0, p.Position, p.Position);
                continue;
            }

            var to = target.Position - p.Position;
            var dist = to.Length();
            var step = p.Definition.ProjectileSpeed * dt;
            if (dist <= step + _world.Settings.UnitRadius)
            {
                _homing.RemoveAt(i);
                ApplyEffect(p.CasterId, p.Team, target, p.Definition);
                EmitAt(AbilityEventType.ProjectileEnd, p.CasterId, p.Definition.Id, target.Id, target.Position, target.Position);
                continue;
            }

            p.Position += to / dist * step;
        }
    }

    /// <summary>원 범위 효과. 시전자가 이미 죽었어도(지연 범위) 팀 기준으로 적용한다.</summary>
    private void ApplyArea(int casterId, byte team, AbilityDefinition def, Vector2 center)
    {
        var r = _world.Settings.UnitRadius;
        foreach (var u in _world.Units)
        {
            if (!Affects(team, casterId, u, def.Affects))
            {
                continue;
            }

            var inside = u.IsStatic
                ? Vector2.Distance(center, u.Footprint.ClosestPoint(center)) <= def.AreaRadius
                : Vector2.Distance(center, u.Position) <= def.AreaRadius + r;
            if (inside)
            {
                ApplyEffect(casterId, team, u, def);
            }
        }

        EmitAt(AbilityEventType.AreaHit, casterId, def.Id, 0, center, center);
    }

    // ── 효과 ──────────────────────────────────────────────

    private void ApplyEffect(int casterId, byte team, Unit target, AbilityDefinition def)
    {
        if (target.IsDead)
        {
            return;
        }

        var effect = def.Effect;
        var caster = _world.Find(casterId);
        var script = Scripts.Get(def.Id);
        if (script != null && caster != null)
        {
            effect = script.ModifyEffect(_world, caster, target, effect);
        }

        if (effect.Damage > 0)
        {
            _world.Combat.ApplyDamage(casterId, team, target, effect.Damage);
        }

        if (effect.Heal > 0 && !target.IsDead)
        {
            target.Hp = MathF.Min(target.Stats.MaxHp, target.Hp + effect.Heal);
        }

        if (effect.Status != StatusType.None && !target.IsDead && !target.IsStatic)
        {
            ApplyStatus(target, effect.Status, effect.StatusDuration, effect.StatusMagnitude, casterId);
        }

        EmitAt(AbilityEventType.Hit, casterId, def.Id, target.Id, target.Position, target.Position);
        if (script != null && caster != null)
        {
            script.OnHit(_world, caster, target, def);
        }
    }

    /// <summary>
    /// 상태 효과를 건다. 기절·둔화는 같은 종류가 있으면 시간·크기를 큰 쪽으로 갱신(겹치지 않음), 보호막은 따로 쌓인다.
    /// 기절하면 하던 시전·돌진·공격 선딜·이동을 멈춘다.
    /// </summary>
    public void ApplyStatus(Unit target, StatusType type, float duration, float magnitude, int sourceId)
    {
        if (type == StatusType.None || duration <= 0)
        {
            return;
        }

        var existing = type == StatusType.Shield ? null : target.Statuses.FirstOrDefault(s => s.Type == type);
        if (existing != null)
        {
            existing.Remaining = MathF.Max(existing.Remaining, duration);
            existing.Magnitude = MathF.Max(existing.Magnitude, magnitude);
        }
        else
        {
            target.Statuses.Add(new StatusInstance { Type = type, Remaining = duration, Magnitude = magnitude, SourceId = sourceId });
        }

        if (type == StatusType.Stun)
        {
            Cancel(target, emit: true);
            target.DashTarget = null;
            target.Windup = 0;
            target.Stop();
        }
    }

    /// <summary>보호막으로 피해를 흡수하고 남은 피해를 돌려준다 (전투 엔진의 피해 처리가 부른다).</summary>
    public float Absorb(Unit target, float damage)
    {
        foreach (var s in target.Statuses)
        {
            if (s.Type != StatusType.Shield || s.Magnitude <= 0)
            {
                continue;
            }

            var taken = MathF.Min(s.Magnitude, damage);
            s.Magnitude -= taken;
            damage -= taken;
            if (damage <= 0)
            {
                return 0;
            }
        }

        return damage;
    }

    public float ShieldOf(Unit u)
    {
        var total = 0f;
        foreach (var s in u.Statuses)
        {
            if (s.Type == StatusType.Shield)
            {
                total += s.Magnitude;
            }
        }

        return total;
    }

    private static void UpdateStatuses(Unit u, float dt)
    {
        for (var i = u.Statuses.Count - 1; i >= 0; i--)
        {
            var s = u.Statuses[i];
            s.Remaining -= dt;
            if (s.Remaining <= 0 || (s.Type == StatusType.Shield && s.Magnitude <= 0))
            {
                u.Statuses.RemoveAt(i);
            }
        }
    }

    // ── AI ───────────────────────────────────────────────

    /// <summary>
    /// 행동 층 AI 용: 준비된 능력 중 데이터의 AI 힌트(<see cref="AbilityAiHint"/>)에 맞는 상황이면 하나를 쓴다.
    /// 적은 시전자 팀에 보이는 유닛만 본다 (안개를 지킨다).
    /// </summary>
    /// <returns>시전을 시작했으면 true.</returns>
    public bool TryAiCast(Unit u)
    {
        if (u.Cast != null || u.IsStunned || u.IsDashing || u.Windup > 0)
        {
            return false;
        }

        foreach (var ability in u.Abilities)
        {
            if (!ability.IsReady)
            {
                continue;
            }

            var def = ability.Definition;
            switch (def.AiHint)
            {
                case AbilityAiHint.OnTarget:
                    if (_world.Find(u.AttackTargetId) is { } target && _world.Combat.IsValidTarget(u, target) && _world.CanSee(u.Team, target)
                        && Vector2.Distance(u.Position, target.Position) <= def.Range * 0.9f
                        && TryBegin(u, def.Id, target.Id, target.Position))
                    {
                        return true;
                    }

                    break;

                case AbilityAiHint.EnemyCluster:
                    if (FindEnemyCluster(u, def, out var center) && TryBegin(u, def.Id, 0, center))
                    {
                        return true;
                    }

                    break;

                case AbilityAiHint.SelfLowHealth:
                    if (u.Hp / u.Stats.MaxHp < def.AiThreshold && u.AttackTargetId != 0 && TryBegin(u, def.Id, 0, u.Position))
                    {
                        return true;
                    }

                    break;

                case AbilityAiHint.AlliesHurt:
                    var hurt = _world.Units.Count(a => a.Team == u.Team && !a.IsDead && !a.IsStatic
                                                       && a.Hp / a.Stats.MaxHp < def.AiThreshold
                                                       && Vector2.Distance(a.Position, u.Position) <= def.AreaRadius);
                    if (hurt >= def.AiMinCount && TryBegin(u, def.Id, 0, u.Position))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// 범위 안에 보이는 적이 AiMinCount 명 이상 모인 곳. 대상 없음(자기 주변) 능력은 자기 위치만 본다.
    /// 지점 능력은 사거리 안의 적 위치마다 그 주변 적 수를 세어 가장 많은 곳.
    /// </summary>
    private bool FindEnemyCluster(Unit u, AbilityDefinition def, out Vector2 center)
    {
        center = u.Position;
        var enemies = _world.Units.Where(e => _world.Combat.IsValidTarget(u, e) && !e.IsStatic && _world.CanSee(u.Team, e)).ToList();
        if (enemies.Count < def.AiMinCount)
        {
            return false;
        }

        var radius = def.AreaRadius + _world.Settings.UnitRadius;
        if (def.Targeting == AbilityTargeting.None)
        {
            return enemies.Count(e => Vector2.Distance(e.Position, u.Position) <= radius) >= def.AiMinCount;
        }

        var best = 0;
        foreach (var c in enemies)
        {
            if (Vector2.Distance(c.Position, u.Position) > def.Range + radius)
            {
                continue;
            }

            var n = enemies.Count(e => Vector2.Distance(e.Position, c.Position) <= radius);
            if (n > best)
            {
                best = n;
                center = c.Position;
            }
        }

        return best >= def.AiMinCount;
    }

    // ── 도우미 ───────────────────────────────────────────

    /// <summary>플레이어 상태 메시지용: 이 플레이어 유닛의 재사용 대기 중인 능력.</summary>
    public List<AbilityCooldown> CooldownsOf(byte owner)
    {
        var result = new List<AbilityCooldown>();
        foreach (var u in _world.Units)
        {
            if (u.Owner != owner || u.IsBot)
            {
                continue;
            }

            foreach (var a in u.Abilities)
            {
                if (!a.IsReady)
                {
                    result.Add(new AbilityCooldown { UnitId = u.Id, Ability = a.Id, Remaining = a.Cooldown });
                }
            }
        }

        return result;
    }

    private Unit? TargetOf(ActiveCast cast) => cast.TargetUnit != 0 ? _world.Find(cast.TargetUnit) : null;

    private bool InCastRange(Unit u, ActiveCast cast, Unit? target)
    {
        var def = cast.Definition;
        switch (def.Targeting)
        {
            case AbilityTargeting.None:
                return true;

            case AbilityTargeting.Unit:
                return target != null && _world.Combat.EdgeDistance(u, target) <= def.Range;

            default:
                // 직선 투사체·돌진은 방향만 쓰므로 다가가지 않는다. 지점 범위만 사거리 확인.
                return def.Delivery != AbilityDelivery.Area || Vector2.Distance(u.Position, cast.Point) - _world.Settings.UnitRadius <= def.Range;
        }
    }

    private bool IsValidUnitTarget(Unit caster, Unit target, AbilityDefinition def) =>
        !target.IsDead && _world.CanSee(caster.Team, target) && Affects(caster.Team, caster.Id, target, def.Affects);

    /// <summary>효과 대상인지: 적(팀이 다르고 둘 다 중립 아님) / 아군(같은 팀, 자신 제외) / 자신.</summary>
    private static bool Affects(byte team, int casterId, Unit u, AbilityAffects affects)
    {
        if (u.IsDead || u.Kind == UnitKind.OreField)
        {
            return false;
        }

        if (u.Id == casterId)
        {
            return (affects & AbilityAffects.Self) != 0;
        }

        if ((affects & AbilityAffects.Enemies) != 0 && UnitArchetypes.AreEnemies(team, u.Team))
        {
            return true;
        }

        return (affects & AbilityAffects.Allies) != 0 && u.Team == team && team != UnitArchetypes.NeutralTeam;
    }

    /// <summary>돌진 도착점: 사거리로 자르고, 벽에 막히면 막히기 직전까지.</summary>
    private Vector2 DashDestination(Unit u, Vector2 aim, float range)
    {
        var to = aim - u.Position;
        var dist = to.Length();
        var dest = dist > range && dist > 1e-6f ? u.Position + to / dist * range : aim;
        var r = _world.Settings.UnitRadius;
        if (!_world.Nav.HasLineOfSight(u.Position, dest, r))
        {
            var lo = 0f;
            var hi = 1f;
            for (var i = 0; i < 12; i++)
            {
                var mid = (lo + hi) / 2;
                if (_world.Nav.HasLineOfSight(u.Position, Vector2.Lerp(u.Position, dest, mid), r))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            dest = Vector2.Lerp(u.Position, dest, lo);
        }

        return _world.Standable(dest);
    }

    private static Vector2 FacingOf(Unit u) =>
        u.Velocity.LengthSquared() > 1e-6f ? Vector2.Normalize(u.Velocity) : Vector2.UnitX;

    private void Emit(AbilityEventType type, Unit caster, AbilityId id, int target, Vector2 point) =>
        EmitAt(type, caster.Id, id, target, point, caster.Position);

    private void EmitAt(AbilityEventType type, int casterId, AbilityId id, int target, Vector2 point, Vector2 fallbackCasterPos)
    {
        _events.Add(new AbilityEvent { Tick = _world.CurrentTick, Type = type, Caster = casterId, Ability = id, Target = target, Point = point });
        var casterPos = _world.Find(casterId)?.Position ?? fallbackCasterPos;
        _eventPositions.Add((casterPos, point));
    }
}
