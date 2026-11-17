using System.Numerics;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;

namespace OpenRTS.Server.Simulation.Combat;

/// <summary>
/// 행동 층 AI: 전투 봇의 상위 판단 (참고 서버의 IAIScript, 특히 LaneMinionAI 의 재평가 규칙을 참고해 새로 작성).
/// 실행(추적·공격)은 <see cref="CombatSystem"/> 이 하고, 여기서는 명령만 바꾼다.
/// <list type="bullet">
/// <item>0.25초마다(유닛별로 시점을 흩어서) 다시 판단한다.</item>
/// <item>대상을 4초 동안 한 번도 공격하지 못했으면(막혀서 닿지 못함) 0.5초 동안 그 대상을 무시하고 다른 적을 찾는다.</item>
/// <item>싸울 상대가 근처에 없으면 <b>우리 팀에 보이는</b> 가장 가까운 적에게 공격 이동한다 (안개 속 적은 모른다).</item>
/// <item>보이는 적이 없으면 상대 진영으로 진군하고, 거기에도 없으면 팀이 가장 오래 못 본 구역부터 나눠서 정찰한다. 적이 다 죽으면 멈춘다.</item>
/// </list>
/// </summary>
public sealed class BattleAi
{
    public const float ThinkInterval = 0.25f;
    public const float GiveUpTime = 4f;
    public const float IgnoreTime = 0.5f;

    /// <summary>공격 이동 목표(가장 가까운 적)가 이만큼 바뀌면 목표를 갱신한다.</summary>
    public const float RetargetGoalDistance = 3f;

    private readonly World _world;

    public BattleAi(World world)
    {
        _world = world;
    }

    public void Update(float dt)
    {
        UpdateExploration(dt);
        foreach (var u in _world.Units)
        {
            if (!u.HasBrain || u.IsDead)
            {
                continue;
            }

            if (u.IgnoreTimer > 0)
            {
                u.IgnoreTimer -= dt;
                if (u.IgnoreTimer <= 0)
                {
                    u.IgnoreTargetId = 0;
                }
            }

            u.ThinkTimer -= dt;
            if (u.ThinkTimer > 0)
            {
                continue;
            }

            u.ThinkTimer += ThinkInterval;
            Think(u);
        }
    }

    /// <summary>처음 판단 시점을 ID 로 흩어 놓는다 (모든 봇이 같은 틱에 몰려 판단하지 않게).</summary>
    public static float InitialThinkDelay(int unitId) => unitId * 0.618034f % 1f * ThinkInterval;

    private void Think(Unit u)
    {
        if (u.Windup > 0 || u.Cast != null || u.IsStunned || u.IsDashing)
        {
            return;
        }

        // 능력: 데이터의 AI 힌트에 맞는 상황이면 먼저 쓴다 (싸우는 중에도).
        if (_world.AbilitySystem.TryAiCast(u))
        {
            return;
        }

        // 닿지 못하는 대상에 매달리지 않는다.
        if (u.AttackTargetId != 0 && u.TimeSinceAttack > GiveUpTime)
        {
            u.IgnoreTargetId = u.AttackTargetId;
            u.IgnoreTimer = IgnoreTime;
            u.AttackTargetId = 0;
            u.TimeSinceAttack = 0;
        }

        if (u.AttackTargetId != 0)
        {
            return; // 싸우는 중이면 엔진에 맡긴다
        }

        var nearest = NearestEnemy(u, out var anyEnemyAlive);
        if (!anyEnemyAlive)
        {
            if (u.Order == UnitOrder.AttackMove)
            {
                u.Order = UnitOrder.Idle;
                u.Stop();
            }

            return;
        }

        var goal = nearest?.Position ?? SearchGoal(u);
        if (u.Order != UnitOrder.AttackMove || Vector2.Distance(u.Goal, goal) > RetargetGoalDistance)
        {
            u.Order = UnitOrder.AttackMove;
            u.Goal = goal;
            u.ChaseTimer = 0;
        }
    }

    /// <summary>적이 안 보일 때 갈 곳: 먼저 상대 진영, 도착했으면 유닛·시각마다 다른 정찰 지점.</summary>
    private Vector2 SearchGoal(Unit u)
    {
        var enemyHome = new Vector2(u.Team == 1 ? EnemyHomeX : -EnemyHomeX, 0);
        if (Vector2.Distance(u.Position, enemyHome) > SearchArrivedDistance && !u.Searched)
        {
            return enemyHome;
        }

        u.Searched = true; // 상대 진영은 이미 봤다 — 이후로는 정찰

        // 1) 팀이 마지막으로 본 적 위치가 있으면 가장 가까운 곳부터 확인한다 (사람이 하듯이).
        var known = _lastKnown[u.Team];
        if (known.Count > 0)
        {
            var nearest = known.Values.OrderBy(k => Vector2.DistanceSquared(k, u.Position)).First();
            return nearest;
        }

        // 2) 정찰: 팀이 가장 오래 못 본 구역으로. 가까운 곳을 조금 더 좋아하고, 유닛마다 다른 흔들림을 줘서 흩어진다.
        var seen = _lastSeen[u.Team];
        var best = 0;
        var bestScore = float.MinValue;
        for (var c = 0; c < seen.Length; c++)
        {
            var center = ScoutCellCenter(c);
            var score = (_time - seen[c]) - 0.08f * Vector2.Distance(u.Position, center) + Jitter(u.Id, c) * 4f;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return ScoutCellCenter(best);
    }

    /// <summary>정찰 구역: 맵을 <see cref="ScoutGrid"/>×<see cref="ScoutGrid"/> 로 나눈 칸.</summary>
    public const int ScoutGrid = 8;

    /// <summary>팀별로 각 정찰 구역을 마지막으로 본 시각 (팀 시야로 갱신, 0.5초마다).</summary>
    private readonly float[][] _lastSeen = { Array.Empty<float>(), new float[ScoutGrid * ScoutGrid], new float[ScoutGrid * ScoutGrid] };
    private float _time;
    private float _exploreTimer;

    /// <summary>팀별로, 본 적이 있는 적 유닛의 마지막 위치 (적 ID → 위치). 가서 없으면 지운다.</summary>
    private readonly Dictionary<int, Vector2>[] _lastKnown = { new(), new(), new() };

    /// <summary>마지막 위치에 이만큼 다가갔는데 그 적이 안 보이면 기억을 지운다.</summary>
    public const float LastKnownClearDistance = 4f;

    private static Vector2 ScoutCellCenter(int cell)
    {
        var size = SimConstants.MapHalfSize * 2 / ScoutGrid;
        return new Vector2(-SimConstants.MapHalfSize + (cell % ScoutGrid + 0.5f) * size, -SimConstants.MapHalfSize + (cell / ScoutGrid + 0.5f) * size);
    }

    /// <summary>유닛·구역마다 고정된 0~1 값 (같은 구역에 몰리지 않게).</summary>
    private static float Jitter(int unitId, int cell)
    {
        var h = (uint)(unitId * 73856093) ^ (uint)(cell * 19349663);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xFFFF) / 65535f;
    }

    /// <summary>팀 시야로 정찰 구역의 "마지막으로 본 시각"을 갱신한다.</summary>
    private void UpdateExploration(float dt)
    {
        _time += dt;
        _exploreTimer -= dt;
        if (_exploreTimer > 0)
        {
            return;
        }

        _exploreTimer = 0.5f;
        for (var team = 1; team < _lastSeen.Length; team++)
        {
            var seen = _lastSeen[team];
            for (var c = 0; c < seen.Length; c++)
            {
                if (_world.CanSee((byte)team, ScoutCellCenter(c)))
                {
                    seen[c] = _time;
                }
            }

            // 적의 마지막 위치: 보이는 적은 갱신, 죽은 적은 지우고, 우리 유닛이 그 자리까지 가 봤는데 없으면 지운다.
            var known = _lastKnown[team];
            foreach (var enemy in _world.Units)
            {
                if (enemy.Team != team && enemy.Team != UnitArchetypes.NeutralTeam && !enemy.IsDead && _world.CanSee((byte)team, enemy))
                {
                    known[enemy.Id] = enemy.Position;
                }
            }

            _stale.Clear();
            foreach (var (id, pos) in known)
            {
                var enemy = _world.Find(id);
                if (enemy is null || enemy.IsDead)
                {
                    _stale.Add(id);
                    continue;
                }

                if (!_world.CanSee((byte)team, enemy) && _world.Units.Any(a => a.Team == team && !a.IsDead && Vector2.Distance(a.Position, pos) <= LastKnownClearDistance))
                {
                    _stale.Add(id); // 가 봤는데 없다
                }
            }

            foreach (var id in _stale)
            {
                known.Remove(id);
            }
        }
    }

    private readonly List<int> _stale = new();

    /// <summary>시나리오를 다시 시작할 때 정찰 기억을 지운다.</summary>
    public void Reset()
    {
        _time = 0;
        _exploreTimer = 0;
        for (var team = 1; team < _lastSeen.Length; team++)
        {
            Array.Clear(_lastSeen[team]);
            _lastKnown[team].Clear();
        }
    }

    /// <summary>상대 진영 x 좌표 (Battle 배치 기준).</summary>
    public const float EnemyHomeX = 30f;
    public const float SearchArrivedDistance = 6f;

    private Unit? NearestEnemy(Unit u, out bool anyEnemyAlive)
    {
        Unit? best = null;
        var bestDist = float.MaxValue;
        anyEnemyAlive = false;
        foreach (var other in _world.Units)
        {
            if (!_world.Combat.IsValidTarget(u, other))
            {
                continue;
            }

            anyEnemyAlive = true; // "아직 싸움이 끝나지 않았다" 만 안다 (위치는 모름)
            if (other.Id == u.IgnoreTargetId || !_world.CanSee(u.Team, other))
            {
                continue;
            }

            var d = Vector2.DistanceSquared(u.Position, other.Position);
            if (d < bestDist)
            {
                best = other;
                bestDist = d;
            }
        }

        return best;
    }
}
