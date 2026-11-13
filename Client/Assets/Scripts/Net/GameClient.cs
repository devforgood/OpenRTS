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
        private readonly List<UnitState> _snapshotUnits = new List<UnitState>();
        private readonly Queue<DelayedPacket> _inbound = new Queue<DelayedPacket>();
        private readonly Queue<DelayedPacket> _outbound = new Queue<DelayedPacket>();
        private readonly System.Random _random = new System.Random();
        private NetManager _net;
        private NetPeer _peer;

        public ClientState State { get; private set; } = ClientState.Connecting;
        public int MyUnitId { get; private set; } = -1;
        public SnapshotInterpolator Interpolator { get; private set; }

        /// <summary>보간하지 않은 최신 스냅샷 (디버그 비교용).</summary>
        public Dictionary<int, Vector2> LatestRaw { get; } = new Dictionary<int, Vector2>();

        /// <summary>LiteNetLib 가 잰 실제 왕복 지연 (시뮬레이션 지연 제외).</summary>
        public int NetworkRttMs => _peer != null ? _peer.RoundTripTime : 0;

        public int SnapshotsReceived { get; private set; }
        public int SnapshotsDroppedBySimulation { get; private set; }

        public event Action Joined;

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
        }

        private void OnDestroy()
        {
            _net?.Stop();
        }

        public void SendMove(Vector2 target)
        {
            if (State != ClientState.Joined)
            {
                return;
            }

            _writer.Reset();
            new MoveCommand { Target = target }.Write(_writer);
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
                        MyUnitId = welcome.UnitId;
                        Interpolator.MinInterpolationDelay = 2f / welcome.SnapshotRate;
                        State = ClientState.Joined;
                        Debug.Log($"[OpenRTS] joined as unit {MyUnitId} (server tick {welcome.ServerTick}, {welcome.TickRate}Hz / snapshot {welcome.SnapshotRate}Hz)");
                        Joined?.Invoke();
                        break;

                    case MessageType.Snapshot:
                        var tick = SnapshotMessage.Read(ref r, _snapshotUnits);
                        SnapshotsReceived++;
                        if (tick > Interpolator.LatestTick || Interpolator.BufferedCount == 0)
                        {
                            LatestRaw.Clear();
                            foreach (var u in _snapshotUnits)
                            {
                                LatestRaw[u.Id] = u.Position;
                            }
                        }

                        Interpolator.AddSnapshot(tick, _snapshotUnits);
                        break;
                }
            }
            catch (PacketFormatException e)
            {
                Debug.LogWarning($"[OpenRTS] malformed packet: {e.Message}");
            }
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
