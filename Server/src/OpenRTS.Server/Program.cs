using System.Globalization;
using OpenRTS.Server;
using OpenRTS.Server.Net;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

const string Usage = """
    사용법: OpenRTS.Server [옵션]
      --port <n>              UDP 포트 (기본 7777)
      --bots <n>              봇 수 (기본 3, 최대 600)
      --scenario <name>       wander | converge | circleswap | crossing | pass | battle (기본 wander)
      --map <name>            open | gate | blocks (기본 open)
      --player-units <n>      플레이어 한 명의 유닛 수 (기본 8, 최대 100)
      --radius <r>            유닛 반지름 (기본 0.5)
      --speed <v>             유닛 속도 (기본 6)
      --no-avoidance          충돌 회피 끄기
      --no-repath             막혔을 때 재경로 끄기
      --no-delta              델타 스냅샷 끄기 (매번 전체 전송)
      --no-debug-commands     클라이언트의 설정 변경 요청 거부
    """;

var port = SimConstants.DefaultPort;
var settings = SimSettings.Default;
var allowDebug = true;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        float NextFloat() => float.Parse(Next(), CultureInfo.InvariantCulture);

        switch (args[i])
        {
            case "--port": port = int.Parse(Next()); break;
            case "--bots": settings.BotCount = ushort.Parse(Next()); break;
            case "--scenario": settings.Scenario = Enum.Parse<BotScenario>(Next(), ignoreCase: true); break;
            case "--map": settings.Map = Enum.Parse<MapKind>(Next(), ignoreCase: true); break;
            case "--player-units": settings.PlayerUnits = byte.Parse(Next()); break;
            case "--radius": settings.UnitRadius = NextFloat(); break;
            case "--speed": settings.UnitSpeed = NextFloat(); break;
            case "--no-avoidance": settings.Avoidance = false; break;
            case "--no-repath": settings.RepathWhenBlocked = false; break;
            case "--no-delta": settings.DeltaSnapshots = false; break;
            case "--no-debug-commands": allowDebug = false; break;
            case "-h" or "--help": Console.WriteLine(Usage); return;
            default: throw new ArgumentException($"unknown option {args[i]}");
        }
    }
}
catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Usage);
    Environment.ExitCode = 2;
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var world = new World(seed: Environment.TickCount, settings);
world.ResetScenario();

using var server = new GameServer(world, msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"), allowDebug);
server.Start(port);
Console.WriteLine($"OpenRTS server: tick {SimConstants.TickRate}Hz, snapshot {SimConstants.SnapshotRate}Hz, protocol v{SimConstants.ProtocolVersion}");
Console.WriteLine($"  {world.Settings}");
Console.WriteLine($"  debug commands {(allowDebug ? "allowed" : "disabled")}. Ctrl+C to stop.");

var loop = new GameLoop(SimConstants.TickRate);
await loop.RunAsync(tick => server.Tick((uint)tick), cts.Token);

Console.WriteLine($"Stopped at tick {loop.Tick}.");
