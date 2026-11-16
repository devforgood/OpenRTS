using System;
using System.Collections.Generic;
using LiteNetLib;
using OpenRTS.Shared;
using OpenRTS.Shared.Interpolation;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Net
{
    public enum ClientState
    {
        Connecting,
        Joined,
        Disconnected,
    }

    /// <summary>
    /// 서버 접속, 패킷 송수신, 스냅샷 보간기 관리.
    /// 테스트용으로 지연·손실을 흉내 낼 수 있다 (LiteNetLib 내장 기능은 DEBUG 빌드 전용이라 직접 구현).
    /// </summary>
    public sealed class GameClient : MonoBehaviour
    {
        private struct DelayedPacket
        {
            public double ReleaseTime;
            public byte[] Data;
            public DeliveryMethod Method;
        }

        public string Host = "127.0.0.1";
        public int Port = SimConstants.DefaultPort;

        [Tooltip("인위적으로 추가하는 왕복 지연(ms). 절반씩 송신·수신에 적용.")]
        public int SimulatedRttMs;

        [Tooltip("수신한 unreliable 패킷(스냅샷)을 버리는 확률(%).")]
        [Range(0, 50)]
        public int SimulatedLossPercent;

        private readonly PacketWriter _writer = new PacketWriter();
        private readonly SnapshotReceiver _receiver = new SnapshotReceiver();
        private readonly List<int> _myUnits = new List<int>();
        private readonly List<CombatEvent> _eventBuffer = new List<CombatEvent>();
        private readonly List<CombatEvent> _pendingEvents = new List<CombatEvent>();
        private readonly Queue<DelayedPacket> _inbound = new Queue<DelayedPacket>();
        private readonly Queue<DelayedPacket> _outbound = new Queue<DelayedPacket>();
        private readonly System.Random _random = new System.Random();
        private NetManager _net;
        private NetPeer _peer;

        public ClientState State { get; private set; } = ClientState.Connecting;
        /// <summary>서버가 정해 준 플레이어 번호 (입장 전 0). 스냅샷 Owner 가 이 값이면 내 유닛.</summary>
        public byte PlayerId { get; private set; }

        public SnapshotInterpolator Interpolator { get; private set; }

        /// <summary>보간하지 않은 최신 스냅샷 (디버그 비교용).</summary>
        public Dictionary<int, Vector2> LatestRaw { get; } = new Dictionary<int, Vector2>();

        /// <summary>최신 스냅샷의 유닛 상태 (소유자·팀·종류·체력·공격 대상). 위치는 보간 전 값.</summary>
        public Dictionary<int, UnitState> Latest { get; } = new Dictionary<int, UnitState>();

        /// <summary>내 팀 (내 유닛이 없으면 0). 서버가 입장 순서대로 1팀·2팀에 나눈다.</summary>
        public byte MyTeam { get; private set; }

        /// <summary>렌더 시점이 된 전투 이벤트 (보간 지연에 맞춰 늦게 재생). 받는 쪽이 비운다.</summary>
        public List<CombatEvent> DueCombatEvents { get; } = new List<CombatEvent>();

        public int CombatEventsReceived { get; private set; }

        /// <summary>내 자원 (서버가 5Hz 로 보냄).</summary>
        public int Ore { get; private set; }

        /// <summary>내 건물별 생산 대기열 (건물 ID → 대기열). 대기열이 빈 건물은 없다.</summary>
        public Dictionary<int, ProductionQueue> Queues { get; } = new Dictionary<int, ProductionQueue>();

        public bool HasPlayerState { get; private set; }

        /// <summary>최신 스냅샷 기준 내 유닛 ID (오름차순).</summary>
        public IReadOnlyList<int> MyUnits => _myUnits;

        /// <summary>LiteNetLib 가 잰 실제 왕복 지연 (시뮬레이션 지연 제외).</summary>
        public int NetworkRttMs => _peer != null ? _peer.RoundTripTime : 0;

        public int SnapshotsReceived { get; private set; }
        public int SnapshotsDroppedBySimulation { get; private set; }

        /// <summary>조각이 다 모이지 않아 버린 스냅샷 수.</summary>
        public int SnapshotsIncomplete => _receiver.IncompleteDropped;

        /// <summary>기준 스냅샷이 없어 버린 델타 수 (기준이 오래돼 지워졌을 때).</summary>
        public int SnapshotsMissingBaseline => _receiver.MissingBaseline;

        public int DeltaSnapshotsReceived => _receiver.DeltaReceived;

        /// <summary>서버의 현재 시뮬레이션 설정 (입장 전에는 null).</summary>
        public SimSettings? Settings { get; private set; }

        /// <summary>서버가 보낸 최신 충돌 지표.</summary>
        public SimStats? Stats { get; private set; }

        public event Action Joined;
        public event Action<SimSettings> SettingsChanged;

        private void Start()
        {
            ReadCommandLine();
            Interpolator = new SnapshotInterpolator(SimConstants.TickDelta, 2f / SimConstants.SnapshotRate);

            var listener = new EventBasedNetListener();
            listener.PeerConnectedEvent += OnConnected;
            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                State = ClientState.Disconnected;
                Debug.Log($"[OpenRTS] disconnected: {info.Reason}");
            };
            listener.NetworkReceiveEvent += OnReceive;

            _net = new NetManager(listener) { AutoRecycle = true };
            _net.Start();
            _net.Connect(Host, Port, SimConstants.ConnectionKey);
            Debug.Log($"[OpenRTS] connecting to {Host}:{Port}");
        }

        private void Update()
        {
            _net?.PollEvents();

            var now = Time.realtimeSinceStartupAsDouble;
            while (_outbound.Count > 0 && _outbound.Peek().ReleaseTime <= now)
            {
                var p = _outbound.Dequeue();
                _peer?.Send(p.Data, p.Method);
            }

            while (_inbound.Count > 0 && _inbound.Peek().ReleaseTime <= now)
            {
                Handle(_inbound.Dequeue().Data);
            }

            Interpolator?.Advance(Time.unscaledDeltaTime);
            ReleaseDueEvents();
        }

        /// <summary>이벤트는 그 틱의 위치가 화면에 그려질 때(렌더 시각) 재생한다.</summary>
        private void ReleaseDueEvents()
        {
            if (Interpolator == null || _pendingEvents.Count == 0)
            {
                return;
            }

            var renderTime = Interpolator.RenderTime;
            for (var i = 0; i < _pendingEvents.Count; i++)
            {
                if (_pendingEvents[i].Tick * (double)SimConstants.TickDelta <= renderTime)
                {
                    DueCombatEvents.Add(_pendingEvents[i]);
                    _pendingEvents.RemoveAt(i);
                    i--;
                }
            }

            // 너무 쌓이면(창이 멈췄다 돌아온 경우 등) 오래된 것부터 버린다.
            if (_pendingEvents.Count > 2000)
            {
                _pendingEvents.RemoveRange(0, _pendingEvents.Count - 2000);
            }
        }

        private void OnDestroy()
        {
            _net?.Stop();
        }

        public bool IsMine(int unitId) => PlayerId != 0 && Latest.TryGetValue(unitId, out var u) && u.Owner == PlayerId;

        /// <summary>나와 적인 유닛인지 (중립·같은 팀 제외).</summary>
        public bool IsEnemy(int unitId) =>
            MyTeam != 0 && Latest.TryGetValue(unitId, out var u) && OpenRTS.Shared.Combat.UnitArchetypes.AreEnemies(MyTeam, u.Team);

        /// <summary>선택한 내 유닛들을 목표로 보낸다. 서버가 소유권을 다시 확인하고 대형을 짠다.</summary>
        public void SendMove(IReadOnlyList<int> unitIds, Vector2 target) => SendCommand(CommandType.Move, unitIds, target, 0);

        /// <summary>
        /// 명령을 보낸다. 지금 추정한 서버 틱을 함께 실어 서버가 명령 지연을 잴 수 있게 한다.
        /// 255개가 넘으면 나눠 보낸다.
        /// </summary>
        public void SendCommand(CommandType type, IReadOnlyList<int> unitIds, Vector2 target, int targetUnit) =>
            SendCommand(type, unitIds, target, targetUnit, OpenRTS.Shared.Combat.UnitKind.Melee);

        /// <summary>Build·Train 처럼 종류가 필요한 명령.</summary>
        public void SendCommand(CommandType type, IReadOnlyList<int> unitIds, Vector2 target, int targetUnit, OpenRTS.Shared.Combat.UnitKind kind)
        {
            if (State != ClientState.Joined || unitIds == null || unitIds.Count == 0)
            {
                return;
            }

            var clientTick = Interpolator != null ? (uint)Math.Max(1.0, Interpolator.ServerClock / SimConstants.TickDelta) : 0u;
            for (var start = 0; start < unitIds.Count; start += UnitCommand.MaxUnits)
            {
                var count = Math.Min(UnitCommand.MaxUnits, unitIds.Count - start);
                var chunk = new int[count];
                for (var i = 0; i < count; i++)
                {
                    chunk[i] = unitIds[start + i];
                }

                _writer.Reset();
                new UnitCommand { Type = type, Target = target, TargetUnit = targetUnit, ClientTick = clientTick, Kind = kind, UnitIds = chunk }.Write(_writer);
                Send(_writer.ToArray(), DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>서버에 설정 변경을 요청한다. 적용된 값은 SimSettings 메시지로 돌아온다.</summary>
        public void SendSettings(SimSettings settings)
        {
            if (State != ClientState.Joined)
            {
                return;
            }

            _writer.Reset();
            settings.Write(_writer, MessageType.SetSimSettings);
            Send(_writer.ToArray(), DeliveryMethod.ReliableOrdered);
        }

        public void SendResetScenario()
        {
            if (State != ClientState.Joined)
            {
                return;
            }

            _writer.Reset();
            _writer.WriteByte((byte)MessageType.ResetScenario);
            Send(_writer.ToArray(), DeliveryMethod.ReliableOrdered);
        }

        private void OnConnected(NetPeer peer)
        {
            _peer = peer;
            _writer.Reset();
            new ClientHello { ProtocolVersion = SimConstants.ProtocolVersion }.Write(_writer);
            Send(_writer.ToArray(), DeliveryMethod.ReliableOrdered);
        }

        private void Send(byte[] data, DeliveryMethod method)
        {
            _outbound.Enqueue(new DelayedPacket
            {
                ReleaseTime = Time.realtimeSinceStartupAsDouble + SimulatedRttMs / 2000.0,
                Data = data,
                Method = method,
            });
        }

        private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (method == DeliveryMethod.Unreliable && SimulatedLossPercent > 0 && _random.Next(100) < SimulatedLossPercent)
            {
                SnapshotsDroppedBySimulation++;
                return;
            }

            var data = new byte[reader.AvailableBytes];
            Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, data.Length);
            _inbound.Enqueue(new DelayedPacket
            {
                ReleaseTime = Time.realtimeSinceStartupAsDouble + SimulatedRttMs / 2000.0,
                Data = data,
            });
        }

        private void Handle(byte[] data)
        {
            try
            {
                var r = new PacketReader(data);
                switch ((MessageType)r.ReadByte())
                {
                    case MessageType.ServerWelcome:
                        var welcome = ServerWelcome.Read(ref r);
                        PlayerId = welcome.PlayerId;
                        Interpolator.MinInterpolationDelay = 2f / welcome.SnapshotRate;
                        State = ClientState.Joined;
                        Debug.Log($"[OpenRTS] joined as player {PlayerId} (server tick {welcome.ServerTick}, {welcome.TickRate}Hz / snapshot {welcome.SnapshotRate}Hz)");
                        Joined?.Invoke();
                        break;

                    case MessageType.Snapshot:
                        if (!_receiver.Receive(ref r, out var tick, out var units))
                        {
                            break;
                        }

                        SnapshotsReceived++;
                        SendAck(tick);
                        if (tick > Interpolator.LatestTick || Interpolator.BufferedCount == 0)
                        {
                            LatestRaw.Clear();
                            Latest.Clear();
                            _myUnits.Clear();
                            foreach (var u in units)
                            {
                                LatestRaw[u.Id] = u.Position;
                                Latest[u.Id] = u;
                                if (PlayerId != 0 && u.Owner == PlayerId)
                                {
                                    _myUnits.Add(u.Id);
                                    MyTeam = u.Team;
                                }
                            }
                        }

                        Interpolator.AddSnapshot(tick, units);
                        break;

                    case MessageType.SimSettings:
                        var settings = SimSettings.Read(ref r);
                        Settings = settings;
                        Debug.Log($"[OpenRTS] settings: {settings}");
                        SettingsChanged?.Invoke(settings);
                        break;

                    case MessageType.SimStats:
                        Stats = SimStats.Read(ref r);
                        break;

                    case MessageType.PlayerState:
                        var ps = PlayerStateMessage.Read(ref r);
                        Ore = ps.Ore;
                        Queues.Clear();
                        foreach (var q in ps.Queues)
                        {
                            Queues[q.BuildingId] = q;
                        }

                        HasPlayerState = true;
                        break;

                    case MessageType.CombatEvents:
                        CombatEventsMessage.Read(ref r, _eventBuffer);
                        CombatEventsReceived += _eventBuffer.Count;
                        _pendingEvents.AddRange(_eventBuffer);
                        break;

                }
            }
            catch (PacketFormatException e)
            {
                Debug.LogWarning($"[OpenRTS] malformed packet: {e.Message}");
            }
        }

        /// <summary>완성한 스냅샷 틱을 알린다. 서버는 이 틱을 기준으로 다음 델타를 만든다.</summary>
        private void SendAck(uint tick)
        {
            _writer.Reset();
            new SnapshotAck { Tick = tick }.Write(_writer);
            Send(_writer.ToArray(), DeliveryMethod.Unreliable);
        }

        private void ReadCommandLine()

        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "-host": Host = args[i + 1]; break;
                    case "-port": Port = int.Parse(args[i + 1]); break;
                    case "-latency": SimulatedRttMs = int.Parse(args[i + 1]); break;
                    case "-loss": SimulatedLossPercent = int.Parse(args[i + 1]); break;
                }
            }
        }
    }
}
