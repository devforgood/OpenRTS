using System.Numerics;
using OpenRTS.Shared;

namespace OpenRTS.Server.Simulation.Combat;

/// <summary>
/// 행동 층 AI: 전투 봇의 상위 판단 (참고 서버의 IAIScript, 특히 LaneMinionAI 의 재평가 규칙을 참고해 새로 작성).
/// 실행(추적·공격)은 <see cref="CombatSystem"/> 이 하고, 여기서는 명령만 바꾼다.
/// <list type="bullet">
/// <item>0.25초마다(유닛별로 시점을 흩어서) 다시 판단한다.</item>
/// <item>대상을 4초 동안 한 번도 공격하지 못했으면(막혀서 닿지 못함) 0.5초 동안 그 대상을 무시하고 다른 적을 찾는다.</item>
/// <item>싸울 상대가 근처에 없으면 <b>우리 팀에 보이는</b> 가장 가까운 적에게 공격 이동한다 (안개 속 적은 모른다).</item>
/// <item>보이는 적이 없으면 상대 진영으로 진군하고, 거기에도 없으면 맵 안을 정찰한다. 적이 다 죽으면 멈춘다.</item>
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
        if (u.Windup > 0)
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

        // 정찰: 유닛 ID 와 몇 초 단위 시각으로 정한 맵 안 지점 (같은 유닛은 한동안 같은 곳으로)
        var slot = (int)(_world.CurrentTick / (30 * 8)) * 7919 + u.Id * 104729;
        var h = SimConstants.MapHalfSize * 0.8f;
        var x = (slot % 1000) / 1000f * 2 - 1;
        var y = (slot / 1000 % 1000) / 1000f * 2 - 1;
        return new Vector2(x * h, y * h);
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
