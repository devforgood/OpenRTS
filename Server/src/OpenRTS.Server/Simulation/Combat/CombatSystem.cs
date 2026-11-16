using System.Numerics;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Simulation.Combat;

/// <summary>유닛이 지금 따르는 명령. 플레이어 명령이나 AI(행동 층)가 정하고, 이 엔진이 실행한다.</summary>
public enum UnitOrder : byte
{
    /// <summary>대기. 획득 거리 안에 적이 오면 스스로 싸우고, 너무 멀리 쫓아가면(리쉬) 제자리로 돌아온다.</summary>
    Idle,

    /// <summary>이동만. 가는 동안 적을 무시한다. 도착하면 Idle.</summary>
    Move,

    /// <summary>목표 지점으로 가면서 만나는 적과 싸운다. 도착하면 Idle.</summary>
    AttackMove,

    /// <summary>지정한 적을 쫓아가 공격한다. 대상이 죽으면 Idle.</summary>
    Attack,

    /// <summary>일꾼: 광맥과 본진을 오가며 자원을 나른다 (경제 시스템이 실행, 적은 무시).</summary>
    Gather,

    /// <summary>일꾼: 건물을 짓는다 (경제 시스템이 실행, 적은 무시).</summary>
    Build,
}

/// <summary>
/// 전투 엔진 층 (참고 서버의 ObjAIBase.UpdateTarget 에 해당하는 역할, 코드는 새로 작성).
/// 유닛의 명령(<see cref="UnitOrder"/>)과 대상을 보고 추적·선딜·타격·투사체·사망을 처리한다.
/// "누구를 칠지, 어디로 갈지" 같은 상위 판단은 <see cref="BattleAi"/>(행동 층)가 명령으로 내려 준다.
///
/// 참고 서버와 다르게 한 것 (ai.md 5장):
/// <list type="bullet">
/// <item>적 탐색·도움 요청은 전체 순회가 아니라 공간 격자로 찾는다.</item>
/// <item>우선순위가 같으면 순회 순서가 아니라 거리로 고른다.</item>
/// <item>틱 순서가 판단 → 이동이라 명령이 같은 틱에 반영된다.</item>
/// <item>추적 경로는 매 틱이 아니라 대상이 일정 거리 이상 움직였을 때만 다시 찾는다.</item>
/// </list>
/// </summary>
public sealed class CombatSystem
{
    /// <summary>추적 중 경로를 다시 찾는 최소 간격(초)과, 그때 대상이 이만큼 움직였어야 한다.</summary>
    public const float ChaseRepathInterval = 0.25f;
    public const float ChaseRepathDistance = 0.5f;

    /// <summary>대기 중 스스로 싸우기 시작한 유닛이 제자리에서 이만큼 멀어지면 포기하고 돌아간다.</summary>
    public const float LeashRange = 10f;

    /// <summary>맞은 유닛 주변 이 거리 안의 한가한 아군이 공격자를 노린다.</summary>
    public const float CallForHelpRange = 8f;

    /// <summary>선딜 중 대상이 사거리 + 이만큼 밖으로 나가면 공격을 취소한다.</summary>
    public const float WindupCancelSlack = 1f;

    /// <summary>적 탐색 격자 셀 크기 (획득 거리 정도).</summary>
    private const float GridCell = 10f;

    /// <summary>가장 큰 건물 발판 반폭의 대각선 (격자 탐색 여유).</summary>
    private const float MaxFootprint = 3f;

    private readonly World _world;
    private readonly SpatialGrid _grid = new();
    private readonly List<Vector2> _positions = new();
    private readonly List<Unit> _indexed = new();
    private readonly List<int> _query = new();
    private readonly List<Projectile> _projectiles = new();
    private readonly List<CombatEvent> _events = new();
    private readonly List<(Vector2 Attacker, Vector2 Target)> _eventPositions = new();

    private struct Projectile
    {
        public int AttackerId;
        public byte Team;
        public int TargetId;
        public Vector2 Position;
        public float Speed;
        public float Damage;
    }

    public CombatSystem(World world)
    {
        _world = world;
    }

    /// <summary>시나리오 시작 후 죽은 유닛 수.</summary>
    public int Deaths { get; private set; }

    public int ProjectileCount => _projectiles.Count;

    /// <summary>쌓인 전투 이벤트. 서버가 스냅샷과 함께 보내고 <see cref="ClearEvents"/> 한다.</summary>
    public IReadOnlyList<CombatEvent> Events => _events;

    /// <summary><see cref="Events"/> 와 같은 순서의, 이벤트 순간 공격자·대상 위치 (시야 필터용).</summary>
    public IReadOnlyList<(Vector2 Attacker, Vector2 Target)> EventPositions => _eventPositions;

    public void ClearEvents()
    {
        _events.Clear();
        _eventPositions.Clear();
    }

    public void Reset()
    {
        _projectiles.Clear();
        ClearEvents();
        Deaths = 0;
    }

    private float Radius => _world.Settings.UnitRadius;

    /// <summary>틱 시작: 전투하는(중립이 아닌, 살아 있는) 유닛으로 탐색 격자를 만든다.</summary>
    public void BeginTick()
    {
        _positions.Clear();
        _indexed.Clear();
        foreach (var u in _world.Units)
        {
            if (u.Team != UnitArchetypes.NeutralTeam && !u.IsDead)
            {
                _positions.Add(u.Position);
                _indexed.Add(u);
            }
        }

        _grid.Build(_positions, GridCell);
    }

    /// <summary>
    /// 두 유닛 가장자리 사이 거리. 건물·자원은 발판 사각형까지의 거리로 잰다.
    /// </summary>
    public float EdgeDistance(Unit a, Unit b)
    {
        if (b.IsStatic)
        {
            return Vector2.Distance(a.Position, b.Footprint.ClosestPoint(a.Position)) - (a.IsStatic ? 0 : Radius);
        }

        if (a.IsStatic)
        {
            return Vector2.Distance(b.Position, a.Footprint.ClosestPoint(b.Position)) - Radius;
        }

        return Vector2.Distance(a.Position, b.Position) - 2 * Radius;
    }

    public bool IsValidTarget(Unit self, Unit? target) =>
        target is { IsDead: false } && UnitArchetypes.AreEnemies(self.Team, target.Team);

    /// <summary>
    /// <paramref name="range"/>(가장자리 거리) 안의 적 중 하나를 고른다.
    /// 우선순위: 내 편을 공격 중인 적 → 그 밖의 적, 같으면 가까운 순.
    /// </summary>
    public Unit? FindTarget(Unit self, float range, int ignoreId = 0)
    {
        Unit? best = null;
        var bestPriority = int.MaxValue;
        var bestDist = float.MaxValue;
        if (_indexed.Count == 0)
        {
            return null; // 아직 BeginTick 전
        }

        _query.Clear();
        _grid.Query(self.Position, range + 2 * Radius + MaxFootprint, _query);
        foreach (var i in _query)
        {
            var other = _indexed[i];
            if (other.Id == ignoreId || !IsValidTarget(self, other))
            {
                continue;
            }

            var dist = EdgeDistance(self, other);
            if (dist > range)
            {
                continue;
            }

            // 우선순위: 내 편을 공격 중인 적 → 공격할 수 있는 유닛 → 건물
            var victim = other.AttackTargetId != 0 ? _world.Find(other.AttackTargetId) : null;
            var priority = victim != null && victim.Team == self.Team ? 0 : other.Stats.CanAttack ? 1 : 2;
            if (priority < bestPriority || (priority == bestPriority && dist < bestDist))
            {
                best = other;
                bestPriority = priority;
                bestDist = dist;
            }
        }

        return best;
    }

    /// <summary>엔진: 명령과 대상에 따라 추적·공격을 진행한다 (이동 계산 전에 호출).</summary>
    public void UpdateUnits(float dt)
    {
        foreach (var u in _world.Units)
        {
            if (u.Team == UnitArchetypes.NeutralTeam || u.IsDead || !u.Stats.CanAttack)
            {
                continue; // 중립·건물은 싸우지 않는다
            }

            u.Cooldown = MathF.Max(0, u.Cooldown - dt);
            u.TimeSinceAttack += dt;

            if (u.Windup > 0)
            {
                UpdateWindup(u, dt);
                continue; // 선딜 중에는 움직이지 않는다
            }

            var target = u.AttackTargetId != 0 ? _world.Find(u.AttackTargetId) : null;
            if (!IsValidTarget(u, target))
            {
                target = null;
                u.AttackTargetId = 0;
            }

            switch (u.Order)
            {
                case UnitOrder.Gather:
                case UnitOrder.Build:
                    continue; // 일하는 중에는 적을 무시 (경제 시스템이 움직임을 맡는다)

                case UnitOrder.Move:
                    if (!u.IsMoving)
                    {
                        BecomeIdle(u);
                    }

                    continue; // 이동 중에는 적을 무시

                case UnitOrder.Attack:
                    if (target == null)
                    {
                        BecomeIdle(u);
                        continue;
                    }

                    break;

                case UnitOrder.AttackMove:
                    target ??= Acquire(u, ignoreId: u.IgnoreTargetId);
                    if (target == null)
                    {
                        // 싸울 상대가 없으면 목표 지점으로 (추적하느라 경로가 바뀌었을 수 있다).
                        if (!u.IsMoving && Vector2.Distance(u.Position, u.Goal) <= Radius * World.NearTargetRadii)
                        {
                            BecomeIdle(u); // 도착 (또는 목표 근처에서 막힘)
                            continue;
                        }

                        var headingToGoal = u.Target is { } t && Vector2.DistanceSquared(t, u.Goal) < 1f;
                        u.ChaseTimer -= dt;
                        if ((!u.IsMoving || !headingToGoal) && u.ChaseTimer <= 0 && _world.TryMoveUnitBudgeted(u, u.Goal))
                        {
                            u.ChaseTimer = 1f; // 막혀 멈췄을 때 매 틱 다시 찾지 않게
                        }

                        continue;
                    }


                    break;

                default: // Idle
                    if (target == null)
                    {
                        target = Acquire(u, ignoreId: 0);
                        if (target == null)
                        {
                            continue;
                        }

                        u.LeashAnchor = u.Position;
                    }

                    // 리쉬: 대기 자리에서 너무 멀어지면 포기하고 돌아간다 (적을 무시하며).
                    if (Vector2.Distance(u.Position, u.LeashAnchor) > LeashRange)
                    {
                        u.AttackTargetId = 0;
                        u.Order = UnitOrder.Move;
                        _world.MoveUnit(u, u.LeashAnchor);
                        continue;
                    }

                    break;
            }

            Engage(u, target, dt);
        }
    }

    private Unit? Acquire(Unit u, int ignoreId)
    {
        if (u.HoldFire)
        {
            return null;
        }

        var target = FindTarget(u, u.Stats.AcquisitionRange, ignoreId);
        if (target != null)
        {
            SetTarget(u, target);
        }

        return target;
    }

    private static void SetTarget(Unit u, Unit target)
    {
        if (u.AttackTargetId != target.Id)
        {
            u.AttackTargetId = target.Id;
            u.TimeSinceAttack = 0;
            u.ChaseTimer = 0;
        }
    }

    private void BecomeIdle(Unit u)
    {
        u.Order = UnitOrder.Idle;
        u.AttackTargetId = 0;
        u.LeashAnchor = u.Position;
    }

    private void Engage(Unit u, Unit target, float dt)
    {
        var stats = u.Stats;
        if (EdgeDistance(u, target) <= stats.Range)
        {
            if (u.IsMoving)
            {
                u.Stop();
            }

            if (u.Cooldown <= 0)
            {
                u.Windup = stats.Windup;
                u.WindupTargetId = target.Id;
                u.Cooldown = stats.Cooldown;
                u.TimeSinceAttack = 0;
            }

            return;
        }

        // 추적: 대상이 충분히 움직였거나 멈춰 버렸을 때만 경로를 다시 찾는다.
        u.ChaseTimer -= dt;
        if (u.ChaseTimer <= 0 && (!u.IsMoving || Vector2.Distance(u.ChasePoint, target.Position) > ChaseRepathDistance)
            && _world.TryMoveUnitBudgeted(u, target.Position))
        {
            u.ChasePoint = target.Position;
            u.ChaseTimer = ChaseRepathInterval;
        }
    }

    private void UpdateWindup(Unit u, float dt)
    {
        var target = _world.Find(u.WindupTargetId);
        if (!IsValidTarget(u, target) || EdgeDistance(u, target!) > u.Stats.Range + WindupCancelSlack)
        {
            u.Windup = 0; // 취소 (쿨다운은 이미 썼다)
            return;
        }

        u.Windup -= dt;
        if (u.Windup > 0)
        {
            return;
        }

        u.Windup = 0;
        var stats = u.Stats;
        if (stats.IsRanged)
        {
            _projectiles.Add(new Projectile
            {
                AttackerId = u.Id,
                Team = u.Team,
                TargetId = target!.Id,
                Position = u.Position,
                Speed = stats.ProjectileSpeed,
                Damage = stats.Damage,
            });
            Emit(CombatEventType.ProjectileLaunch, u.Id, target.Id);
        }
        else
        {
            Emit(CombatEventType.MeleeHit, u.Id, target!.Id);
            ApplyDamage(u.Id, u.Team, target, stats.Damage);
        }
    }

    /// <summary>투사체 이동과 명중 (이동 계산 뒤에 호출해 이번 틱 위치로 맞춘다). 대상이 죽으면 투사체는 사라진다.</summary>
    public void UpdateProjectiles(float dt)
    {
        for (var i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            var target = _world.Find(p.TargetId);
            if (target is null || target.IsDead)
            {
                _projectiles.RemoveAt(i);
                continue;
            }

            var aim = target.IsStatic ? target.Footprint.ClosestPoint(p.Position) : target.Position;
            var to = aim - p.Position;
            var dist = to.Length();
            var step = p.Speed * dt;
            if (dist <= step + (target.IsStatic ? 0.05f : Radius))
            {
                _projectiles.RemoveAt(i);
                ApplyDamage(p.AttackerId, p.Team, target, p.Damage);
                continue;
            }

            p.Position += to / dist * step;
            _projectiles[i] = p;
        }
    }

    public void ApplyDamage(int attackerId, byte attackerTeam, Unit target, float damage)
    {
        if (target.IsDead)
        {
            return;
        }

        target.Hp -= damage;
        if (target.Hp <= 0)
        {
            target.Hp = 0;
            Deaths++;
            Emit(CombatEventType.Death, attackerId, target.Id);
            return;
        }

        var attacker = _world.Find(attackerId);
        if (attacker is { IsDead: false } && attacker.Team == attackerTeam)
        {
            CallForHelp(attacker, target);
        }
    }

    /// <summary>맞은 유닛과 주변의 한가한 아군이 공격자를 노린다 (공간 격자로 찾는다).</summary>
    private void CallForHelp(Unit attacker, Unit victim)
    {
        _query.Clear();
        _grid.Query(victim.Position, CallForHelpRange, _query);
        foreach (var i in _query)
        {
            var ally = _indexed[i];
            if (ally.IsDead || ally.HoldFire || !ally.Stats.CanAttack || ally.Team != victim.Team || ally.AttackTargetId != 0 || ally.Windup > 0)

            {
                continue;
            }

            if (ally.Order is not (UnitOrder.Idle or UnitOrder.AttackMove))
            {
                continue; // 이동·지정 공격 명령 중이면 따르지 않는다
            }

            if (Vector2.Distance(ally.Position, victim.Position) > CallForHelpRange)
            {
                continue;
            }

            if (ally.Order == UnitOrder.Idle)
            {
                ally.LeashAnchor = ally.Position;
            }

            SetTarget(ally, attacker);
        }
    }

    /// <summary>죽은 유닛 목록 (World 가 지운다).</summary>
    public void CollectDead(List<int> output)
    {
        output.Clear();
        foreach (var u in _world.Units)
        {
            if (u.IsDead)
            {
                output.Add(u.Id);
            }
        }
    }

    private void Emit(CombatEventType type, int attacker, int target)
    {
        _events.Add(new CombatEvent { Tick = _world.CurrentTick, Type = type, Attacker = attacker, Target = target });
        var a = _world.Find(attacker);
        var t = _world.Find(target);
        var tp = t?.Position ?? a?.Position ?? Vector2.Zero;
        _eventPositions.Add((a?.Position ?? tp, tp));
    }

}
