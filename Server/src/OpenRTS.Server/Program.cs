using OpenRTS.Server;
using OpenRTS.Server.Net;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;

// 사용법: OpenRTS.Server [--port 7777] [--bots 3]
var port = SimConstants.DefaultPort;
var bots = 3;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--bots": bots = int.Parse(args[++i]); break;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var world = new World(seed: Environment.TickCount);
for (var i = 0; i < bots; i++)
{
    world.SpawnRandom(isBot: true);
}

using var server = new GameServer(world, msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));
server.Start(port);
Console.WriteLine($"OpenRTS server: tick {SimConstants.TickRate}Hz, snapshot {SimConstants.SnapshotRate}Hz, protocol v{SimConstants.ProtocolVersion}, bots {bots}. Ctrl+C to stop.");

var loop = new GameLoop(SimConstants.TickRate);
await loop.RunAsync(tick => server.Tick((uint)tick), cts.Token);

Console.WriteLine($"Stopped at tick {loop.Tick}.");
