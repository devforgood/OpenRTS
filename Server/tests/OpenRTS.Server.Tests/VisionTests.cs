using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class VisionTests
{
    [Fact]
    public void VisionGrid_RevealsCellsWhoseCenterIsWithinRadius()
    {
        var grid = new VisionGrid();
        grid.Reveal(new Vector2(0.5f, 0.5f), 3f); // (0.5, 0.5) 은 칸 중심

        Assert.True(grid.IsVisible(new Vector2(0.5f, 0.5f)));
        Assert.True(grid.IsVisible(new Vector2(3.4f, 0.5f)));   // 칸 중심 (3.5, 0.5) 까지 3
        Assert.False(grid.IsVisible(new Vector2(4.2f, 0.5f)));  // 칸 중심 (4.5, 0.5) 까지 4
        Assert.True(grid.IsVisible(new Vector2(2.6f, 2.6f)));   // 칸 중심 (2.5, 2.5) 까지 2.83
        Assert.False(grid.IsVisible(new Vector2(3.2f, 2.6f)));  // 칸 중심 (3.5, 2.5) 까지 3.6
    }

    [Fact]
    public void VisionGrid_ClearHidesEverything_AndClampsAtMapEdge()
    {
        var grid = new VisionGrid();
        grid.Reveal(new Vector2(SimConstants.MapHalfSize, SimConstants.MapHalfSize), 2f);
        Assert.True(grid.IsVisible(new Vector2(SimConstants.MapHalfSize, SimConstants.MapHalfSize)));
        grid.Clear();
        Assert.False(grid.IsVisible(new Vector2(SimConstants.MapHalfSize, SimConstants.MapHalfSize)));
    }

    private static World Create(bool fog = true)
    {
        var s = SimSettings.Default;
        s.BotCount = 0;
        s.FogOfWar = fog;
        return new World(settings: s);
    }

    [Fact]
    public void Enemy_IsVisibleOnlyInsideSight_AllyAndNeutralAlways()
    {
        var world = Create();
        var scout = world.Spawn(new Vector2(-20, 0), team: 1);
        var ally = world.Spawn(new Vector2(40, 40), team: 1);
        var neutral = world.Spawn(new Vector2(40, -40));
        var near = world.Spawn(new Vector2(-20 + UnitArchetypes.Melee.SightRange - 1.5f, 0), team: 2);
        var far = world.Spawn(new Vector2(20, 0), team: 2);
        foreach (var u in world.Units)
        {
            u.HoldFire = true;
            u.Order = Simulation.Combat.UnitOrder.Move; // 서로 쫓아가지 않게
        }

        world.Step(SimConstants.TickDelta);

        Assert.True(world.CanSee(1, ally));
        Assert.True(world.CanSee(1, neutral));
        Assert.True(world.CanSee(1, near));
        Assert.False(world.CanSee(1, far));
        Assert.True(world.CanSee(2, far));
        Assert.True(world.CanSee(2, scout));   // 2팀 near 에서 8.5 → 시야 10 안
        Assert.False(world.CanSee(2, ally));   // 2팀 어느 유닛에서도 멀다
    }

    [Fact]
    public void Walls_BlockSight()
    {
        var s = SimSettings.Default;
        s.BotCount = 0;
        s.Map = OpenRTS.Shared.Map.MapKind.Gate;
        var world = new World(settings: s);
        var watcher = world.Spawn(new Vector2(-3, 20), team: 1);
        var behindWall = world.Spawn(new Vector2(3, 20), team: 2);   // 6 떨어졌지만 벽 너머
        var throughGap = world.Spawn(new Vector2(3, 0), team: 2);
        var watcherAtGap = world.Spawn(new Vector2(-3, 0), team: 1); // 통로로는 보인다
        foreach (var u in world.Units)
        {
            u.HoldFire = true;
            u.Order = Simulation.Combat.UnitOrder.Move;
        }

        world.Step(SimConstants.TickDelta);
        Assert.False(world.CanSee(1, behindWall));
        Assert.True(world.CanSee(1, throughGap));
        Assert.False(world.CanSee(2, watcher));
        Assert.True(world.CanSee(2, watcherAtGap));
    }

    [Fact]
    public void FogOff_EverythingVisible()
    {
        var world = Create(fog: false);
        world.Spawn(new Vector2(-40, 0), team: 1);
        var far = world.Spawn(new Vector2(40, 0), team: 2);
        world.Step(SimConstants.TickDelta);
        Assert.True(world.CanSee(1, far));
    }

    [Fact]
    public void AttackCommand_OnUnitInFog_IsRejected()
    {
        var world = Create();
        world.AddPlayer(1); // 1팀
        var mine = world.UnitsOf(1).First();
        var hidden = world.Spawn(new Vector2(mine.Position.X > 0 ? -45 : 45, 45), team: 2);
        hidden.HoldFire = true;
        world.Step(SimConstants.TickDelta);
        Assert.False(world.CanSee(1, hidden));

        var count = world.Command(1, new UnitCommand { Type = CommandType.Attack, TargetUnit = hidden.Id, UnitIds = new[] { mine.Id } });
        Assert.Equal(0, count);
        Assert.Equal(0, mine.AttackTargetId);
    }
}
