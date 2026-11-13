using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class WorldTests
{
    private static World Create(SimSettings s) => new(settings: s);

    private static SimSettings Settings(ushort bots = 0, BotScenario scenario = BotScenario.Wander, bool avoidance = true)
    {
        var s = SimSettings.Default;
        s.BotCount = bots;
        s.Scenario = scenario;
        s.Avoidance = avoidance;
        s.Separation = avoidance; // "회피 없음" 은 분리 보정까지 끈 상태
        return s;
    }

    private static void Run(World world, float seconds, Action? eachTick = null)
    {
        for (var t = 0f; t < seconds; t += SimConstants.TickDelta)
        {
            world.Step(SimConstants.TickDelta);
            eachTick?.Invoke();
        }
    }

    [Fact]
    public void Unit_MovesAtSpeed_AndStopsAtTarget()
    {
        var world = Create(Settings());
        var unit = world.Spawn(Vector2.Zero);
        world.SetMoveTarget(unit.Id, new Vector2(10, 0));

        Run(world, 1f);
        Assert.InRange(unit.Position.X, SimConstants.UnitSpeed - 0.25f, SimConstants.UnitSpeed + 0.25f);

        Run(world, 2f);
        Assert.True(Vector2.Distance(new Vector2(10, 0), unit.Position) <= World.ArrivalDistance);
        Assert.Null(unit.Target);
    }

    [Fact]
    public void MoveTarget_IsClampedToMap()
    {
        var world = Create(Settings());
        var unit = world.Spawn(Vector2.Zero);
        world.SetMoveTarget(unit.Id, new Vector2(1000, -1000));

        Assert.Equal(new Vector2(SimConstants.MapHalfSize, -SimConstants.MapHalfSize), unit.Target);
    }

    [Fact]
    public void HeadOn_WithAvoidance_PassWithoutOverlap_AndBothArrive()
    {
        var world = Create(Settings());
        var a = world.Spawn(new Vector2(-10, 0));
        var b = world.Spawn(new Vector2(10, 0.01f));
        world.SetMoveTarget(a.Id, new Vector2(10, 0));
        world.SetMoveTarget(b.Id, new Vector2(-10, 0));

        var minDist = float.MaxValue;
        Run(world, 8f, () => minDist = Math.Min(minDist, Vector2.Distance(a.Position, b.Position)));

        Assert.True(minDist >= 2 * world.Settings.UnitRadius * 0.95f, $"min distance {minDist}");
        Assert.Null(a.Target);
        Assert.Null(b.Target);
        Assert.True(Vector2.Distance(a.Position, new Vector2(10, 0)) < 0.05f);
        Assert.True(Vector2.Distance(b.Position, new Vector2(-10, 0)) < 0.05f);
    }

    [Fact]
    public void HeadOn_WithoutAvoidance_PassesThrough()
    {
        var world = Create(Settings(avoidance: false));
        var a = world.Spawn(new Vector2(-10, 0));
        var b = world.Spawn(new Vector2(10, 0));
        world.SetMoveTarget(a.Id, new Vector2(10, 0));
        world.SetMoveTarget(b.Id, new Vector2(-10, 0));

        var minDist = float.MaxValue;
        Run(world, 5f, () => minDist = Math.Min(minDist, Vector2.Distance(a.Position, b.Position)));

        Assert.True(minDist < 0.5f);
    }

    // 기대치: 원형 교차는 중앙에 모두 동시에 몰려 ORCA 해가 없는 순간이 생기므로 15% 까지 허용.
    // 나머지는 반지름 합의 5% 이내. (CollisionReportTests 로 조합별 수치를 볼 수 있다.)
    [Theory]
    [InlineData(BotScenario.CircleSwap, 100, 0.8f)]
    [InlineData(BotScenario.Crossing, 100, 0.95f)]
    [InlineData(BotScenario.Converge, 60, 0.95f)]
    [InlineData(BotScenario.Wander, 200, 0.95f)]
    public void Scenario_KeepsUnitsApart(BotScenario scenario, int bots, float minSeparation)
    {
        var world = Create(Settings((ushort)bots, scenario));
        world.ResetScenario();
        Assert.Equal(bots, world.Units.Count);

        var worst = 1f;
        var maxOverlaps = 0;
        var t = 0;
        Run(world, 12f, () =>
        {
            if (++t % 5 == 0)
            {
                var stats = world.ComputeStats();
                worst = Math.Min(worst, stats.MinSeparationRatio);
                maxOverlaps = Math.Max(maxOverlaps, stats.OverlapPairs);
            }
        });

        Assert.All(world.Units, u => Assert.True(float.IsFinite(u.Position.X) && float.IsFinite(u.Position.Y)));
        Assert.True(worst > minSeparation, $"{scenario}: worst separation ratio {worst:0.000}, max overlapping pairs {maxOverlaps}");
    }

    [Fact]
    public void CircleSwap_WithoutAvoidance_Overlaps()
    {
        var world = Create(Settings(60, BotScenario.CircleSwap, avoidance: false));
        world.ResetScenario();

        var worst = 1f;
        Run(world, 6f, () => worst = Math.Min(worst, world.ComputeStats().MinSeparationRatio));

        Assert.True(worst < 0.5f);
    }

    [Fact]
    public void ApplySettings_RespawnsBots_OnlyWhenCountOrScenarioChanges()
    {
        var world = Create(Settings(10));
        world.ResetScenario();
        var player = world.Spawn(new Vector2(1, 1));
        var firstBot = world.Units.First(u => u.IsBot).Id;

        var s = world.Settings;
        s.UnitRadius = 0.8f;
        world.ApplySettings(s);
        Assert.Contains(world.Units, u => u.Id == firstBot); // 반지름만 바뀜 → 그대로

        s.BotCount = 25;
        world.ApplySettings(s);
        Assert.Equal(25, world.Units.Count(u => u.IsBot));
        Assert.DoesNotContain(world.Units, u => u.Id == firstBot);
        Assert.Contains(world.Units, u => u.Id == player.Id); // 플레이어 유닛은 유지
    }

    [Fact]
    public void BlockedUnit_GivesUp_WhenStopWhenBlocked()
    {
        var world = Create(Settings(40, BotScenario.Converge));
        world.ResetScenario();

        Run(world, 30f);

        // 40명이 한 점으로 모이면 대부분은 도착하지 못하고 막혀서 멈춘다.
        Assert.True(world.BlockedStops > 0);
    }
}
