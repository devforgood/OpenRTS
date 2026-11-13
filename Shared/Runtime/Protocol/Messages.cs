using System.Collections.Generic;
using System.Numerics;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Shared.Protocol
{
    /// <summary>패킷 첫 바이트. 값을 바꾸면 ProtocolVersion 도 올린다.</summary>
    public enum MessageType : byte
    {
        /// <summary>C→S 접속 직후 1회. 프로토콜 버전 확인. (reliable)</summary>
        ClientHello = 1,

        /// <summary>S→C 입장 허가와 내 유닛 ID. (reliable)</summary>
        ServerWelcome = 2,

        /// <summary>C→S 내 유닛 이동 명령. (reliable ordered)</summary>
        MoveCommand = 3,

        /// <summary>S→C 위치 스냅샷. (unreliable — 늦은 것은 클라이언트가 버림)</summary>
        Snapshot = 4,
    }

    public struct ClientHello
    {
        public int ProtocolVersion;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.ClientHello);
            w.WriteInt(ProtocolVersion);
        }

        public static ClientHello Read(ref PacketReader r) => new ClientHello { ProtocolVersion = r.ReadInt() };
    }

    public struct ServerWelcome
    {
        public int UnitId;
        public uint ServerTick;
        public int TickRate;
        public int SnapshotRate;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.ServerWelcome);
            w.WriteInt(UnitId);
            w.WriteUInt(ServerTick);
            w.WriteInt(TickRate);
            w.WriteInt(SnapshotRate);
        }

        public static ServerWelcome Read(ref PacketReader r) => new ServerWelcome
        {
            UnitId = r.ReadInt(),
            ServerTick = r.ReadUInt(),
            TickRate = r.ReadInt(),
            SnapshotRate = r.ReadInt(),
        };
    }

    public struct MoveCommand
    {
        public Vector2 Target;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.MoveCommand);
            w.WriteFloat(Target.X);
            w.WriteFloat(Target.Y);
        }

        public static MoveCommand Read(ref PacketReader r) => new MoveCommand
        {
            Target = new Vector2(r.ReadFloat(), r.ReadFloat()),
        };
    }

    public struct UnitState
    {
        public int Id;
        public Vector2 Position;

        public UnitState(int id, Vector2 position)
        {
            Id = id;
            Position = position;
        }
    }

    /// <summary>
    /// 한 서버 틱의 유닛 위치 목록. 지금은 모든 유닛을 float 로 보낸다.
    /// 양자화·델타·시야 필터는 README 4.2 / 로드맵 3단계에서 추가한다.
    /// </summary>
    public static class SnapshotMessage
    {
        public static void Write(PacketWriter w, uint tick, IReadOnlyList<UnitState> units)
        {
            w.WriteByte((byte)MessageType.Snapshot);
            w.WriteUInt(tick);
            w.WriteUShort((ushort)units.Count);
            for (var i = 0; i < units.Count; i++)
            {
                w.WriteInt(units[i].Id);
                w.WriteFloat(units[i].Position.X);
                w.WriteFloat(units[i].Position.Y);
            }
        }

        /// <summary>메시지 타입 바이트를 읽은 뒤 호출한다. <paramref name="units"/> 는 비운 뒤 채운다.</summary>
        public static uint Read(ref PacketReader r, List<UnitState> units)
        {
            units.Clear();
            var tick = r.ReadUInt();
            int count = r.ReadUShort();
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadInt();
                var x = r.ReadFloat();
                var y = r.ReadFloat();
                units.Add(new UnitState(id, new Vector2(x, y)));
            }

            return tick;
        }
    }
}
