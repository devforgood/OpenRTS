using System.Diagnostics;
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
    /// <summary>지표(SimStats) 전송 간격 (틱).</summary>
    public const int TicksPerStats = SimConstants.TickRate / 2;

    private readonly NetManager _net;
    private readonly Dictionary<int, int> _peerUnits = new(); // peer.Id → unitId
    private readonly List<NetPeer> _peers = new();
    private readonly PacketWriter _writer = new();
    private readonly Action<string> _log;
    private readonly Stopwatch _stepWatch = new();
    private double _stepMsTotal;
    private int _stepSamples;

    public GameServer(World world, Action<string>? log = null, bool allowDebugCommands = true)
    {
        World = world;
        AllowDebugCommands = allowDebugCommands;
        _log = log ?? (_ => { });

        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += request => request.AcceptIfKey(SimConstants.ConnectionKey);
        listener.PeerConnectedEvent += peer => _log($"peer {peer.Id} connected from {peer}");
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnReceive;

        _net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = 10_000 };
    }

    public World World { get; }

    /// <summary>클라이언트의 설정 변경·시나리오 재시작 요청을 받을지.</summary>
    public bool AllowDebugCommands { get; }

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

        _stepWatch.Restart();
        World.Step(SimConstants.TickDelta);
        _stepMsTotal += _stepWatch.Elapsed.TotalMilliseconds;
        _stepSamples++;

        if (tick % SimConstants.TicksPerSnapshot == 0)
        {
            BroadcastSnapshot(tick);
        }

        if (tick % TicksPerStats == 0)
        {
            BroadcastStats();
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
        _net.GetConnectedPeers(_peers);

        // 조각 크기는 모든 피어가 쪼개지 않고 받을 수 있는 크기에 맞춘다 (MTU 탐색 전에는 작다).
        var maxPacket = int.MaxValue;
        foreach (var peer in _peers)
        {
            if (_peerUnits.ContainsKey(peer.Id))
            {
                maxPacket = Math.Min(maxPacket, peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable));
            }
        }

        var unitsPerPart = SnapshotMessage.UnitsPerPart(maxPacket);
        var parts = SnapshotMessage.PartCount(state.Count, unitsPerPart);
        for (var part = 0; part < parts; part++)
        {
            _writer.Reset();
            SnapshotMessage.WritePart(_writer, tick, state, part, unitsPerPart);
            SendToJoined(DeliveryMethod.Unreliable);
        }
    }

    private void BroadcastStats()
    {
        if (_peerUnits.Count == 0)
        {
            return;
        }

        var stats = World.ComputeStats();
        stats.StepMs = _stepSamples > 0 ? (float)(_stepMsTotal / _stepSamples) : 0f;
        _stepMsTotal = 0;
        _stepSamples = 0;

        _writer.Reset();
        stats.Write(_writer);
        _net.GetConnectedPeers(_peers);
        SendToJoined(DeliveryMethod.Unreliable);
    }

    private void BroadcastSettings()
    {
        _writer.Reset();
        World.Settings.Write(_writer, MessageType.SimSettings);
        _net.GetConnectedPeers(_peers);
        SendToJoined(DeliveryMethod.ReliableOrdered);
    }

    /// <summary>_writer 내용을 _peers 중 입장한 피어에게 보낸다.</summary>
    private void SendToJoined(DeliveryMethod method)
    {
        foreach (var peer in _peers)
        {
            if (_peerUnits.ContainsKey(peer.Id))
            {
                peer.Send(_writer.Buffer, 0, _writer.Length, method);
            }
        }
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            var r = new PacketReader(reader.RawData, reader.Position, reader.AvailableBytes);
            var type = (MessageType)r.ReadByte();
            if (type == MessageType.ClientHello)
            {
                HandleHello(peer, ClientHello.Read(ref r));
                return;
            }

            if (!_peerUnits.TryGetValue(peer.Id, out var unitId))
            {
                return; // 입장 전 메시지는 무시
            }

            switch (type)
            {
                case MessageType.MoveCommand:
                    World.SetMoveTarget(unitId, MoveCommand.Read(ref r).Target);
                    break;

                case MessageType.SetSimSettings:
                    var requested = SimSettings.Read(ref r);
                    if (AllowDebugCommands)
                    {
                        var applied = World.ApplySettings(requested);
                        _log($"peer {peer.Id} changed settings: {applied}");
                        BroadcastSettings();
                    }

                    break;

                case MessageType.ResetScenario:
                    if (AllowDebugCommands)
                    {
                        World.ResetScenario();
                        _log($"peer {peer.Id} reset scenario {World.Settings.Scenario}");
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

        _writer.Reset();
        World.Settings.Write(_writer, MessageType.SimSettings);
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
