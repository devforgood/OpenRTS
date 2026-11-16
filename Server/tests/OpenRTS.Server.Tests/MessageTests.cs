using System.Numerics;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Interpolation;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Server.Tests;

public class MessageTests
{
    private static SnapshotEntry E(int id, float x, float y, byte owner = 0, float hp = 1f, int target = 0, byte team = 1) =>
        SnapshotEntry.From(new UnitState(id, new Vector2(x, y), owner) { Health = hp, AttackTarget = target, Team = team });

    private static List<SnapshotEntry> ReadSingle(PacketWriter w, out uint tick, out uint baseTick)
    {
        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.Snapshot, (MessageType)r.ReadByte());
        var decoded = new List<SnapshotEntry>();
        tick = SnapshotMessage.ReadPart(ref r, decoded, out baseTick, out _, out _);
        Assert.Equal(0, r.Remaining);
        return decoded;
    }

    [Fact]
    public void Snapshot_RoundTrips_AllFields()
    {
        var w = new PacketWriter(4); // 작은 버퍼로 시작해 확장도 검증
        var units = new[]
        {
            new UnitState(1, new Vector2(1.5f, -2f), 3) { Team = 2, Kind = UnitKind.Ranged, Health = 0.5f, AttackTarget = 42, Flags = UnitFlags.WindingUp },
            new UnitState(42, new Vector2(-49.9f, 50f)) { Health = 0.001f },
        };
        SnapshotMessage.WriteSingle(w, 1234, SnapshotMessage.NoBaseline, units.Select(u => SnapshotEntry.From(u)).ToList());
        Assert.Equal(SnapshotMessage.HeaderSize + 2 * SnapshotMessage.FullEntrySize, w.Length);

        var decoded = ReadSingle(w, out var tick, out var baseTick);
        Assert.Equal((1234u, 0u), (tick, baseTick));
        var a = decoded[0].ToState();
        Assert.True(Vector2.Distance(units[0].Position, a.Position) < 0.002f);
        Assert.Equal((3, 2, UnitKind.Ranged, 42, UnitFlags.WindingUp), (a.Owner, a.Team, a.Kind, a.AttackTarget, a.Flags));
        Assert.InRange(a.Health, 0.5f, 0.505f);
        Assert.True(decoded[1].Health >= 1, "alive units never quantize to 0 health");
    }

    [Fact]
    public void Snapshot_PlanSplitsIntoParts_ThatFitMtu_AndAssembles()
    {
        var entries = Enumerable.Range(1, 500).Select(i => E(i, i % 50, -i % 50)).ToList();
        var parts = new List<(int Start, int Count)>();
        SnapshotMessage.Plan(entries, SnapshotMessage.DefaultMaxPacketSize, parts);
        Assert.Equal(500, parts.Sum(p => p.Count));
        Assert.Equal(7, parts.Count); // (1023-13)/14 = 72개씩 → 500개는 7조각

        var assembler = new SnapshotAssembler();
        var w = new PacketWriter();
        var partEntries = new List<SnapshotEntry>();
        List<SnapshotEntry>? all = null;
        for (var p = parts.Count - 1; p >= 0; p--) // 순서가 뒤바뀌어 도착해도 된다
        {
            w.Reset();
            SnapshotMessage.WritePart(w, 7, SnapshotMessage.NoBaseline, entries, parts[p].Start, parts[p].Count, p, parts.Count);
            Assert.True(w.Length <= SnapshotMessage.DefaultMaxPacketSize, $"part {p} is {w.Length} bytes");

            var r = new PacketReader(w.ToArray());
            r.ReadByte();
            var tick = SnapshotMessage.ReadPart(ref r, partEntries, out var baseTick, out var part, out var count);
            var done = assembler.Add(tick, baseTick, part, count, partEntries, out _, out var result);
            Assert.Equal(p == 0, done);
            if (done)
            {
                all = result;
            }
        }

        Assert.Equal(entries.Select(u => u.Id).Order(), all!.Select(u => u.Id).Order());
    }

    [Fact]
    public void Plan_EmptyDelta_StillSendsOnePart()
    {
        var parts = new List<(int Start, int Count)>();
        SnapshotMessage.Plan(new List<SnapshotEntry>(), 1023, parts);
        Assert.Equal(new[] { (0, 0) }, parts);
    }

    [Fact]
    public void Assembler_DropsIncompleteTick_WhenNewerArrives()
    {
        var assembler = new SnapshotAssembler();
        var one = new List<SnapshotEntry> { E(1, 0, 0) };

        Assert.False(assembler.Add(10, 0, 0, 2, one, out _, out _));   // 10의 조각 1/2
        Assert.True(assembler.Add(11, 0, 0, 1, one, out var t, out _)); // 11 완성 → 10 버림
        Assert.Equal(11u, t);
        Assert.False(assembler.Add(10, 0, 1, 2, one, out _, out _));   // 늦게 온 10의 나머지
        Assert.Equal(1, assembler.IncompleteDropped);
    }

    [Fact]
    public void Delta_SendsOnlyChangedFieldGroups_AndRebuildsFullState()
    {
        var baseline = new List<SnapshotEntry> { E(1, 0, 0), E(2, 5, 5), E(3, -5, 1), E(5, 9, 9), E(6, 1, 1) };
        var current = new List<SnapshotEntry> { E(1, 0, 0), E(2, 5.5f, 5), E(4, 1, 1, owner: 2), E(5, 9, 9, hp: 0.5f), E(6, 1, 1, target: 5) };

        var delta = new List<SnapshotEntry>();
        SnapshotDelta.Diff(baseline, current, delta);

        // 1 그대로(빠짐), 2 위치만, 3 제거, 4 새로(전부), 5 체력만, 6 행동만
        Assert.Equal(new ushort[] { 2, 3, 4, 5, 6 }, delta.Select(e => e.Id));
        Assert.Equal(
            new[] { SnapshotFields.Position, SnapshotFields.Removed, SnapshotFields.All, SnapshotFields.Health, SnapshotFields.Action },
            delta.Select(e => e.Fields));

        // 전송 크기: 위치 7, 제거 3, 전체 14, 체력 4, 행동 6
        Assert.Equal(new[] { 7, 3, 14, 4, 6 }, delta.Select(e => SnapshotMessage.EntrySize(e)));

        // 실제로 직렬화한 뒤 복원
        var w = new PacketWriter();
        SnapshotMessage.WriteSingle(w, 20, 10, delta);
        var wire = ReadSingle(w, out _, out _);
        wire.Reverse(); // 조각 도착 순서가 바뀌어도 된다
        var rebuilt = new List<SnapshotEntry>();
        SnapshotDelta.Apply(baseline, wire, rebuilt);
        Assert.Equal(current.Count, rebuilt.Count);
        for (var i = 0; i < current.Count; i++)
        {
            Assert.True(current[i].SameValues(rebuilt[i]), $"{current[i]} != {rebuilt[i]}");
        }
    }

    [Fact]
    public void Receiver_AppliesDeltaOnTopOfAckedBaseline_AndCountsMissingBaseline()
    {
        var receiver = new SnapshotReceiver();
        var w = new PacketWriter();

        bool Feed(uint tick, uint baseTick, List<SnapshotEntry> entries, out IReadOnlyList<UnitState> states)
        {
            w.Reset();
            SnapshotMessage.WriteSingle(w, tick, baseTick, entries);
            var r = new PacketReader(w.ToArray());
            r.ReadByte();
            return receiver.Receive(ref r, out _, out states);
        }

        var full = new List<SnapshotEntry> { E(1, 0, 0), E(2, 3, 3) };
        Assert.True(Feed(10, 0, full, out var s10));
        Assert.Equal(2, s10.Count);

        var delta = new List<SnapshotEntry>();
        SnapshotDelta.Diff(full, new List<SnapshotEntry> { E(1, 1, 0), E(2, 3, 3, hp: 0.25f) }, delta);
        Assert.Equal(2, delta.Count);
        Assert.True(Feed(12, 10, delta, out var s12));
        Assert.Equal(new[] { 1, 2 }, s12.Select(u => u.Id));
        Assert.True(Math.Abs(s12[0].Position.X - 1) < 0.002f);
        Assert.InRange(s12[1].Health, 0.25f, 0.26f);
        Assert.True(Math.Abs(s12[1].Position.X - 3) < 0.002f); // 위치는 baseline 값 유지

        Assert.False(Feed(14, 8, delta, out _)); // 8 은 받은 적 없음
        Assert.Equal(1, receiver.MissingBaseline);
    }

    [Fact]
    public void SimSettings_RoundTrip_AndClamp()
    {
        var s = SimSettings.Default;
        s.BotCount = 123;
        s.Scenario = BotScenario.Battle;
        s.Avoidance = false;
        s.Map = OpenRTS.Shared.Map.MapKind.Gate;
        s.PlayerUnits = 20;
        s.RepathWhenBlocked = false;
        s.DeltaSnapshots = false;
        var w = new PacketWriter();
        s.Write(w, MessageType.SetSimSettings);
        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.SetSimSettings, (MessageType)r.ReadByte());
        Assert.Equal(s, SimSettings.Read(ref r));

        var bad = new SimSettings { BotCount = 60000, UnitRadius = float.NaN, UnitSpeed = 999, Scenario = (BotScenario)77, MaxNeighbors = 0, TimeHorizon = -1, NeighborDist = 0, Map = (OpenRTS.Shared.Map.MapKind)50, PlayerUnits = 0 };
        var c = bad.Clamped();
        Assert.Equal(SimSettings.MaxBots, c.BotCount);
        Assert.Equal(BotScenario.Wander, c.Scenario);
        Assert.Equal(0.1f, c.UnitRadius);
        Assert.Equal(20f, c.UnitSpeed);
        Assert.Equal(1, c.MaxNeighbors);
        Assert.Equal(OpenRTS.Shared.Map.MapKind.Open, c.Map);
        Assert.Equal(1, c.PlayerUnits);
    }

    [Fact]
    public void Welcome_Command_And_Events_RoundTrip()
    {
        var w = new PacketWriter();
        new ServerWelcome { PlayerId = 7, ServerTick = 99, TickRate = 30, SnapshotRate = 15 }.Write(w);
        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.ServerWelcome, (MessageType)r.ReadByte());
        var welcome = ServerWelcome.Read(ref r);
        Assert.Equal(((byte)7, 99u, 30, 15), (welcome.PlayerId, welcome.ServerTick, welcome.TickRate, welcome.SnapshotRate));

        w.Reset();
        new UnitCommand { Type = CommandType.Attack, Target = new Vector2(3, 4), TargetUnit = 77, ClientTick = 1234, UnitIds = new[] { 5, 9, 65535 } }.Write(w);
        r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.UnitCommand, (MessageType)r.ReadByte());
        var cmd = UnitCommand.Read(ref r);
        Assert.Equal((CommandType.Attack, new Vector2(3, 4), 77, 1234u), (cmd.Type, cmd.Target, cmd.TargetUnit, cmd.ClientTick));
        Assert.Equal(new[] { 5, 9, 65535 }, cmd.UnitIds);

        var events = new List<CombatEvent>
        {
            new() { Tick = 10, Type = CombatEventType.ProjectileLaunch, Attacker = 3, Target = 4 },
            new() { Tick = 11, Type = CombatEventType.Death, Attacker = 3, Target = 4 },
        };
        w.Reset();
        CombatEventsMessage.Write(w, events, 0, events.Count);
        Assert.True(CombatEventsMessage.MaxPerPacket * 9 + 3 <= 1023);
        r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.CombatEvents, (MessageType)r.ReadByte());
        var decoded = new List<CombatEvent>();
        CombatEventsMessage.Read(ref r, decoded);
        Assert.Equal(events, decoded);
    }

    [Fact]
    public void AbilityMessages_RoundTrip()
    {
        var w = new PacketWriter();
        new UnitCommand { Type = CommandType.Cast, Ability = OpenRTS.Shared.Abilities.AbilityId.Leap, Target = new Vector2(1, 2), TargetUnit = 9, UnitIds = new[] { 3 } }.Write(w);
        var r = new PacketReader(w.ToArray());
        r.ReadByte();
        var cmd = UnitCommand.Read(ref r);
        Assert.Equal((CommandType.Cast, OpenRTS.Shared.Abilities.AbilityId.Leap, 9), (cmd.Type, cmd.Ability, cmd.TargetUnit));

        var events = new List<AbilityEvent>
        {
            new() { Tick = 5, Type = AbilityEventType.AreaTelegraph, Caster = 7, Ability = OpenRTS.Shared.Abilities.AbilityId.Volley, Target = 0, Point = new Vector2(3.5f, -2.25f) },
            new() { Tick = 6, Type = AbilityEventType.Hit, Caster = 7, Ability = OpenRTS.Shared.Abilities.AbilityId.Volley, Target = 8, Point = new Vector2(-49, 49) },
        };
        w.Reset();
        AbilityEventsMessage.Write(w, events, 0, events.Count);
        Assert.True(3 + AbilityEventsMessage.MaxPerPacket * 14 <= 1023);
        r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.AbilityEvents, (MessageType)r.ReadByte());
        var decoded = new List<AbilityEvent>();
        AbilityEventsMessage.Read(ref r, decoded);
        Assert.Equal(events.Select(e => (e.Tick, e.Type, e.Caster, e.Ability, e.Target)), decoded.Select(e => (e.Tick, e.Type, e.Caster, e.Ability, e.Target)));
        Assert.True(Vector2.Distance(events[0].Point, decoded[0].Point) < 0.002f);

        w.Reset();
        new PlayerStateMessage
        {
            Ore = 42,
            Queues = new List<ProductionQueue>(),
            Cooldowns = new List<AbilityCooldown> { new() { UnitId = 11, Ability = OpenRTS.Shared.Abilities.AbilityId.Nova, Remaining = 3.33f } },
        }.Write(w);
        r = new PacketReader(w.ToArray());
        r.ReadByte();
        var ps = PlayerStateMessage.Read(ref r);
        var cd = Assert.Single(ps.Cooldowns);
        Assert.Equal((11, OpenRTS.Shared.Abilities.AbilityId.Nova), (cd.UnitId, cd.Ability));
        Assert.InRange(cd.Remaining, 3.33f, 3.41f); // 0.1초 단위로 올림
    }

    [Fact]
    public void TruncatedOrInvalidCommand_Throws()
    {
        var w = new PacketWriter();
        new UnitCommand { Target = new Vector2(3, 4), UnitIds = new[] { 1 } }.Write(w);
        var bytes = w.ToArray();

        Assert.Throws<PacketFormatException>(() =>
        {
            var r = new PacketReader(bytes, 0, bytes.Length - 1);
            r.ReadByte();
            UnitCommand.Read(ref r);
        });

        bytes[1] = 99; // 없는 명령 종류
        Assert.Throws<PacketFormatException>(() =>
        {
            var r = new PacketReader(bytes);
            r.ReadByte();
            UnitCommand.Read(ref r);
        });
    }
}
