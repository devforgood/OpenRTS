using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Protocol;
using Xunit.Abstractions;

namespace OpenRTS.Server.Tests;

/// <summary>
/// 시나리오·설정 조합별 충돌 지표를 출력한다 (판정은 NaN 여부만).
/// dotnet test --filter CollisionReport --logger "console;verbosity=detailed"
/// </summary>
public class CollisionReportTests
{
    private const float Seconds = 30f;
    private readonly ITestOutputHelper _output;

    public CollisionReportTests(ITestOutputHelper output) => _output = output;

    private static SimSettings Make(BotScenario scenario, int bots, bool avoidance = true, bool separation = true, byte maxNeighbors = 10, float horizon = 2f)
    {
        var s = SimSettings.Default;
        s.BotCount = (ushort)bots;
        s.Scenario = scenario;
        s.Avoidance = avoidance;
        s.Separation = separation;
        s.MaxNeighbors = maxNeighbors;
        s.TimeHorizon = horizon;
        return s;
    }

    [Fact]
    public void CollisionReport()
    {
        var cases = new List<(string Mode, SimSettings Settings)>();
        foreach (var (scenario, bots) in new[] { (BotScenario.CircleSwap, 100), (BotScenario.CircleSwap, 300), (BotScenario.Crossing, 200), (BotScenario.Converge, 60), (BotScenario.Wander, 300) })
        {
            cases.Add(("none", Make(scenario, bots, avoidance: false, separation: false)));
            cases.Add(("orca", Make(scenario, bots, separation: false)));
            cases.Add(("orca+sep", Make(scenario, bots)));
        }

        cases.Add(("orca+sep n20", Make(BotScenario.CircleSwap, 100, maxNeighbors: 20)));
        cases.Add(("orca+sep h5", Make(BotScenario.CircleSwap, 100, horizon: 5)));
        cases.Add(("orca+sep h0.5", Make(BotScenario.CircleSwap, 100, horizon: 0.5f)));

        _output.WriteLine($"{Seconds}s each. worstSep = min(distance / 2r), 1.0 = never overlapped");
        _output.WriteLine("scenario     bots  mode           worstSep  maxOverlapPairs  blockedStops  completedTrips  avgStepMs");
        foreach (var (mode, settings) in cases)
        {
            var world = new World(settings: settings);
            world.ResetScenario();

            var worst = 1f;
            var maxOverlaps = 0;
            var stops = 0;
            var wasMoving = world.Units.ToDictionary(u => u.Id, u => u.IsMoving);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ticks = (int)(Seconds / SimConstants.TickDelta);
            for (var t = 0; t < ticks; t++)
            {
                world.Step(SimConstants.TickDelta);
                foreach (var u in world.Units)
                {
                    if (wasMoving[u.Id] && !u.IsMoving) stops++;
                    wasMoving[u.Id] = u.IsMoving;
                }

                if (t % 5 == 0)
                {
                    var stats = world.ComputeStats();
                    worst = Math.Min(worst, stats.MinSeparationRatio);
                    maxOverlaps = Math.Max(maxOverlaps, stats.OverlapPairs);
                }
            }

            var stepMs = sw.Elapsed.TotalMilliseconds / ticks;
            var trips = stops - world.BlockedStops; // 실제로 목표에 도착한 횟수
            _output.WriteLine($"{settings.Scenario,-12} {settings.BotCount,4}  {mode,-13} {worst,8:0.000}  {maxOverlaps,15}  {world.BlockedStops,12}  {trips,14}  {stepMs,9:0.00}");
            Assert.All(world.Units, u => Assert.True(float.IsFinite(u.Position.X) && float.IsFinite(u.Position.Y)));
        }
    }
}
