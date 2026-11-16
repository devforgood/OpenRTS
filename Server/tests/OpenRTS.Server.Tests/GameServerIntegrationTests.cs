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
    private uint _tick = 1;

    public GameServerIntegrationTests()
    {
        var settings = SimSettings.Default;
        settings.BotCount = 0;
        _server = new GameServer(new World(settings: settings));
        _server.Start(0);
    }

    public void Dispose() => _server.Dispose();

    private static int PlayerUnits => SimSettings.Default.PlayerUnits;

    [Fact]
    public void Client_Joins_GetsSquad_AndMovesOneUnit()
    {
        using var client = new TestClient(_server.Port);

        RunUntil(() => client.Settings is not null && client.MyUnits().Count == PlayerUnits, client);
        var unitId = client.MyUnits()[0];
        var start = _server.World.Find(unitId)!.Position;
        var target = start + new Vector2(0, -3); // 무리(격자) 밖으로

        client.SendMove(target, unitId);
        // 스냅샷은 16비트 양자화라 0.01 이내면 같은 위치.
        RunUntil(() => client.LastSnapshot.TryGetValue(unitId, out var u) && Vector2.Distance(u.Position, target) < 0.01f, client);

        Assert.True(Vector2.Distance(target, _server.World.Find(unitId)!.Position) <= World.ArrivalDistance);
        Assert.Equal(0u, client.LastSnapshotTick % SimConstants.TicksPerSnapshot);
    }

    [Fact]
    public void GroupMove_SpreadsIntoFormation_AndEveryoneArrives()
    {
        using var client = new TestClient(_server.Port);
        RunUntil(() => client.MyUnits().Count == PlayerUnits, client);
        var ids = client.MyUnits();
        var target = new Vector2(-20, 15);

        client.SendMove(target, ids.ToArray());
        RunUntil(() => ids.Any(id => _server.World.Find(id)!.IsMoving), client);
        RunUntil(() => ids.All(id => !_server.World.Find(id)!.IsMoving), client);


        var positions = ids.Select(id => _server.World.Find(id)!.Position).ToList();
        var r = _server.World.Settings.UnitRadius;
        Assert.All(positions, p => Assert.True(Vector2.Distance(p, target) < 4f, $"{p} far from {target}"));
        for (var i = 0; i < positions.Count; i++)
        {
            for (var j = i + 1; j < positions.Count; j++)
            {
                Assert.True(Vector2.Distance(positions[i], positions[j]) > 2 * r * 0.95f, "units overlap at destination");
            }
        }

        Assert.Equal(0, _server.World.BlockedStops); // 대형 칸이 있으니 서로 밀치다 포기하지 않는다
    }

    [Fact]
    public void Client_CannotCommandOtherPlayersUnits()
    {
        using var a = new TestClient(_server.Port);
        using var b = new TestClient(_server.Port);
        RunUntil(() => a.MyUnits().Count == PlayerUnits && b.MyUnits().Count == PlayerUnits && a.LastSnapshot.Count == 2 * PlayerUnits, a, b);

        var victim = b.MyUnits()[0];
        a.SendMove(new Vector2(40, 40), victim);
        for (var i = 0; i < 30; i++)
        {
            Step(a, b);
        }

        // 두 플레이어는 다른 팀이라 서로 싸우며 움직일 수는 있지만, a 가 찍은 곳으로 가지는 않는다.
        var unit = _server.World.Find(victim);
        Assert.True(unit is null || unit.Target is not { } t || Vector2.Distance(t, new Vector2(40, 40)) > 5f);

    }

    [Fact]
    public void TwoClients_SeeEachOther_AndDisconnectRemovesUnits()
    {
        using var a = new TestClient(_server.Port);
        var b = new TestClient(_server.Port);

        RunUntil(() => a.Welcome is not null && b.Welcome is not null, a, b);
        var bPlayer = b.Welcome!.Value.PlayerId;
        RunUntil(() => a.LastSnapshot.Values.Count(u => u.Owner == bPlayer) == PlayerUnits, a, b);

        b.Dispose();
        RunUntil(() => _server.PlayerCount == 1 && a.LastSnapshot.Values.All(u => u.Owner != bPlayer), a);
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
        requested.BotCount = 450; // 전체 스냅샷이면 4조각
        requested.Scenario = BotScenario.CircleSwap;
        requested.UnitRadius = 0.4f;
        a.SendSettings(requested);

        // 두 클라이언트 모두 새 설정을 받고, 봇 450 + 플레이어 유닛이 담긴 완성 스냅샷을 받는다.
        var expected = 450 + 2 * PlayerUnits;
        RunUntil(() => b.Settings is { BotCount: 450 } && b.LastSnapshot.Count == expected && a.LastSnapshot.Count == expected, a, b);
        Assert.Equal(BotScenario.CircleSwap, b.Settings!.Value.Scenario);
        Assert.Equal(0.4f, b.Settings!.Value.UnitRadius);

        RunUntil(() => b.Stats is { } s && s.UnitCount == expected, a, b);
    }

    [Fact]
    public void DeltaSnapshots_MatchServerState_AndCostFarLessWhenIdle()
    {
        using var client = new TestClient(_server.Port);
        var settings = SimSettings.Default;
        settings.BotCount = 200;
        settings.Scenario = BotScenario.CircleSwap;
        RunUntil(() => client.Settings is not null, client);
        client.SendSettings(settings);
        RunUntil(() => client.Settings is { BotCount: 200 } && client.LastSnapshot.Count == 200 + PlayerUnits, client);

        // 움직이는 중: 클라이언트가 복원한 상태가 서버 상태(양자화 값)와 정확히 같아야 한다.
        for (var i = 0; i < 60; i++)
        {
            Step(client);
        }

        RunUntil(() => client.Receiver.DeltaReceived > 10, client);
        AssertClientMatchesServer(client);
        Assert.Equal(0, client.Receiver.MissingBaseline);

        // 전부 멈추게 한 뒤(회피 끔 + 봇 0 + 내 유닛 정지) 대역폭 비교
        settings.BotCount = 0;
        client.SendSettings(settings);
        RunUntil(() => client.Settings is { BotCount: 0 } && client.LastSnapshot.Count == PlayerUnits, client);
        for (var i = 0; i < 20; i++)
        {
            Step(client); // 봇 제거분이 담긴 델타가 지나갈 때까지
        }

        var bytesBefore = _server.TotalSnapshotBytes;

        for (var i = 0; i < 60; i++)
        {
            Step(client);
        }

        var idleBytesPerSnapshot = (_server.TotalSnapshotBytes - bytesBefore) / 30.0;
        var fullBytes = SnapshotMessage.HeaderSize + PlayerUnits * SnapshotMessage.FullEntrySize;
        Assert.True(idleBytesPerSnapshot < fullBytes / 2.0, $"idle snapshot {idleBytesPerSnapshot:0} bytes vs full {fullBytes}");
        AssertClientMatchesServer(client);
    }

    [Fact]
    public void Battle_OverNetwork_ClientSeesDamageEventsAndWinner()
    {
        using var client = new TestClient(_server.Port);
        RunUntil(() => client.Settings is not null && client.MyUnits().Count == PlayerUnits, client);
        var settings = client.Settings!.Value;
        settings.Scenario = BotScenario.Battle;
        settings.BotCount = 40;
        client.SendSettings(settings);

        RunUntil(() => client.Settings is { Scenario: BotScenario.Battle } && client.LastSnapshot.Count == 40 + PlayerUnits, client);
        Assert.All(client.LastSnapshot.Values, u => Assert.InRange(u.Team, (byte)1, (byte)2));
        Assert.Equal(_server.World.TeamOf(client.Welcome!.Value.PlayerId), client.LastSnapshot[client.MyUnits()[0]].Team);

        RunUntil(() => client.Stats is { Winner: not 0 }, client);
        Assert.Contains(client.Events, e => e.Type == CombatEventType.ProjectileLaunch);
        Assert.Contains(client.Events, e => e.Type == CombatEventType.MeleeHit);
        Assert.True(client.Events.Count(e => e.Type == CombatEventType.Death) >= 20);
        Assert.True(client.SawDamage);
        AssertClientMatchesServer(client); // 체력·공격 대상까지 서버가 보낸 값과 같다
    }

    [Fact]
    public void FogOfWar_EnemyOutsideVision_IsNeverInAnyPacket_AndAppearsWhenScouted()
    {
        using var a = new TestClient(_server.Port);
        using var b = new TestClient(_server.Port);
        RunUntil(() => a.MyUnits().Count == PlayerUnits && b.MyUnits().Count == PlayerUnits, a, b);
        var world = _server.World;
        Assert.NotEqual(world.TeamOf(a.Welcome!.Value.PlayerId), world.TeamOf(b.Welcome!.Value.PlayerId));

        // 두 무리를 맵 양 끝으로 옮긴다 (시야 10~11 보다 훨씬 멀리).
        var aUnits = a.MyUnits();
        var bUnits = b.MyUnits();
        for (var i = 0; i < PlayerUnits; i++)
        {
            world.Find(aUnits[i])!.Position = new Vector2(-40 + i % 3 * 1.5f, i / 3 * 1.5f);
            world.Find(bUnits[i])!.Position = new Vector2(40 - i % 3 * 1.5f, i / 3 * 1.5f);
        }

        RunUntil(() => !a.LastSnapshot.Keys.Intersect(bUnits).Any() && !b.LastSnapshot.Keys.Intersect(aUnits).Any(), a, b);

        // 이제부터 a 가 받는 모든 스냅샷 조각에 b 유닛 정보(제거 표시 말고)가 하나도 없어야 한다.
        a.RawEntries.Clear();
        for (var i = 0; i < 60; i++)
        {
            Step(a, b);
        }

        Assert.NotEmpty(a.RawEntries); // 패킷은 계속 받았다 (빈 델타 포함)
        Assert.DoesNotContain(a.RawEntries, e => bUnits.Contains(e.Id) && !e.IsRemoved);
        Assert.DoesNotContain(a.LastSnapshot.Values, u => u.Team == world.TeamOf(b.Welcome!.Value.PlayerId));

        // a 의 유닛 하나를 b 무리 옆으로 보내면(정찰) 보이기 시작하고, 돌아오면 다시 사라진다.
        var scout = world.Find(aUnits[0])!;
        scout.Position = new Vector2(32, 0);
        RunUntil(() => bUnits.All(a.LastSnapshot.ContainsKey), a, b);
        scout.Position = new Vector2(-40, 0);
        RunUntil(() => !a.LastSnapshot.Keys.Intersect(bUnits).Any(), a, b);
        AssertClientMatchesServer(a);
    }

    [Fact]
    public void Skirmish_OverNetwork_TrainWorker_AndEconomyStateIsPrivate()
    {
        using var a = new TestClient(_server.Port);
        using var b = new TestClient(_server.Port);
        RunUntil(() => a.Settings is not null && b.Settings is not null && a.Welcome is not null && b.Welcome is not null, a, b);
        var settings = a.Settings!.Value;
        settings.Scenario = BotScenario.Skirmish;
        a.SendSettings(settings);

        RunUntil(() => a.PlayerState is { } s && s.Ore == OpenRTS.Shared.Combat.EconomyRules.StartingOre && a.MyUnits().Count == 1 + OpenRTS.Shared.Combat.EconomyRules.StartingWorkers, a, b);
        var hq = a.LastSnapshot.Values.Single(u => u.Owner == a.Welcome!.Value.PlayerId && u.Kind == OpenRTS.Shared.Combat.UnitKind.Headquarters);

        a.SendCommand(new UnitCommand { Type = CommandType.Train, Kind = OpenRTS.Shared.Combat.UnitKind.Worker, UnitIds = new[] { hq.Id } });
        RunUntil(() => a.PlayerState is { } s && s.Ore == OpenRTS.Shared.Combat.EconomyRules.StartingOre - OpenRTS.Shared.Combat.UnitArchetypes.Worker.Cost && s.Queues.Count == 1, a, b);
        Assert.Equal(hq.Id, a.PlayerState!.Value.Queues[0].BuildingId);
        Assert.True(b.PlayerState is { } bs && bs.Ore == OpenRTS.Shared.Combat.EconomyRules.StartingOre && bs.Queues.Count == 0); // 남의 자원·대기열은 오지 않는다

        RunUntil(() => a.MyUnits().Count == 2 + OpenRTS.Shared.Combat.EconomyRules.StartingWorkers, a, b);
        Assert.True((a.LastSnapshot[hq.Id].Flags & UnitFlags.Producing) == 0);
    }

    private void AssertClientMatchesServer(TestClient client)
    {
        // 클라이언트가 마지막으로 복원한 틱에 서버가 실제로 보낸 상태와 비교한다.
        var team = _server.World.TeamOf(client.Welcome!.Value.PlayerId);
        Assert.True(_server.TryGetSentSnapshot(team, client.LastSnapshotTick, out var sent));
        var server = sent.ToDictionary(e => (int)e.Id);
        Assert.Equal(server.Count, client.LastSnapshot.Count);
        foreach (var (id, state) in client.LastSnapshot)
        {
            Assert.Equal(server[id], SnapshotEntry.From(state));
        }

    }

    private void Step(params TestClient[] clients)
    {
        _server.Tick(_tick++);
        foreach (var c in clients)
        {
            c.Poll();
        }

        Thread.Sleep(2);
    }

    private void RunUntil(Func<bool> condition, params TestClient[] clients)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Step(clients);
        }
    }

    private sealed class TestClient : IDisposable
    {
        private readonly NetManager _net;
        private readonly PacketWriter _writer = new();
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
                    case MessageType.PlayerState:
                        PlayerState = PlayerStateMessage.Read(ref r);
                        break;
                    case MessageType.CombatEvents:
                        var batch = new List<CombatEvent>();
                        CombatEventsMessage.Read(ref r, batch);
                        Events.AddRange(batch);
                        break;
                    case MessageType.Snapshot:
                        var raw = new PacketReader(reader.RawData, reader.Position + 1, reader.AvailableBytes - 1);
                        SnapshotMessage.ReadPart(ref raw, _rawPart, out _, out _, out _);
                        RawEntries.AddRange(_rawPart);
                        if (Receiver.Receive(ref r, out var tick, out var units))
                        {
                            LastSnapshotTick = tick;
                            LastSnapshot = units.ToDictionary(u => u.Id);
                            SawDamage |= units.Any(u => u.Team != 0 && u.Health < 0.999f);
                            _writer.Reset();
                            new SnapshotAck { Tick = tick }.Write(_writer);
                            _peer!.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.Unreliable);
                        }

                        break;
                }
            };

            _net = new NetManager(listener) { AutoRecycle = true };
            _net.Start();
            _net.Connect("127.0.0.1", port, SimConstants.ConnectionKey);
        }

        private readonly List<SnapshotEntry> _rawPart = new();

        /// <summary>받은 모든 스냅샷 조각의 항목 그대로 (복원 전).</summary>
        public List<SnapshotEntry> RawEntries { get; } = new();

        public SnapshotReceiver Receiver { get; } = new();

        public List<CombatEvent> Events { get; } = new();
        public PlayerStateMessage? PlayerState { get; private set; }

        public void SendCommand(UnitCommand command)
        {
            _writer.Reset();
            command.Write(_writer);
            _peer!.Send(_writer.Buffer, 0, _writer.Length, DeliveryMethod.ReliableOrdered);
        }
        public bool SawDamage { get; private set; }

        public ServerWelcome? Welcome { get; private set; }
        public SimSettings? Settings { get; private set; }
        public SimStats? Stats { get; private set; }
        public Dictionary<int, UnitState> LastSnapshot { get; private set; } = new();
        public uint LastSnapshotTick { get; private set; }
        public bool Disconnected { get; private set; }

        public List<int> MyUnits() => Welcome is { } w
            ? LastSnapshot.Values.Where(u => u.Owner == w.PlayerId).Select(u => u.Id).Order().ToList()
            : new List<int>();

        public void Poll() => _net.PollEvents();

        public void SendMove(Vector2 target, params int[] unitIds)
        {
            _writer.Reset();
            new UnitCommand { Type = CommandType.Move, Target = target, UnitIds = unitIds }.Write(_writer);
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
