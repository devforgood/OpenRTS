using System.Numerics;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Server.Tests;

public class MessageTests
{
    [Fact]
    public void Snapshot_RoundTrips()
    {
        var w = new PacketWriter(4); // 작은 버퍼로 시작해 확장도 검증
        var units = new[] { new UnitState(1, new Vector2(1.5f, -2f)), new UnitState(42, new Vector2(-49.9f, 50f)) };
        SnapshotMessage.Write(w, 1234, units);

        var r = new PacketReader(w.ToArray());
        Assert.Equal(MessageType.Snapshot, (MessageType)r.ReadByte());
        var decoded = new List<UnitState>();
        Assert.Equal(1234u, SnapshotMessage.Read(ref r, decoded));
        Assert.Equal(units, decoded);
        Assert.Equal(0, r.Remaining);
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
