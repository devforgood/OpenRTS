using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Map;
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

        foreach (var bots in new[] { 50, 150 })
        {
            var gate = Make(BotScenario.Pass, bots);
            gate.Map = MapKind.Gate;
            cases.Add(("gate", gate));
            gate.RepathWhenBlocked = false;
            cases.Add(("gate norepath", gate));
        }

        var blocks = Make(BotScenario.Wander, 300);
        blocks.Map = MapKind.Blocks;
        cases.Add(("blocks", blocks));

        _output.WriteLine($"{Seconds}s each. worstSep = min(distance / 2r), 1.0 = never overlapped");
        _output.WriteLine("scenario     bots  mode           worstSep  maxOverlapPairs  wallHits  blockedStops  repaths  completedTrips  avgStepMs");
        foreach (var (mode, settings) in cases)
        {
            var world = new World(settings: settings);
            world.ResetScenario();

            var worst = 1f;
            var maxOverlaps = 0;
            var wallHits = 0;
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
                    wallHits = Math.Max(wallHits, stats.WallPenetrations);
                }
            }

            var stepMs = sw.Elapsed.TotalMilliseconds / ticks;
            var trips = stops - world.BlockedStops; // 실제로 목표에 도착한 횟수
            _output.WriteLine($"{settings.Scenario,-12} {settings.BotCount,4}  {mode,-13} {worst,8:0.000}  {maxOverlaps,15}  {wallHits,8}  {world.BlockedStops,12}  {world.Repaths,7}  {trips,14}  {stepMs,9:0.00}");
            Assert.All(world.Units, u => Assert.True(float.IsFinite(u.Position.X) && float.IsFinite(u.Position.Y)));
        }
    }

    /// <summary>Battle: 맵·인원별로 한쪽이 전멸할 때까지 걸린 시간, 남은 유닛, 서버 1틱 비용.</summary>
    [Fact]
    public void BattleReport()
    {
        _output.WriteLine("map     bots  winner  seconds  survivors  deaths  worstSep  wallHits  avgStepMs  maxStepMs");
        foreach (var (map, bots) in new[] { (MapKind.Open, 60), (MapKind.Open, 200), (MapKind.Open, 600), (MapKind.Gate, 200), (MapKind.Blocks, 200) })
        {
            var s = Make(BotScenario.Battle, bots);
            s.Map = map;
            var world = new World(settings: s);
            world.ResetScenario();
            var worst = 1f;
            var wallHits = 0;
            var maxStep = 0.0;
            var worstProfile = new StepProfile();
            var total = System.Diagnostics.Stopwatch.StartNew();
            var ticks = 0;
            while (world.Winner == 0 && ticks < 30 * 300)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                world.Step(SimConstants.TickDelta);
                if (sw.Elapsed.TotalMilliseconds > maxStep)
                {
                    maxStep = sw.Elapsed.TotalMilliseconds;
                    worstProfile = world.LastProfile;
                }
                ticks++;
                if (ticks % 5 == 0)
                {
                    var stats = world.ComputeStats();
                    worst = Math.Min(worst, stats.MinSeparationRatio);
                    wallHits = Math.Max(wallHits, stats.WallPenetrations);
                }
            }

            var survivors = world.Units.Count(u => u.Team == world.Winner);
            _output.WriteLine($"{map,-7} {bots,4}  {world.Winner,6}  {ticks * SimConstants.TickDelta,7:0.0}  {survivors,9}  {world.Combat.Deaths,6}  {worst,8:0.000}  {wallHits,8}  {total.Elapsed.TotalMilliseconds / ticks,9:0.00}  {maxStep,9:0.00}");
            _output.WriteLine($"        worst tick: {worstProfile}, deferred paths {world.DeferredPaths}");
            Assert.NotEqual(0, world.Winner);
        }
    }

    /// <summary>
    /// 스냅샷 대역폭: 전체 전송과 델타 전송(클라이언트가 2스냅샷 전 것을 확인했다고 가정 — 왕복 약 130ms)의
    /// 클라이언트 1명당 바이트/초. 패킷 헤더(UDP/LiteNetLib)는 빼고 스냅샷 메시지만 센다.
    /// </summary>
    [Fact]
    public void BandwidthReport()
    {
        const int AckLagSnapshots = 2;
        var cases = new List<(string Name, SimSettings Settings)>
        {
            ("idle 300", Make(BotScenario.Wander, 0)),
            ("wander 300", Make(BotScenario.Wander, 300)),
            ("circleswap 300", Make(BotScenario.CircleSwap, 300)),
            ("converge 60", Make(BotScenario.Converge, 60)),
        };
        var gate = Make(BotScenario.Pass, 50);
        gate.Map = MapKind.Gate;
        cases.Add(("gate pass 50", gate));
        var noFog = Make(BotScenario.Battle, 200);
        noFog.FogOfWar = false;
        cases.Add(("battle 200 nofog", noFog));
        cases.Add(("battle 200 fog", Make(BotScenario.Battle, 200))); // 1팀이 받는 양

        _output.WriteLine($"{Seconds}s each, {SimConstants.SnapshotRate}Hz snapshots, delta baseline = {AckLagSnapshots} snapshots ago");
        _output.WriteLine("case             units  full B/s   delta B/s  ratio");
        foreach (var (name, settings) in cases)
        {
            var world = new World(settings: settings);
            world.ResetScenario();
            if (name.StartsWith("idle"))
            {
                for (var i = 0; i < 300; i++)
                {
                    world.Spawn(new Vector2(i % 20 * 2 - 20, i / 20 * 2 - 15)); // 멈춰 있는 유닛 300
                }
            }

            var history = new List<List<SnapshotEntry>>();
            var delta = new List<SnapshotEntry>();
            long fullBytes = 0, deltaBytes = 0;
            var ticks = (int)(Seconds / SimConstants.TickDelta);
            for (var t = 1; t <= ticks; t++)
            {
                world.Step(SimConstants.TickDelta);
                if (t % SimConstants.TicksPerSnapshot != 0)
                {
                    continue;
                }

                var current = world.CaptureState()
                    .Where(u => world.Find(u.Id) is { } unit && world.CanSee(1, unit))
                    .Select(u => SnapshotEntry.From(u)).OrderBy(e => e.Id).ToList();
                fullBytes += Bytes(current);
                if (history.Count >= AckLagSnapshots)
                {
                    SnapshotDelta.Diff(history[^AckLagSnapshots], current, delta);
                    deltaBytes += Bytes(delta);
                }
                else
                {
                    deltaBytes += Bytes(current);
                }

                history.Add(current);
            }

            var fullRate = fullBytes / Seconds;
            var deltaRate = deltaBytes / Seconds;
            _output.WriteLine($"{name,-16} {world.Units.Count,5}  {fullRate,8:0}  {deltaRate,10:0}  {deltaRate / fullRate,5:0.00}");
            Assert.True(deltaBytes <= fullBytes);
        }

        static long Bytes(List<SnapshotEntry> entries)
        {
            var parts = new List<(int Start, int Count)>();
            SnapshotMessage.Plan(entries, SnapshotMessage.DefaultMaxPacketSize, parts);
            return parts.Count * SnapshotMessage.HeaderSize + entries.Sum(e => (long)SnapshotMessage.EntrySize(e));
        }
    }

}
