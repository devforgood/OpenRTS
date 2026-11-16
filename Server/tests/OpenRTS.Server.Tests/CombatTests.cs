using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class CombatTests
{
    private static World Create(MapKind map = MapKind.Open, BotScenario scenario = BotScenario.Wander, ushort bots = 0)
    {
        var s = SimSettings.Default;
        s.BotCount = bots;
        s.Scenario = scenario;
        s.Map = map;
        var world = new World(settings: s);
        world.ResetScenario();
        return world;
    }

    /// <summary>반격하지 않는 표적 (사격 중지).</summary>
    private static Unit Dummy(World world, Vector2 position, byte team = 2, UnitKind kind = UnitKind.Melee)
    {
        var u = world.Spawn(position, team: team, kind: kind);
        u.HoldFire = true;
        return u;
    }

    private static float RunUntil(World world, Func<bool> condition, float maxSeconds)
    {
        var t = 0f;
        while (!condition())
        {
            Assert.True(t < maxSeconds, $"not reached within {maxSeconds}s");
            world.Step(SimConstants.TickDelta);
            t += SimConstants.TickDelta;
        }

        return t;
    }

    [Fact]
    public void Melee_IdleUnitAcquires_AndKillsInExpectedTime()
    {
        var world = Create();
        var attacker = world.Spawn(new Vector2(0, 0), team: 1, kind: UnitKind.Melee);
        var target = Dummy(world, new Vector2(3, 0));
        var events = new List<CombatEvent>();

        var time = RunUntil(world, () =>
        {
            events.AddRange(world.Combat.Events);
            world.Combat.ClearEvents();
            return world.Find(target.Id) is null;
        }, 20f);

        // 120 체력 / 12 = 10타. 접근 약 0.3초 + 선딜 0.25초 + 쿨다운 1초 × 9
        var m = UnitArchetypes.Melee;
        var expected = 9 * m.Cooldown + m.Windup;
        Assert.InRange(time, expected, expected + 1f);
        Assert.Equal(10, events.Count(e => e.Type == CombatEventType.MeleeHit && e.Attacker == attacker.Id));
        Assert.Single(events, e => e.Type == CombatEventType.Death && e.Target == target.Id);
        world.Step(SimConstants.TickDelta); // 다음 틱에 죽은 대상을 놓는다
        Assert.Equal(UnitOrder.Idle, attacker.Order);

        Assert.Equal(0, attacker.AttackTargetId);
    }

    [Fact]
    public void Ranged_ProjectileHits_AfterTravelTime()
    {
        var world = Create();
        var shooter = world.Spawn(new Vector2(0, 0), team: 1, kind: UnitKind.Ranged);
        var target = Dummy(world, new Vector2(6.5f, 0)); // 가장자리 거리 5.5 → 사거리 안

        RunUntil(world, () => world.Combat.ProjectileCount > 0, 2f);
        Assert.False(shooter.IsMoving); // 사거리 안이라 다가가지 않음
        var hp = target.Hp;
        var travel = RunUntil(world, () => target.Hp < hp, 2f);

        var expected = (6.5f - world.Settings.UnitRadius) / UnitArchetypes.Ranged.ProjectileSpeed;
        Assert.InRange(travel, expected - 0.07f, expected + 0.07f);
        Assert.Equal(hp - UnitArchetypes.Ranged.Damage, target.Hp, 3);
    }

    [Fact]
    public void Projectile_Vanishes_WhenTargetDiesFirst()
    {
        var world = Create();
        world.Spawn(new Vector2(0, 0), team: 1, kind: UnitKind.Ranged);
        var target = Dummy(world, new Vector2(7, 0));
        RunUntil(world, () => world.Combat.ProjectileCount > 0, 2f);

        world.Combat.ApplyDamage(0, 1, target, 1000); // 다른 이유로 먼저 죽음
        world.Step(SimConstants.TickDelta);
        Assert.Null(world.Find(target.Id));
        Assert.Equal(0, world.Combat.ProjectileCount);
    }

    [Fact]
    public void MoveOrder_IgnoresEnemies_ThenIdleAutoEngages()
    {
        var world = Create();
        var u = world.Spawn(new Vector2(-10, 0), team: 1, owner: 1);
        Dummy(world, new Vector2(6, 2)); // 지나갈 때 가장자리 거리 1, 도착 지점(10,0)에서 약 3.5

        world.Command(1, new UnitCommand { Type = CommandType.Move, Target = new Vector2(10, 0), UnitIds = new[] { u.Id } });

        RunUntil(world, () => u.Position.X > 0, 5f);
        Assert.Equal(0, u.AttackTargetId); // 지나가는 동안 무시

        RunUntil(world, () => !u.IsMoving && u.Order == UnitOrder.Idle, 5f);
        world.Step(SimConstants.TickDelta);
        Assert.NotEqual(0, u.AttackTargetId); // 도착 후 대기 → 획득 거리 안의 적과 싸움
    }

    [Fact]
    public void AttackCommand_ChasesAroundWall_AndKills()
    {
        var world = Create(MapKind.Gate);
        var u = world.Spawn(new Vector2(-6, 20), team: 1, owner: 1, kind: UnitKind.Melee);
        var target = Dummy(world, new Vector2(6, 20));
        world.Command(1, new UnitCommand { Type = CommandType.Attack, TargetUnit = target.Id, UnitIds = new[] { u.Id } });

        RunUntil(world, () => world.Find(target.Id) is null, 30f);
        Assert.True(u.Position.X > 0, "should have gone through the gap");
    }

    [Fact]
    public void IdleUnit_LeashesBack_WhenChasingTooFar()
    {
        var world = Create();
        var guard = world.Spawn(new Vector2(0, 0), team: 1);
        var runner = Dummy(world, new Vector2(4, 0));
        world.SetMoveTarget(runner.Id, new Vector2(45, 0)); // 같은 속도로 도망

        RunUntil(world, () => guard.AttackTargetId != 0, 1f);
        RunUntil(world, () => guard.Order == UnitOrder.Move, 5f); // 리쉬 거리를 넘으면 돌아간다
        Assert.Equal(0, guard.AttackTargetId);
        RunUntil(world, () => guard.Order == UnitOrder.Idle, 5f);
        Assert.True(Vector2.Distance(guard.Position, Vector2.Zero) < 0.5f, $"returned to {guard.Position}");
    }

    [Fact]
    public void CallForHelp_IdleAlliesTargetTheAttacker()
    {
        var world = Create();
        var victim = world.Spawn(new Vector2(0, 0), team: 1);
        victim.HoldFire = true; // 스스로는 반격하지 않게
        var ally = world.Spawn(new Vector2(-7, 0), team: 1); // 공격자까지 가장자리 거리 > 획득 거리 8
        var shooter = world.Spawn(new Vector2(6, 0), team: 2, kind: UnitKind.Ranged);

        world.Step(SimConstants.TickDelta);
        Assert.Null(world.Combat.FindTarget(ally, UnitArchetypes.Melee.AcquisitionRange));
        RunUntil(world, () => victim.Hp < victim.Stats.MaxHp, 3f);
        Assert.Equal(shooter.Id, ally.AttackTargetId);
    }

    [Fact]
    public void FindTarget_PrefersEnemyAttackingMyTeam_OverCloserOne()
    {
        var world = Create();
        var me = world.Spawn(new Vector2(0, 0), team: 1);
        var friend = world.Spawn(new Vector2(0, 6), team: 1);
        var near = world.Spawn(new Vector2(2, 0), team: 2);
        var aggressor = world.Spawn(new Vector2(4, 4), team: 2);
        foreach (var u in world.Units)
        {
            u.HoldFire = true; // 서로 움직이지 않게
        }

        world.Step(SimConstants.TickDelta, tick: 1); // 격자 갱신
        aggressor.AttackTargetId = friend.Id;


        Assert.Equal(aggressor.Id, world.Combat.FindTarget(me, 8f)!.Id);
        Assert.NotEqual(near.Id, world.Combat.FindTarget(me, 8f)!.Id);
    }

    [Fact]
    public void Neutral_And_SameTeam_AreNeverTargets()
    {
        var world = Create(bots: 5); // Wander 봇 = 중립
        var u = world.Spawn(world.Units[0].Position + new Vector2(1.2f, 0), team: 1);
        world.Spawn(u.Position + new Vector2(0, 1.2f), team: 1);
        for (var i = 0; i < 60; i++)
        {
            world.Step(SimConstants.TickDelta);
        }

        Assert.Equal(0, u.AttackTargetId);
        Assert.All(world.Units, x => Assert.Equal(x.Stats.MaxHp, x.Hp));
    }

    [Fact]
    public void StopCommand_CancelsWindup()
    {
        var world = Create();
        var u = world.Spawn(new Vector2(0, 0), team: 1, owner: 1);
        var target = Dummy(world, new Vector2(1.1f, 0));
        RunUntil(world, () => u.Windup > 0, 1f);

        world.Command(1, new UnitCommand { Type = CommandType.Stop, UnitIds = new[] { u.Id } });
        Assert.Equal(0, u.Windup);
        world.Step(SimConstants.TickDelta);
        Assert.Equal(target.Stats.MaxHp, target.Hp);
    }

    [Fact]
    public void Commands_OnlyAffectOwnUnits()
    {
        var world = Create();
        var mine = world.Spawn(new Vector2(0, 0), team: 1, owner: 1);
        var theirs = world.Spawn(new Vector2(0, 5), team: 2, owner: 2);
        var count = world.Command(1, new UnitCommand { Type = CommandType.Move, Target = new Vector2(20, 20), UnitIds = new[] { mine.Id, theirs.Id } });

        Assert.Equal(1, count);
        Assert.True(mine.IsMoving);
        Assert.False(theirs.IsMoving);
    }

    [Theory]
    [InlineData(MapKind.Open, 60)]
    [InlineData(MapKind.Open, 200)]
    [InlineData(MapKind.Gate, 60)]
    public void Battle_EndsWithOneTeamWipedOut_ThenRestarts(MapKind map, ushort bots)
    {
        var world = Create(map, BotScenario.Battle, bots);
        Assert.Equal(bots / 2, world.Units.Count(u => u.Team == 1));
        Assert.Equal(bots - bots / 2, world.Units.Count(u => u.Team == 2));

        var time = RunUntil(world, () => world.Winner != 0, 180f);
        var loser = world.Winner == 1 ? 2 : 1;
        Assert.Equal(0, world.Units.Count(u => u.Team == loser));
        Assert.True(world.Units.Count(u => u.Team == world.Winner) > 0);
        Assert.True(world.Combat.Deaths >= bots / 2);
        Assert.True(time > 3f, "battle should take a while");

        RunUntil(world, () => world.Winner == 0, World.BattleRestartDelay + 1f);
        Assert.Equal(bots, world.Units.Count(u => u.IsBot));
    }
}
