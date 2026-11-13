using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;

namespace OpenRTS.Server.Tests;

public class WorldTests
{
    [Fact]
    public void Unit_MovesAtSpeed_AndStopsAtTarget()
    {
        var world = new World();
        var unit = world.Spawn(Vector2.Zero);
        world.SetMoveTarget(unit.Id, new Vector2(10, 0));

        world.Step(1f);
        Assert.Equal(SimConstants.UnitSpeed, unit.Position.X, 3);

        for (var i = 0; i < 10; i++)
        {
            world.Step(1f);
        }

        Assert.Equal(new Vector2(10, 0), unit.Position);
        Assert.Null(unit.Target);
    }

    [Fact]
    public void MoveTarget_IsClampedToMap()
    {
        var world = new World();
        var unit = world.Spawn(Vector2.Zero);
        world.SetMoveTarget(unit.Id, new Vector2(1000, -1000));

        Assert.Equal(new Vector2(SimConstants.MapHalfSize, -SimConstants.MapHalfSize), unit.Target);
    }

    [Fact]
    public void Bot_AlwaysHasSomewhereToGo()
    {
        var world = new World();
        var bot = world.Spawn(Vector2.Zero, isBot: true);

        world.Step(SimConstants.TickDelta);

        Assert.NotEqual(Vector2.Zero, bot.Position);
    }
}
