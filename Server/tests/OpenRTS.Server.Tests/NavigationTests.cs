using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Server.Simulation.Avoidance;
using OpenRTS.Server.Simulation.Navigation;
using OpenRTS.Shared;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class NavigationTests
{
    private static NavGrid Grid(MapKind map) => new(MapLayouts.Create(map));

    private static void AssertPathClear(NavGrid nav, Vector2 start, IReadOnlyList<Vector2> path, float radius)
    {
        var from = start;
        foreach (var p in path)
        {
            Assert.True(nav.HasLineOfSight(from, p, radius), $"segment {from} → {p} touches a wall");
            from = p;
        }
    }

    [Fact]
    public void OpenMap_PathIsStraightLine()
    {
        var nav = Grid(MapKind.Open);
        var path = new List<Vector2>();
        Assert.True(nav.FindPath(new Vector2(-10, -10), new Vector2(20, 5), 0.5f, path));
        Assert.Equal(new[] { new Vector2(20, 5) }, path);
    }

    [Fact]
    public void Gate_PathGoesThroughGap()
    {
        var nav = Grid(MapKind.Gate);
        var start = new Vector2(-20, 20);
        var goal = new Vector2(20, 20);
        var path = new List<Vector2>();

        Assert.True(nav.FindPath(start, goal, 0.5f, path));
        Assert.Equal(goal, path[^1]);
        AssertPathClear(nav, start, path, 0.5f);
        Assert.Contains(path, p => MathF.Abs(p.X) <= 2f && MathF.Abs(p.Y) < MapLayouts.GateGapWidth / 2); // 통로 근처를 지난다
        Assert.InRange(path.Count, 2, 4); // 단순화되어 경유점이 몇 개 안 된다
    }

    [Fact]
    public void Gate_UnitWiderThanGap_GetsClosestReachablePoint()
    {
        var nav = Grid(MapKind.Gate);
        var radius = MapLayouts.GateGapWidth / 2 + 0.3f; // 통로보다 큼
        var path = new List<Vector2>();

        Assert.True(nav.FindPath(new Vector2(-20, 0), new Vector2(20, 0), radius, path));
        Assert.True(path[^1].X < 0, $"ended at {path[^1]} — should stay on the left side");
        Assert.True(path[^1].X > -5, "should get as close as possible to the goal");
    }

    [Fact]
    public void Blocks_EveryPathAvoidsWalls()
    {
        var nav = Grid(MapKind.Blocks);
        var random = new Random(7);
        var path = new List<Vector2>();
        for (var i = 0; i < 50; i++)
        {
            Vector2 R() => new((float)(random.NextDouble() * 90 - 45), (float)(random.NextDouble() * 90 - 45));
            var start = nav.NearestStandable(R(), 0.5f);
            var goal = R();
            Assert.True(nav.FindPath(start, goal, 0.5f, path));
            AssertPathClear(nav, start, path, 0.5f);
            Assert.True(Vector2.Distance(nav.NearestStandable(goal, 0.5f), path[^1]) < 0.01f);
        }
    }

    [Fact]
    public void NearestStandable_PullsPointOutOfWall()
    {
        var nav = Grid(MapKind.Gate);
        var p = nav.NearestStandable(new Vector2(0.2f, 30), 0.5f); // 벽 한가운데
        Assert.True(nav.Clearance(p) >= 0.5f);
        Assert.True(MathF.Abs(p.X) < 2.5f); // 벽 바로 옆
    }

    [Fact]
    public void SegmentRectDistance_Cases()
    {
        var r = new WallRect(new Vector2(0, 0), new Vector2(2, 2));
        Assert.Equal(0f, NavGrid.SegmentRectDistance(new Vector2(-1, 1), new Vector2(3, 1), r)); // 관통
        Assert.Equal(1f, NavGrid.SegmentRectDistance(new Vector2(-1, 3), new Vector2(3, 3), r), 4); // 위로 지나감
        Assert.Equal(MathF.Sqrt(2), NavGrid.SegmentRectDistance(new Vector2(3, 3), new Vector2(4, 4), r), 4); // 모서리
    }

    [Fact]
    public void Orca_StopsBeforeWall_WithoutPushOut()
    {
        // 벽을 향해 최고 속도로 달려도 ORCA 장애물 제약만으로 파고들지 않는다.
        var wall = new WallRect(new Vector2(2, -5), new Vector2(4, 5));
        var obstacles = ObstacleVertex.CreatePolygon(wall.CounterClockwise());
        var solver = new OrcaSolver();
        var near = new List<ObstacleVertex>();
        var dist = new List<float>();
        var pos = Vector2.Zero;
        var vel = Vector2.Zero;
        const float radius = 0.5f, speed = 6f, dt = 1f / 30;
        for (var i = 0; i < 90; i++)
        {
            OrcaSolver.FindObstacleNeighbors(pos, speed + radius, obstacles, near, dist);
            vel = solver.ComputeNewVelocity(pos, vel, new Vector2(speed, 0), radius, speed, Array.Empty<OrcaNeighbor>(), 2f, dt, near, 1f);
            pos += vel * dt;
            Assert.True(pos.X <= 2 - radius + 0.02f, $"tick {i}: penetrated to {pos}");
        }

        Assert.True(pos.X > 1.3f, $"should get close to the wall, ended at {pos}");
    }

    [Fact]
    public void World_UnitRoutesAroundWalls_AndNeverEntersThem()
    {
        var s = SimSettings.Default;
        s.BotCount = 0;
        s.Map = MapKind.Blocks;
        var world = new World(settings: s);
        var unit = world.Spawn(new Vector2(-30, 5));
        var goal = new Vector2(30, 5); // 사이에 세로 벽 두 개
        world.SetMoveTarget(unit.Id, goal);
        Assert.True(unit.Path.Count > 1, "should need a detour");

        for (var t = 0; t < 30 * 20 && unit.IsMoving; t++)
        {
            world.Step(SimConstants.TickDelta);
            Assert.True(world.Nav.Clearance(unit.Position) >= s.UnitRadius * 0.95f, $"inside wall at {unit.Position}");
        }

        Assert.True(Vector2.Distance(unit.Position, goal) <= World.ArrivalDistance);
    }

    [Fact]
    public void GatePass50_CrossesNarrowGap_WithoutOverlapOrWallHits()
    {
        var s = SimSettings.Default;
        s.BotCount = 50;
        s.Map = MapKind.Gate;
        s.Scenario = BotScenario.Pass;
        var world = new World(settings: s);
        world.ResetScenario();

        var worst = 1f;
        var wallHits = 0;
        var crossed = new HashSet<int>();
        var maxSpeed = 0f;
        for (var t = 0; t < 30 * 40; t++)
        {
            var before = world.Units.ToDictionary(u => u.Id, u => u.Position);
            world.Step(SimConstants.TickDelta);
            foreach (var u in world.Units)
            {
                maxSpeed = MathF.Max(maxSpeed, Vector2.Distance(before[u.Id], u.Position) / SimConstants.TickDelta);
                if (u.Position.X > 5)
                {
                    crossed.Add(u.Id);
                }
            }

            if (crossed.Count == 50)
            {
                break;
            }

            if (t % 5 == 0)
            {
                var stats = world.ComputeStats();
                worst = MathF.Min(worst, stats.MinSeparationRatio);
                wallHits += stats.WallPenetrations;
            }
        }

        Assert.Equal(50, crossed.Count);
        Assert.Equal(0, wallHits);
        Assert.True(worst >= 0.9f, $"worst separation {worst}");
        // 튐 없음: 밀어내기 보정이 있어도 한 틱 이동량이 최고 속도의 1.5배를 넘지 않는다.
        Assert.True(maxSpeed <= s.UnitSpeed * 1.5f, $"max speed {maxSpeed}");
    }

    [Fact]
    public void BlockedByIdleCrowd_Repaths_AroundIt()
    {
        // 목표 앞에 멈춘 유닛 벽을 세워 두면, 진척이 없을 때 그 칸을 피하는 경로를 다시 찾는다.
        var s = SimSettings.Default;
        s.BotCount = 0;
        var world = new World(settings: s);
        for (var y = -6; y <= 6; y++)
        {
            world.Spawn(new Vector2(0, y * 1.05f));
            world.Spawn(new Vector2(1.05f, y * 1.05f));
        }

        var unit = world.Spawn(new Vector2(-15, 0));
        var goal = new Vector2(15, 0);
        world.SetMoveTarget(unit.Id, goal);
        for (var t = 0; t < 30 * 20 && unit.IsMoving; t++)
        {
            world.Step(SimConstants.TickDelta);
        }

        Assert.True(Vector2.Distance(unit.Position, goal) < 0.5f, $"ended at {unit.Position}, repaths {world.Repaths}");
    }
}
