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

    /// <summary>델타 기준으로 쓸 수 있는 과거 스냅샷 수 (15Hz 기준 약 4초).</summary>
    public const int HistorySize = 64;

    private sealed class Session
    {
        public byte PlayerId;
        public uint LastAck;
    }

    private readonly NetManager _net;
    private readonly Dictionary<int, Session> _sessions = new(); // peer.Id → 세션 (입장한 피어만)
    private readonly List<NetPeer> _peers = new();
    private readonly PacketWriter _writer = new();
    private readonly Action<string> _log;
    private readonly Stopwatch _stepWatch = new();
    private readonly SnapshotHistory _history = new(HistorySize);
    private readonly List<SnapshotEntry> _current = new();
    private readonly Dictionary<uint, List<SnapshotEntry>> _deltas = new(); // baseline 틱 → 변경분 (한 스냅샷 안에서 재사용)
    private readonly Stack<List<SnapshotEntry>> _deltaPool = new();
    private readonly List<(int Start, int Count)> _parts = new();
    private readonly List<CombatEvent> _pendingEvents = new();
    private float _commandDelayMs;
    private double _stepMsTotal;
    private int _stepSamples;
    private long _snapshotBytes;
    private int _snapshotSends;
    private int _deltaSends;

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

    public int PlayerCount => _sessions.Count;

    /// <summary>시작 후 보낸 스냅샷 바이트 합계 (모든 피어).</summary>
    public long TotalSnapshotBytes { get; private set; }

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
        World.Step(SimConstants.TickDelta, tick);
        _stepMsTotal += _stepWatch.Elapsed.TotalMilliseconds;
        _stepSamples++;

        _pendingEvents.AddRange(World.Combat.Events);
        World.Combat.ClearEvents();

        if (tick % SimConstants.TicksPerSnapshot == 0)
        {
            BroadcastSnapshot(tick);
            BroadcastCombatEvents();
        }

        if (tick % TicksPerStats == 0)
        {
            BroadcastStats();
        }
    }

    public void Dispose() => _net.Stop();

    /// <summary>최근 보낸 스냅샷 상태 (ID 순, 양자화 값). 테스트·디버그용.</summary>
    public bool TryGetSentSnapshot(uint tick, out IReadOnlyList<SnapshotEntry> state) => _history.TryGet(tick, out state);


    private void BroadcastSnapshot(uint tick)
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        _current.Clear();
        foreach (var u in World.CaptureState())
        {
            _current.Add(SnapshotEntry.From(u));
        }

        _current.Sort(SnapshotDelta.ById);

        foreach (var list in _deltas.Values)
        {
            _deltaPool.Push(list);
        }

        _deltas.Clear();
        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                continue;
            }

            // 기준: 클라이언트가 마지막으로 확인한 스냅샷. 없거나 너무 오래됐으면 전체를 보낸다.
            var baseTick = SnapshotMessage.NoBaseline;
            IReadOnlyList<SnapshotEntry> entries = _current;
            if (World.Settings.DeltaSnapshots && _history.TryGet(session.LastAck, out var baseline))
            {
                baseTick = session.LastAck;
                if (!_deltas.TryGetValue(baseTick, out var delta))
                {
                    delta = _deltaPool.Count > 0 ? _deltaPool.Pop() : new List<SnapshotEntry>();
                    SnapshotDelta.Diff(baseline, _current, delta);
                    _deltas.Add(baseTick, delta);
                }

                entries = delta;
                _deltaSends++;
            }

            _snapshotSends++;

            // 조각 크기는 이 피어가 쪼개지 않고 받을 수 있는 크기에 맞춘다 (MTU 탐색 전에는 작다).
            SnapshotMessage.Plan(entries, peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable), _parts);
            for (var part = 0; part < _parts.Count; part++)
            {
                _writer.Reset();
                SnapshotMessage.WritePart(_writer, tick, baseTick, entries, _parts[part].Start, _parts[part].Count, part, _parts.Count);
                peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
                _snapshotBytes += _writer.Length;
                TotalSnapshotBytes += _writer.Length;
            }
        }

        _history.Store(tick, _current);
    }

    /// <summary>쌓인 전투 이벤트를 연출용으로 보낸다 (unreliable, 패킷 크기에 맞춰 나눔).</summary>
    private void BroadcastCombatEvents()
    {
        if (_pendingEvents.Count == 0 || _sessions.Count == 0)
        {
            _pendingEvents.Clear();
            return;
        }

        for (var start = 0; start < _pendingEvents.Count; start += CombatEventsMessage.MaxPerPacket)
        {
            _writer.Reset();
            CombatEventsMessage.Write(_writer, _pendingEvents, start, Math.Min(CombatEventsMessage.MaxPerPacket, _pendingEvents.Count - start));
            SendToJoined(DeliveryMethod.Unreliable);
        }

        _pendingEvents.Clear();
    }

    private void BroadcastStats()
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        var stats = World.ComputeStats();
        stats.StepMs = _stepSamples > 0 ? (float)(_stepMsTotal / _stepSamples) : 0f;
        stats.SnapshotBytesPerSec = (float)(_snapshotBytes / (TicksPerStats * (double)SimConstants.TickDelta) / _sessions.Count);
        stats.DeltaRatio = _snapshotSends > 0 ? (float)_deltaSends / _snapshotSends : 0f;
        stats.CommandDelayMs = _commandDelayMs;
        _stepMsTotal = 0;
        _stepSamples = 0;
        _snapshotBytes = 0;
        _snapshotSends = 0;
        _deltaSends = 0;

        _writer.Reset();
        stats.Write(_writer);
        SendToJoined(DeliveryMethod.Unreliable);
    }

    private void BroadcastSettings()
    {
        _writer.Reset();
        World.Settings.Write(_writer, MessageType.SimSettings);
        SendToJoined(DeliveryMethod.ReliableOrdered);
    }

    /// <summary>_writer 내용을 입장한 피어 모두에게 보낸다.</summary>
    private void SendToJoined(DeliveryMethod method)
    {
        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (_sessions.ContainsKey(peer.Id))
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

            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                return; // 입장 전 메시지는 무시
            }

            switch (type)
            {
                case MessageType.UnitCommand:
                    var command = UnitCommand.Read(ref r);
                    World.Command(session.PlayerId, command);
                    if (command.ClientTick != 0 && command.ClientTick <= CurrentTick + SimConstants.TickRate)
                    {
                        // 클라이언트가 추정한 서버 틱과 실제 받은 틱의 차이 = 대략 편도 지연 (최근 값 쪽으로 이동 평균)
                        var delayMs = ((long)CurrentTick - command.ClientTick) * SimConstants.TickDelta * 1000f;
                        _commandDelayMs = _commandDelayMs == 0 ? delayMs : _commandDelayMs * 0.7f + delayMs * 0.3f;
                    }

                    break;

                case MessageType.SnapshotAck:
                    var ack = SnapshotAck.Read(ref r).Tick;
                    if (ack > session.LastAck && ack <= CurrentTick)
                    {
                        session.LastAck = ack;
                    }

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

        if (_sessions.ContainsKey(peer.Id))
        {
            return;
        }

        if (!TryAllocatePlayerId(out var playerId))
        {
            _log($"peer {peer.Id}: server full, rejecting");
            peer.Disconnect();
            return;
        }

        var units = World.AddPlayer(playerId);
        _sessions[peer.Id] = new Session { PlayerId = playerId };

        _writer.Reset();
        new ServerWelcome
        {
            PlayerId = playerId,
            ServerTick = CurrentTick,
            TickRate = SimConstants.TickRate,
            SnapshotRate = SimConstants.SnapshotRate,
        }.Write(_writer);
        peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);

        _writer.Reset();
        World.Settings.Write(_writer, MessageType.SimSettings);
        peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
        _log($"peer {peer.Id} joined as player {playerId} (team {World.TeamOf(playerId)}) with {units.Count} units");

    }

    private bool TryAllocatePlayerId(out byte playerId)
    {
        for (var id = 1; id < byte.MaxValue; id++)
        {
            if (_sessions.Values.All(s => s.PlayerId != id))
            {
                playerId = (byte)id;
                return true;
            }
        }

        playerId = 0;
        return false;
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_sessions.Remove(peer.Id, out var session))
        {
            World.RemovePlayer(session.PlayerId);
        }

        _log($"peer {peer.Id} disconnected ({info.Reason})");
    }
}
