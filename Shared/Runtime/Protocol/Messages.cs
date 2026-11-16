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

        /// <summary>S→C 입장 허가와 플레이어 번호. (reliable)</summary>
        ServerWelcome = 2,

        /// <summary>C→S 선택한 내 유닛들에게 내리는 명령 (이동·공격 이동·공격·정지). (reliable ordered)</summary>
        UnitCommand = 3,

        /// <summary>S→C 위치 스냅샷 조각 (전체 또는 델타). (unreliable — 늦은 것은 클라이언트가 버림)</summary>
        Snapshot = 4,

        /// <summary>S→C 현재 시뮬레이션 설정. 입장 시와 변경 시. (reliable)</summary>
        SimSettings = 5,

        /// <summary>C→S 설정 변경 요청 (디버그). (reliable)</summary>
        SetSimSettings = 6,

        /// <summary>C→S 봇 시나리오 재시작 요청 (디버그). (reliable)</summary>
        ResetScenario = 7,

        /// <summary>S→C 충돌 확인 지표. (unreliable, 2Hz)</summary>
        SimStats = 8,

        /// <summary>C→S 받은 스냅샷 틱 확인. 델타 압축의 기준점. (unreliable)</summary>
        SnapshotAck = 9,

        /// <summary>S→C 전투 이벤트 (타격·발사·사망 연출용). (unreliable)</summary>
        CombatEvents = 10,
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
        /// <summary>이 클라이언트의 플레이어 번호. 스냅샷의 Owner 와 같으면 내 유닛.</summary>
        public byte PlayerId;
        public uint ServerTick;
        public int TickRate;
        public int SnapshotRate;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.ServerWelcome);
            w.WriteByte(PlayerId);
            w.WriteUInt(ServerTick);
            w.WriteInt(TickRate);
            w.WriteInt(SnapshotRate);
        }

        public static ServerWelcome Read(ref PacketReader r) => new ServerWelcome
        {
            PlayerId = r.ReadByte(),
            ServerTick = r.ReadUInt(),
            TickRate = r.ReadInt(),
            SnapshotRate = r.ReadInt(),
        };
    }

    public enum CommandType : byte
    {
        /// <summary>그 지점으로 이동. 가는 동안 적을 무시한다.</summary>
        Move = 0,

        /// <summary>그 지점으로 가면서 만나는 적과 싸운다.</summary>
        AttackMove = 1,

        /// <summary>지정한 유닛(TargetUnit)을 쫓아가 공격한다.</summary>
        Attack = 2,

        /// <summary>하던 일을 멈춘다.</summary>
        Stop = 3,
    }

    /// <summary>선택한 내 유닛들에게 내리는 명령. 서버가 소유권을 확인하고, 이동이면 대형을 짠다.</summary>
    public struct UnitCommand
    {
        /// <summary>한 명령에 담을 수 있는 최대 유닛 수.</summary>
        public const int MaxUnits = 255;

        public CommandType Type;
        public Vector2 Target;
        public int TargetUnit;

        /// <summary>
        /// 보낼 때 클라이언트가 추정한 서버 틱. 서버는 (받은 틱 - 이 값)으로 명령 지연을 잰다.
        /// 나중에 지연 보상·명령 순서 판정에 쓴다.
        /// </summary>
        public uint ClientTick;

        public IReadOnlyList<int> UnitIds;

        public void Write(PacketWriter w)
        {
            var ids = UnitIds ?? Array.Empty<int>();
            var count = Math.Min(ids.Count, MaxUnits);
            w.WriteByte((byte)MessageType.UnitCommand);
            w.WriteByte((byte)Type);
            w.WriteFloat(Target.X);
            w.WriteFloat(Target.Y);
            w.WriteUShort((ushort)TargetUnit);
            w.WriteUInt(ClientTick);
            w.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
            {
                w.WriteUShort((ushort)ids[i]);
            }
        }

        public static UnitCommand Read(ref PacketReader r)
        {
            var cmd = new UnitCommand
            {
                Type = (CommandType)r.ReadByte(),
                Target = new Vector2(r.ReadFloat(), r.ReadFloat()),
                TargetUnit = r.ReadUShort(),
                ClientTick = r.ReadUInt(),
            };
            if (!Enum.IsDefined(typeof(CommandType), cmd.Type))
            {
                throw new PacketFormatException($"Unknown command {(byte)cmd.Type}.");
            }

            int count = r.ReadByte();
            var ids = new int[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = r.ReadUShort();
            }

            cmd.UnitIds = ids;
            return cmd;
        }
    }

    public enum CombatEventType : byte
    {
        /// <summary>근접 타격 (Attacker → Target 즉시 피해).</summary>
        MeleeHit = 0,

        /// <summary>투사체 발사 (Attacker → Target 으로 날아감, 속도는 공격자 종류의 ProjectileSpeed).</summary>
        ProjectileLaunch = 1,

        /// <summary>유닛 사망 (Target 이 죽음, Attacker 가 막타).</summary>
        Death = 2,
    }

    public struct CombatEvent
    {
        public uint Tick;
        public CombatEventType Type;
        public int Attacker;
        public int Target;
    }

    /// <summary>
    /// S→C 최근 틱들의 전투 이벤트 (연출용). 스냅샷과 같은 주기로 보내고 unreliable 이다 —
    /// 잃어버려도 체력·사망은 스냅샷이 권위를 가지므로 연출 하나가 빠질 뿐이다.
    /// </summary>
    public static class CombatEventsMessage
    {
        /// <summary>패킷 하나에 담는 최대 이벤트 수 (헤더 3 + 이벤트당 9바이트 → 1023 바이트 안).</summary>
        public const int MaxPerPacket = 110;

        public static void Write(PacketWriter w, IReadOnlyList<CombatEvent> events, int start, int count)
        {
            w.WriteByte((byte)MessageType.CombatEvents);
            w.WriteUShort((ushort)count);
            for (var i = start; i < start + count; i++)
            {
                var e = events[i];
                w.WriteUInt(e.Tick);
                w.WriteByte((byte)e.Type);
                w.WriteUShort((ushort)e.Attacker);
                w.WriteUShort((ushort)e.Target);
            }
        }

        public static void Read(ref PacketReader r, List<CombatEvent> output)
        {
            output.Clear();
            int count = r.ReadUShort();
            for (var i = 0; i < count; i++)
            {
                output.Add(new CombatEvent
                {
                    Tick = r.ReadUInt(),
                    Type = (CombatEventType)r.ReadByte(),
                    Attacker = r.ReadUShort(),
                    Target = r.ReadUShort(),
                });
            }
        }
    }

    /// <summary>C→S 다 모아서 적용한 스냅샷 틱. 서버는 이 틱을 기준(baseline)으로 바뀐 유닛만 보낸다.</summary>
    public struct SnapshotAck
    {
        public uint Tick;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.SnapshotAck);
            w.WriteUInt(Tick);
        }

        public static SnapshotAck Read(ref PacketReader r) => new SnapshotAck { Tick = r.ReadUInt() };
    }

    [Flags]
    public enum UnitFlags : byte
    {
        None = 0,

        /// <summary>공격 선딜 중 (멈춰서 휘두르는 중).</summary>
        WindingUp = 1,
    }

    /// <summary>클라이언트가 복원한 유닛 상태 한 개.</summary>
    public struct UnitState : IEquatable<UnitState>
    {
        public int Id;
        public Vector2 Position;

        /// <summary>소유 플레이어 번호. 0 은 서버 봇.</summary>
        public byte Owner;

        /// <summary>팀. 0 은 중립(전투 안 함).</summary>
        public byte Team;

        public Combat.UnitKind Kind;

        /// <summary>남은 체력 비율 (0~1, 1/255 단위).</summary>
        public float Health;

        /// <summary>공격 대상 유닛 ID (없으면 0).</summary>
        public int AttackTarget;

        public UnitFlags Flags;

        public UnitState(int id, Vector2 position, byte owner = 0)
        {
            Id = id;
            Position = position;
            Owner = owner;
            Team = 0;
            Kind = Combat.UnitKind.Melee;
            Health = 1f;
            AttackTarget = 0;
            Flags = UnitFlags.None;
        }

        public bool Equals(UnitState o) =>
            Id == o.Id && Position.Equals(o.Position) && Owner == o.Owner && Team == o.Team && Kind == o.Kind &&
            Health.Equals(o.Health) && AttackTarget == o.AttackTarget && Flags == o.Flags;

        public override bool Equals(object obj) => obj is UnitState o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Id, Position, Owner, Team, Kind, Health, AttackTarget, Flags);
    }
}

