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
    private readonly Dictionary<byte, SnapshotHistory> _teamHistory = new(); // 팀 → 그 팀에게 보낸 상태
    private readonly Dictionary<byte, List<SnapshotEntry>> _teamStates = new();
    private readonly HashSet<byte> _teams = new();
    private readonly Dictionary<(byte Team, uint BaseTick), List<SnapshotEntry>> _deltas = new(); // 한 스냅샷 안에서 재사용
    private readonly Stack<List<SnapshotEntry>> _deltaPool = new();
    private readonly List<(int Start, int Count)> _parts = new();
    private readonly List<CombatEvent> _pendingEvents = new();
    private readonly List<(System.Numerics.Vector2 Attacker, System.Numerics.Vector2 Target)> _pendingEventPositions = new();
    private readonly List<CombatEvent> _visibleEvents = new();
    private readonly List<AbilityEvent> _pendingAbilityEvents = new();
    private readonly List<(System.Numerics.Vector2 Caster, System.Numerics.Vector2 Point)> _pendingAbilityPositions = new();
    private readonly List<AbilityEvent> _visibleAbilityEvents = new();
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
        _pendingEventPositions.AddRange(World.Combat.EventPositions);
        World.Combat.ClearEvents();
        _pendingAbilityEvents.AddRange(World.AbilitySystem.Events);
        _pendingAbilityPositions.AddRange(World.AbilitySystem.EventPositions);
        World.AbilitySystem.ClearEvents();

        if (tick % SimConstants.TicksPerSnapshot == 0)
        {
            BroadcastSnapshot(tick);
            BroadcastCombatEvents();
            BroadcastAbilityEvents();
        }

        if (tick % TicksPerStats == 0)
        {
            BroadcastStats();
        }

        if (tick % TicksPerPlayerState == 0)
        {
            SendPlayerStates();
        }
    }

    /// <summary>플레이어별 경제 상태(자원·생산 대기열) 전송 간격 (틱, 5Hz).</summary>
    public const int TicksPerPlayerState = SimConstants.TickRate / 5;

    /// <summary>각 플레이어에게 자기 자원과 자기 건물의 생산 대기열만 보낸다.</summary>
    private void SendPlayerStates()
    {
        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                continue;
            }

            _writer.Reset();
            new PlayerStateMessage
            {
                Ore = World.EconomySystem.OreOf(session.PlayerId),
                Queues = World.EconomySystem.QueuesOf(session.PlayerId),
                Cooldowns = World.AbilitySystem.CooldownsOf(session.PlayerId),
            }.Write(_writer);
            peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
        }
    }

    public void Dispose() => _net.Stop();

    /// <summary>최근 <paramref name="team"/> 에게 보낸 스냅샷 상태 (ID 순, 양자화 값). 테스트·디버그용.</summary>
    public bool TryGetSentSnapshot(byte team, uint tick, out IReadOnlyList<SnapshotEntry> state)
    {
        if (_teamHistory.TryGetValue(team, out var history))
        {
            return history.TryGet(tick, out state);
        }

        state = Array.Empty<SnapshotEntry>();
        return false;
    }

    /// <summary>
    /// 스냅샷 전송. 팀마다 그 팀이 볼 수 있는 유닛만 담는다 (Fog of War 전송 필터).
    /// 보이지 않는 유닛을 노리는 공격 대상 ID 도 0 으로 가린다. 델타 기준도 팀별 보낸 상태로 따로 보관한다.
    /// </summary>
    private void BroadcastSnapshot(uint tick)
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        var all = World.CaptureState();
        _teams.Clear();
        foreach (var session in _sessions.Values)
        {
            _teams.Add(World.TeamOf(session.PlayerId));
        }

        foreach (var team in _teams)
        {
            if (!_teamStates.TryGetValue(team, out var list))
            {
                _teamStates[team] = list = new List<SnapshotEntry>();
                _teamHistory[team] = new SnapshotHistory(HistorySize);
            }

            list.Clear();
            foreach (var u in all)
            {
                var unit = World.Find(u.Id);
                if (unit == null || !World.CanSee(team, unit))
                {
                    continue;
                }

                var entry = SnapshotEntry.From(u);
                if (entry.AttackTarget != 0 && (World.Find(entry.AttackTarget) is not { } target || !World.CanSee(team, target)))
                {
                    entry.AttackTarget = 0; // 안개 속 유닛의 ID 를 흘리지 않는다
                }

                list.Add(entry);
            }

            list.Sort(SnapshotDelta.ById);
        }

        foreach (var delta in _deltas.Values)
        {
            _deltaPool.Push(delta);
        }

        _deltas.Clear();
        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                continue;
            }

            var team = World.TeamOf(session.PlayerId);
            var current = _teamStates[team];

            // 기준: 클라이언트가 마지막으로 확인한 스냅샷. 없거나 너무 오래됐으면 전체를 보낸다.
            var baseTick = SnapshotMessage.NoBaseline;
            IReadOnlyList<SnapshotEntry> entries = current;
            if (World.Settings.DeltaSnapshots && _teamHistory[team].TryGet(session.LastAck, out var baseline))
            {
                baseTick = session.LastAck;
                if (!_deltas.TryGetValue((team, baseTick), out var delta))
                {
                    delta = _deltaPool.Count > 0 ? _deltaPool.Pop() : new List<SnapshotEntry>();
                    SnapshotDelta.Diff(baseline, current, delta);
                    _deltas.Add((team, baseTick), delta);
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

        foreach (var team in _teams)
        {
            _teamHistory[team].Store(tick, _teamStates[team]);
        }
    }

    /// <summary>
    /// 쌓인 전투 이벤트를 연출용으로 보낸다 (unreliable, 패킷 크기에 맞춰 나눔).
    /// 팀마다 공격자나 대상 위치가 그 팀 시야 안인 이벤트만 보낸다.
    /// </summary>
    private void BroadcastCombatEvents()
    {
        if (_pendingEvents.Count == 0 || _sessions.Count == 0)
        {
            _pendingEvents.Clear();
            _pendingEventPositions.Clear();
            return;
        }

        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                continue;
            }

            var team = World.TeamOf(session.PlayerId);
            _visibleEvents.Clear();
            for (var i = 0; i < _pendingEvents.Count; i++)
            {
                var (a, t) = _pendingEventPositions[i];
                var seeAttacker = World.CanSee(team, a);
                var seeTarget = World.CanSee(team, t);
                if (seeAttacker || seeTarget)
                {
                    // 한쪽만 보이면 안 보이는 쪽 ID 는 가린다 (위치를 모르니 연출에도 쓸 수 없다).
                    var e = _pendingEvents[i];
                    if (!seeAttacker) e.Attacker = 0;
                    if (!seeTarget) e.Target = 0;
                    _visibleEvents.Add(e);
                }

            }

            for (var start = 0; start < _visibleEvents.Count; start += CombatEventsMessage.MaxPerPacket)
            {
                _writer.Reset();
                CombatEventsMessage.Write(_writer, _visibleEvents, start, Math.Min(CombatEventsMessage.MaxPerPacket, _visibleEvents.Count - start));
                peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
            }
        }

        _pendingEvents.Clear();
        _pendingEventPositions.Clear();
    }

    /// <summary>
    /// 능력 연출 이벤트. 팀마다 시전자나 지점이 그 팀 시야 안인 것만 보내고,
    /// 안 보이는 시전자·대상 ID 는 0 으로 가린다 (지점만 보여도 예고 범위·폭발은 보인다).
    /// </summary>
    private void BroadcastAbilityEvents()
    {
        if (_pendingAbilityEvents.Count == 0 || _sessions.Count == 0)
        {
            _pendingAbilityEvents.Clear();
            _pendingAbilityPositions.Clear();
            return;
        }

        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                continue;
            }

            var team = World.TeamOf(session.PlayerId);
            _visibleAbilityEvents.Clear();
            for (var i = 0; i < _pendingAbilityEvents.Count; i++)
            {
                var (casterPos, point) = _pendingAbilityPositions[i];
                var seeCaster = World.CanSee(team, casterPos);
                var seePoint = World.CanSee(team, point);
                if (!seeCaster && !seePoint)
                {
                    continue;
                }

                var e = _pendingAbilityEvents[i];
                if (!seeCaster) e.Caster = 0;
                if (e.Target != 0 && (World.Find(e.Target) is not { } t || !World.CanSee(team, t)) && !seePoint) e.Target = 0;
                _visibleAbilityEvents.Add(e);
            }

            for (var start = 0; start < _visibleAbilityEvents.Count; start += AbilityEventsMessage.MaxPerPacket)
            {
                _writer.Reset();
                AbilityEventsMessage.Write(_writer, _visibleAbilityEvents, start, Math.Min(AbilityEventsMessage.MaxPerPacket, _visibleAbilityEvents.Count - start));
                peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
            }
        }

        _pendingAbilityEvents.Clear();
        _pendingAbilityPositions.Clear();
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

        // Fog of War 가 켜져 있으면 상대 팀 정보(생존 수·사망·투사체)를 "모름"으로 가리고,
        // 유닛 수는 그 팀이 볼 수 있는 수로 보낸다. 겹침 지표는 디버그용이라 전체 기준 그대로.
        _net.GetConnectedPeers(_peers);
        foreach (var peer in _peers)
        {
            if (!_sessions.TryGetValue(peer.Id, out var session))
            {
                continue;
            }

            var s = stats;
            if (World.Settings.FogOfWar)
            {
                var team = World.TeamOf(session.PlayerId);
                s.UnitCount = (ushort)Math.Min(ushort.MaxValue, World.Units.Count(u => World.CanSee(team, u)));
                if (team != 1) s.Team1Alive = SimStats.Unknown;
                if (team != 2) s.Team2Alive = SimStats.Unknown;
                s.Deaths = SimStats.Unknown;
                s.Projectiles = SimStats.Unknown;
            }

            _writer.Reset();
            s.Write(_writer);
            peer.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
        }
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
                    var delaySeconds = CommandDelay(command.ClientTick);
                    World.Command(session.PlayerId, command, delaySeconds);
                    if (command.ClientTick != 0)
                    {
                        var delayMs = delaySeconds * 1000f;
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

    /// <summary>
    /// 명령 지연(초) = 받은 서버 틱 − 명령의 ClientTick. ClientTick 은 클라이언트가 그 순간 추정한 서버 시각인데,
    /// 그 추정은 마지막으로 받은 스냅샷 기준이라 내려오는 지연만큼 늦다. 그래서 이 값은 편도보다 크고 왕복에 가깝다
    /// (= "플레이어가 본 화면 시각부터 서버가 받을 때까지"). 미래 틱이거나 비어 있으면 0, 1초를 넘으면 1초로 자른다.
    /// 쓰는 쪽(선딜 보상)이 따로 상한을 둔다.
    /// </summary>
    private float CommandDelay(uint clientTick)
    {
        if (clientTick == 0 || clientTick > CurrentTick)
        {
            return 0f;
        }

        return MathF.Min(1f, (CurrentTick - clientTick) * SimConstants.TickDelta);
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
