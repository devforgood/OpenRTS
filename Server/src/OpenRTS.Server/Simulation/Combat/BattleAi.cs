using System.Numerics;

namespace OpenRTS.Server.Simulation.Combat;

/// <summary>
/// 행동 층 AI: 전투 봇의 상위 판단 (참고 서버의 IAIScript, 특히 LaneMinionAI 의 재평가 규칙을 참고해 새로 작성).
/// 실행(추적·공격)은 <see cref="CombatSystem"/> 이 하고, 여기서는 명령만 바꾼다.
/// <list type="bullet">
/// <item>0.25초마다(유닛별로 시점을 흩어서) 다시 판단한다.</item>
/// <item>대상을 4초 동안 한 번도 공격하지 못했으면(막혀서 닿지 못함) 0.5초 동안 그 대상을 무시하고 다른 적을 찾는다.</item>
/// <item>싸울 상대가 근처에 없으면 가장 가까운 적에게 공격 이동한다. 적이 다 죽으면 멈춘다.</item>
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

        var nearest = NearestEnemy(u);
        if (nearest == null)
        {
            if (u.Order == UnitOrder.AttackMove)
            {
                u.Order = UnitOrder.Idle;
                u.Stop();
            }

            return;
        }

        if (u.Order != UnitOrder.AttackMove || Vector2.Distance(u.Goal, nearest.Position) > RetargetGoalDistance)
        {
            u.Order = UnitOrder.AttackMove;
            u.Goal = nearest.Position;
            u.ChaseTimer = 0;
        }
    }

    private Unit? NearestEnemy(Unit u)
    {
        Unit? best = null;
        var bestDist = float.MaxValue;
        foreach (var other in _world.Units)
        {
            if (other.Id == u.IgnoreTargetId || !_world.Combat.IsValidTarget(u, other))
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
