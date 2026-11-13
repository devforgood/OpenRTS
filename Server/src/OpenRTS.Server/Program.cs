using OpenRTS.Server;
using OpenRTS.Shared;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var loop = new GameLoop(SimConstants.TickRate);
Console.WriteLine($"OpenRTS server starting (tick {SimConstants.TickRate}Hz, protocol v{SimConstants.ProtocolVersion}). Ctrl+C to stop.");

await loop.RunAsync(tick =>
{
    // TODO: 네트워크 수신 → 명령 적용 → 시뮬레이션 → 상태 전송
    if (tick % (SimConstants.TickRate * 10) == 0)
    {
        Console.WriteLine($"tick {tick}");
    }
}, cts.Token);

Console.WriteLine($"Stopped at tick {loop.Tick}.");
