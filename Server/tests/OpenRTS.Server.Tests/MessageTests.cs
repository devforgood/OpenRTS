using System.Numerics;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Server.Tests;

public class MessageTests
{
    [Fact]
    public void Snapshot_RoundTrips_WithinQuantizationError()
    {
        var w = new PacketWriter(4); // 작은 버퍼로 시작해 확장도 검증
        var units = new[] { new UnitState(1, new Vector2(1.5f, -2f)), new UnitState(42, new Vector2(-49.9f, 50f)) };
        SnapshotMessage.WritePart(w, 1234, units, 0);

        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.Snapshot, (MessageType)r.ReadByte());
        var decoded = new List<UnitState>();
        Assert.Equal(1234u, SnapshotMessage.ReadPart(ref r, decoded, out var part, out var partCount));
        Assert.Equal((0, 1), (part, partCount));
        Assert.Equal(0, r.Remaining);
        Assert.Equal(units.Select(u => u.Id), decoded.Select(u => u.Id));
        for (var i = 0; i < units.Length; i++)
        {
            Assert.True(Vector2.Distance(units[i].Position, decoded[i].Position) < 0.002f);
        }
    }

    [Fact]
    public void Snapshot_SplitsIntoParts_ThatFitMtu()
    {
        var units = Enumerable.Range(1, 500).Select(i => new UnitState(i, new Vector2(i % 50, -i % 50))).ToArray();
        var parts = SnapshotMessage.PartCount(units.Length);
        Assert.Equal(4, parts); // 160개씩

        var assembler = new OpenRTS.Shared.Interpolation.SnapshotAssembler();
        var w = new PacketWriter();
        var partUnits = new List<UnitState>();
        IReadOnlyList<UnitState>? all = null;
        for (var p = parts - 1; p >= 0; p--) // 순서가 뒤바뀌어 도착해도 된다
        {
            w.Reset();
            SnapshotMessage.WritePart(w, 7, units, p);
            Assert.True(w.Length <= 1023, $"part {p} is {w.Length} bytes"); // LiteNetLib MTU 탐색 전 한도

            var r = new PacketReader(w.ToArray());
            r.ReadByte();
            var tick = SnapshotMessage.ReadPart(ref r, partUnits, out var part, out var count);
            var done = assembler.Add(tick, part, count, partUnits, out _, out var result);
            Assert.Equal(p == 0, done);
            if (done)
            {
                all = result;
            }
        }

        Assert.Equal(units.Select(u => u.Id).Order(), all!.Select(u => u.Id).Order());
    }

    [Fact]
    public void Assembler_DropsIncompleteTick_WhenNewerArrives()
    {
        var assembler = new OpenRTS.Shared.Interpolation.SnapshotAssembler();
        var one = new List<UnitState> { new(1, Vector2.Zero) };

        Assert.False(assembler.Add(10, 0, 2, one, out _, out _));   // 10의 조각 1/2
        Assert.True(assembler.Add(11, 0, 1, one, out var t, out _)); // 11 완성 → 10 버림
        Assert.Equal(11u, t);
        Assert.False(assembler.Add(10, 1, 2, one, out _, out _));   // 늦게 온 10의 나머지
        Assert.Equal(1, assembler.IncompleteDropped);
    }

    [Fact]
    public void SimSettings_RoundTrip_AndClamp()
    {
        var s = SimSettings.Default;
        s.BotCount = 123;
        s.Scenario = BotScenario.CircleSwap;
        s.Avoidance = false;
        var w = new PacketWriter();
        s.Write(w, MessageType.SetSimSettings);
        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.SetSimSettings, (MessageType)r.ReadByte());
        Assert.Equal(s, SimSettings.Read(ref r));

        var bad = new SimSettings { BotCount = 60000, UnitRadius = float.NaN, UnitSpeed = 999, Scenario = (BotScenario)77, MaxNeighbors = 0, TimeHorizon = -1, NeighborDist = 0 };
        var c = bad.Clamped();
        Assert.Equal(SimSettings.MaxBots, c.BotCount);
        Assert.Equal(BotScenario.Wander, c.Scenario);
        Assert.Equal(0.1f, c.UnitRadius);
        Assert.Equal(20f, c.UnitSpeed);
        Assert.Equal(1, c.MaxNeighbors);
    }

    [Fact]
    public void Welcome_And_Move_RoundTrip()
    {
        var w = new PacketWriter();
        new ServerWelcome { UnitId = 7, ServerTick = 99, TickRate = 30, SnapshotRate = 15 }.Write(w);
        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.ServerWelcome, (MessageType)r.ReadByte());
        var welcome = ServerWelcome.Read(ref r);
        Assert.Equal((7, 99u, 30, 15), (welcome.UnitId, welcome.ServerTick, welcome.TickRate, welcome.SnapshotRate));

        w.Reset();
        new MoveCommand { Target = new Vector2(3, 4) }.Write(w);
        r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.MoveCommand, (MessageType)r.ReadByte());
        Assert.Equal(new Vector2(3, 4), MoveCommand.Read(ref r).Target);
    }

    [Fact]
    public void TruncatedPacket_Throws()
    {
        var w = new PacketWriter();
        new MoveCommand { Target = new Vector2(3, 4) }.Write(w);
        var bytes = w.ToArray();

        Assert.Throws<PacketFormatException>(() =>
        {
            var r = new PacketReader(bytes, 0, bytes.Length - 1);
            r.ReadByte();
            MoveCommand.Read(ref r);
        });
    }
}
