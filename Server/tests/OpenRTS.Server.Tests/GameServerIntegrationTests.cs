using System.Numerics;
using LiteNetLib;
using OpenRTS.Server.Net;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Interpolation;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Server.Tests;

/// <summary>실제 UDP 소켓으로 서버와 테스트 클라이언트를 띄워 동기화 흐름을 확인한다.</summary>
public sealed class GameServerIntegrationTests : IDisposable
{
    private readonly GameServer _server;
    private uint _tick;

    public GameServerIntegrationTests()
    {
        var settings = SimSettings.Default;
        settings.BotCount = 0;
        _server = new GameServer(new World(settings: settings));
        _server.Start(0);
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public void Client_Joins_Moves_AndSeesItsPositionInSnapshots()
    {
        using var client = new TestClient(_server.Port);

        RunUntil(() => client.Welcome is not null && client.Settings is not null, client);
        var unitId = client.Welcome!.Value.UnitId;
        var start = _server.World.Find(unitId)!.Position;
        var target = start + new Vector2(3, 0);

        client.SendMove(target);
        // 3유닛 / 6유닛/초 = 0.5초 = 15틱. 여유를 두고 기다린다. 스냅샷은 16비트 양자화라 0.01 이내면 같은 위치.
        RunUntil(() => client.LastSnapshot.TryGetValue(unitId, out var p) && Vector2.Distance(p, target) < 0.01f, client);

        Assert.True(Vector2.Distance(target, _server.World.Find(unitId)!.Position) <= World.ArrivalDistance);
        Assert.Equal(0u, client.LastSnapshotTick % SimConstants.TicksPerSnapshot);
    }

    [Fact]
    public void TwoClients_SeeEachOther_AndDisconnectRemovesUnit()
    {
        using var a = new TestClient(_server.Port);
        var b = new TestClient(_server.Port);

        RunUntil(() => a.Welcome is not null && b.Welcome is not null, a, b);
        var bUnit = b.Welcome!.Value.UnitId;
        RunUntil(() => a.LastSnapshot.ContainsKey(bUnit), a, b);

        b.Dispose();
        RunUntil(() => _server.PlayerCount == 1 && !a.LastSnapshot.ContainsKey(bUnit), a);
    }

    [Fact]
    public void WrongProtocolVersion_IsRejected()
    {
        using var client = new TestClient(_server.Port, protocolVersion: SimConstants.ProtocolVersion + 1);

        RunUntil(() => client.Disconnected, client);
        Assert.Null(client.Welcome);
        Assert.Equal(0, _server.PlayerCount);
    }

    [Fact]
    public void SettingsChange_IsBroadcast_AndManyUnitsArriveInSplitSnapshots()
    {
        using var a = new TestClient(_server.Port);
        using var b = new TestClient(_server.Port);
        RunUntil(() => a.Settings is not null && b.Settings is not null, a, b);

        var requested = a.Settings!.Value;
        requested.BotCount = 450; // 스냅샷 3조각
        requested.Scenario = BotScenario.CircleSwap;
        requested.UnitRadius = 0.4f;
        a.SendSettings(requested);

        // 두 클라이언트 모두 새 설정을 받고, 봇 450 + 플레이어 2 가 담긴 완성 스냅샷을 받는다.
        RunUntil(() => b.Settings is { BotCount: 450 } && b.LastSnapshot.Count == 452 && a.LastSnapshot.Count == 452, a, b);
        Assert.Equal(BotScenario.CircleSwap, b.Settings!.Value.Scenario);
        Assert.Equal(0.4f, b.Settings!.Value.UnitRadius);

        RunUntil(() => b.Stats is { UnitCount: 452 }, a, b);
    }

    private void RunUntil(Func<bool> condition, params TestClient[] clients)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            _server.Tick(_tick++);
            foreach (var c in clients)
            {
                c.Poll();
            }

            Thread.Sleep(2);
        }
    }

    private sealed class TestClient : IDisposable
    {
        private readonly NetManager _net;
        private readonly PacketWriter _writer = new();
        private readonly List<UnitState> _part = new();
        private readonly SnapshotAssembler _assembler = new();
        private NetPeer? _peer;

        public TestClient(int port, int protocolVersion = SimConstants.ProtocolVersion)
        {
            var listener = new EventBasedNetListener();
            listener.PeerConnectedEvent += peer =>
            {
                _peer = peer;
                _writer.Reset();
                new ClientHello { ProtocolVersion = protocolVersion }.Write(_writer);
                peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
            };
            listener.PeerDisconnectedEvent += (_, _) => Disconnected = true;
            listener.NetworkReceiveEvent += (_, reader, _, _) =>
            {
                var r = new PacketReader(reader.RawData, reader.Position, reader.AvailableBytes);
                switch ((MessageType)r.ReadByte())
                {
                    case MessageType.ServerWelcome:
                        Welcome = ServerWelcome.Read(ref r);
                        break;
                    case MessageType.SimSettings:
                        Settings = SimSettings.Read(ref r);
                        break;
                    case MessageType.SimStats:
                        Stats = SimStats.Read(ref r);
                        break;
                    case MessageType.Snapshot:
                        var tick = SnapshotMessage.ReadPart(ref r, _part, out var part, out var count);
                        if (_assembler.Add(tick, part, count, _part, out var done, out var units))
                        {
                            LastSnapshotTick = done;
                            LastSnapshot = units.ToDictionary(u => u.Id, u => u.Position);
                        }

                        break;
                }
            };

            _net = new NetManager(listener) { AutoRecycle = true };
            _net.Start();
            _net.Connect("127.0.0.1", port, SimConstants.ConnectionKey);
        }

        public ServerWelcome? Welcome { get; private set; }
        public SimSettings? Settings { get; private set; }
        public SimStats? Stats { get; private set; }
        public Dictionary<int, Vector2> LastSnapshot { get; private set; } = new();
        public uint LastSnapshotTick { get; private set; }
        public bool Disconnected { get; private set; }

        public void Poll() => _net.PollEvents();

        public void SendMove(Vector2 target)
        {
            _writer.Reset();
            new MoveCommand { Target = target }.Write(_writer);
            _peer!.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
        }

        public void SendSettings(SimSettings settings)
        {
            _writer.Reset();
            settings.Write(_writer, MessageType.SetSimSettings);
            _peer!.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
        }

        public void Dispose() => _net.Stop();
    }
}
