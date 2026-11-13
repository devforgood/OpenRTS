using System.Numerics;
using LiteNetLib;
using OpenRTS.Server.Net;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Server.Tests;

/// <summary>실제 UDP 소켓으로 서버와 테스트 클라이언트를 띄워 이동 동기화 흐름을 확인한다.</summary>
public sealed class GameServerIntegrationTests : IDisposable
{
    private readonly GameServer _server;
    private uint _tick;

    public GameServerIntegrationTests()
    {
        _server = new GameServer(new World());
        _server.Start(0);
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public void Client_Joins_Moves_AndSeesItsPositionInSnapshots()
    {
        using var client = new TestClient(_server.Port);

        RunUntil(() => client.Welcome is not null, client);
        var unitId = client.Welcome!.Value.UnitId;
        var start = _server.World.Find(unitId)!.Position;
        var target = start + new Vector2(3, 0);

        client.SendMove(target);
        // 3유닛 / 6유닛/초 = 0.5초 = 15틱. 여유를 두고 기다린다.
        RunUntil(() => client.LastSnapshot.TryGetValue(unitId, out var p) && Vector2.Distance(p, target) < 0.001f, client);

        Assert.Equal(target, _server.World.Find(unitId)!.Position);
        // 스냅샷은 TicksPerSnapshot 틱마다만 온다.
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

    private void RunUntil(Func<bool> condition, params TestClient[] clients)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
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
        private readonly List<UnitState> _units = new();
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
                    case MessageType.Snapshot:
                        LastSnapshotTick = SnapshotMessage.Read(ref r, _units);
                        LastSnapshot = _units.ToDictionary(u => u.Id, u => u.Position);
                        break;
                }
            };

            _net = new NetManager(listener) { AutoRecycle = true };
            _net.Start();
            _net.Connect("127.0.0.1", port, SimConstants.ConnectionKey);
        }

        public ServerWelcome? Welcome { get; private set; }
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

        public void Dispose() => _net.Stop();
    }
}
