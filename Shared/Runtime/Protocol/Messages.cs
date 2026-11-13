using System;
using System.Collections.Generic;
using System.Numerics;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Shared.Protocol
{
    /// <summary>패킷 첫 바이트. 값이나 형식을 바꾸면 ProtocolVersion 도 올린다.</summary>
    public enum MessageType : byte
    {
        /// <summary>C→S 접속 직후 1회. 프로토콜 버전 확인. (reliable)</summary>
        ClientHello = 1,

        /// <summary>S→C 입장 허가와 내 유닛 ID. (reliable)</summary>
        ServerWelcome = 2,

        /// <summary>C→S 내 유닛 이동 명령. (reliable ordered)</summary>
        MoveCommand = 3,

        /// <summary>S→C 위치 스냅샷 조각. (unreliable — 늦은 것은 클라이언트가 버림)</summary>
        Snapshot = 4,

        /// <summary>S→C 현재 시뮬레이션 설정. 입장 시와 변경 시. (reliable)</summary>
        SimSettings = 5,

        /// <summary>C→S 설정 변경 요청 (디버그). (reliable)</summary>
        SetSimSettings = 6,

        /// <summary>C→S 봇 시나리오 재시작 요청 (디버그). (reliable)</summary>
        ResetScenario = 7,

        /// <summary>S→C 충돌 확인 지표. (unreliable, 2Hz)</summary>
        SimStats = 8,
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

    public struct UnitState : IEquatable<UnitState>
    {
        public int Id;
        public Vector2 Position;

        public UnitState(int id, Vector2 position)
        {
            Id = id;
            Position = position;
        }

        public bool Equals(UnitState other) => Id == other.Id && Position.Equals(other.Position);
        public override bool Equals(object obj) => obj is UnitState o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Id, Position);
    }

    /// <summary>
    /// 한 서버 틱의 유닛 위치. 위치는 맵 범위 기준 16비트로 양자화하고(해상도 약 0.0015),
    /// 한 패킷(MTU)에 다 들어가지 않으면 여러 조각으로 나눠 보낸다.
    /// 클라이언트는 <see cref="OpenRTS.Shared.Interpolation.SnapshotAssembler"/> 로 모든 조각이 모인 틱만 사용한다.
    /// </summary>
    public static class SnapshotMessage
    {
        /// <summary>헤더: type 1 + tick 4 + part 1 + partCount 1 + count 2.</summary>
        public const int HeaderSize = 9;

        /// <summary>유닛당: id 2 + x 2 + y 2.</summary>
        public const int UnitSize = 6;

        /// <summary>
        /// 조각 하나에 담는 기본 유닛 수. LiteNetLib 는 MTU 탐색 전 unreliable 패킷을 1023바이트로 제한하므로
        /// (1023 - 9) / 6 = 169 보다 작게 잡는다. 서버는 실제 허용 크기로 <see cref="UnitsPerPart"/> 를 계산해 쓴다.
        /// </summary>
        public const int DefaultUnitsPerPart = 160;

        public const int MaxUnitId = ushort.MaxValue;

        /// <summary>패킷 최대 크기(바이트)에 들어가는 유닛 수.</summary>
        public static int UnitsPerPart(int maxPacketSize) => Math.Max(1, (maxPacketSize - HeaderSize) / UnitSize);

        public static int PartCount(int unitCount, int unitsPerPart = DefaultUnitsPerPart) =>
            Math.Max(1, (unitCount + unitsPerPart - 1) / unitsPerPart);

        /// <summary><paramref name="part"/> 번째 조각을 쓴다.</summary>
        public static void WritePart(PacketWriter w, uint tick, IReadOnlyList<UnitState> units, int part, int unitsPerPart = DefaultUnitsPerPart)
        {
            var partCount = PartCount(units.Count, unitsPerPart);
            if (partCount > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(units), "Too many units for one snapshot.");
            }

            var start = part * unitsPerPart;
            var count = Math.Max(0, Math.Min(unitsPerPart, units.Count - start));

            w.WriteByte((byte)MessageType.Snapshot);
            w.WriteUInt(tick);
            w.WriteByte((byte)part);
            w.WriteByte((byte)partCount);
            w.WriteUShort((ushort)count);
            for (var i = start; i < start + count; i++)
            {
                w.WriteUShort((ushort)units[i].Id);
                w.WriteShort(Quantize(units[i].Position.X));
                w.WriteShort(Quantize(units[i].Position.Y));
            }
        }

        /// <summary>메시지 타입 바이트를 읽은 뒤 호출한다. <paramref name="units"/> 는 비운 뒤 채운다.</summary>
        public static uint ReadPart(ref PacketReader r, List<UnitState> units, out int part, out int partCount)
        {
            units.Clear();
            var tick = r.ReadUInt();
            part = r.ReadByte();
            partCount = r.ReadByte();
            int count = r.ReadUShort();
            if (partCount == 0 || part >= partCount)
            {
                throw new PacketFormatException($"Bad snapshot part {part}/{partCount}.");
            }

            for (var i = 0; i < count; i++)
            {
                var id = r.ReadUShort();
                var x = Dequantize(r.ReadShort());
                var y = Dequantize(r.ReadShort());
                units.Add(new UnitState(id, new Vector2(x, y)));
            }

            return tick;
        }

        public static short Quantize(float coordinate)
        {
            var v = coordinate / SimConstants.MapHalfSize * short.MaxValue;
            return (short)Math.Round(Math.Max(-short.MaxValue, Math.Min(short.MaxValue, v)));
        }

        public static float Dequantize(short value) => value * SimConstants.MapHalfSize / short.MaxValue;
    }
}
