using LiteNetLib;
using OpenRTS.Server.Simulation;
using OpenRTS.Shared;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Server.Net;

/// <summary>
/// 네트워크와 시뮬레이션을 묶은 서버. <see cref="Tick"/> 을 고정 틱마다 한 번 호출한다.
/// LiteNetLib 이벤트는 <see cref="Tick"/> 안의 PollEvents 에서만 발생하므로 별도 동기화가 필요 없다.
/// </summary>
public sealed class GameServer : IDisposable
{
    // Unreliable 패킷 한 개에 들어가는 유닛 수 (헤더 7B + 유닛당 12B, MTU 여유 포함).
    // 넘으면 잘린다. 양자화·우선순위는 로드맵 3단계.
    private const int MaxUnitsPerSnapshot = 100;

    private readonly NetManager _net;
    private readonly Dictionary<int, int> _peerUnits = new(); // peer.Id → unitId
    private readonly List<NetPeer> _peers = new();
    private readonly PacketWriter _writer = new();
    private readonly List<UnitState> _snapshotUnits = new();
    private readonly Action<string> _log;

    public GameServer(World world, Action<string>? log = null)
    {
        World = world;
        _log = log ?? (_ => { });

        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += request => request.AcceptIfKey(SimConstants.ConnectionKey);
        listener.PeerConnectedEvent += peer => _log($"peer {peer.Id} connected from {peer}");
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnReceive;

        _net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = 10_000 };
    }

    public World World { get; }

    public uint CurrentTick { get; private set; }

    /// <summary>실제로 바인딩된 포트 (0 으로 시작하면 OS가 고른 포트).</summary>
    public int Port => _net.LocalPort;

    public int PlayerCount => _peerUnits.Count;

    public void Start(int port)
    {
        if (!_net.Start(port))
        {
            throw new InvalidOperationException($"Failed to bind UDP port {port}.");
        }

        _log($"listening on UDP {_net.LocalPort}");
    }

    public void Tick(uint tick)
    {
        CurrentTick = tick;
        _net.PollEvents();
        World.Step(SimConstants.TickDelta);

        if (tick % SimConstants.TicksPerSnapshot == 0)
        {
            BroadcastSnapshot(tick);
        }
    }

    public void Dispose() => _net.Stop();

    private void BroadcastSnapshot(uint tick)
    {
        if (_peerUnits.Count == 0)
        {
            return;
        }

        var state = World.CaptureState();
        _snapshotUnits.Clear();
        for (var i = 0; i < state.Count && i < MaxUnitsPerSnapshot; i++)
        {
            _snapshotUnits.Add(state[i]);
        }

        _writer.Reset();
        SnapshotMessage.Write(_writer, tick, _snapshotUnits);

        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (_peerUnits.ContainsKey(peer.Id))
            {
                peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
            }
        }
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            var r = new PacketReader(reader.RawData, reader.Position, reader.AvailableBytes);
            var type = (MessageType)r.ReadByte();
            switch (type)
            {
                case MessageType.ClientHello:
                    HandleHello(peer, ClientHello.Read(ref r));
                    break;
                case MessageType.MoveCommand:
                    if (_peerUnits.TryGetValue(peer.Id, out var unitId))
                    {
                        World.SetMoveTarget(unitId, MoveCommand.Read(ref r).Target);
                    }

                    break;
                default:
                    _log($"peer {peer.Id}: unexpected message {type}");
                    break;
            }
        }
        catch (PacketFormatException e)
        {
            _log($"peer {peer.Id}: malformed packet ({e.Message}), disconnecting");
            peer.Disconnect();
        }
    }

    private void HandleHello(NetPeer peer, ClientHello hello)
    {
        if (hello.ProtocolVersion != SimConstants.ProtocolVersion)
        {
            _log($"peer {peer.Id}: protocol {hello.ProtocolVersion} != {SimConstants.ProtocolVersion}, rejecting");
            peer.Disconnect();
            return;
        }

        if (_peerUnits.ContainsKey(peer.Id))
        {
            return;
        }

        var unit = World.SpawnRandom();
        _peerUnits[peer.Id] = unit.Id;

        _writer.Reset();
        new ServerWelcome
        {
            UnitId = unit.Id,
            ServerTick = CurrentTick,
            TickRate = SimConstants.TickRate,
            SnapshotRate = SimConstants.SnapshotRate,
        }.Write(_writer);
        peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
        _log($"peer {peer.Id} joined as unit {unit.Id}");
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_peerUnits.Remove(peer.Id, out var unitId))
        {
            World.Remove(unitId);
        }

        _log($"peer {peer.Id} disconnected ({info.Reason})");
    }
}
