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

        /// <summary>S→C 이 플레이어의 자원·생산 대기열·능력 재사용 대기. (unreliable, 5Hz)</summary>
        PlayerState = 11,

        /// <summary>S→C 능력 연출 이벤트 (시전·투사체·범위·돌진·적중). (unreliable)</summary>
        AbilityEvents = 12,
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

        /// <summary>일꾼: 광맥(TargetUnit)을 캐서 본진에 나르기를 반복한다.</summary>
        Gather = 4,

        /// <summary>일꾼: Target 위치에 Kind 건물을 짓는다 (비용은 명령 때 낸다).</summary>
        Build = 5,

        /// <summary>건물: Kind 유닛을 생산 대기열에 넣는다 (비용은 넣을 때 낸다).</summary>
        Train = 6,

        /// <summary>능력 사용: Ability 를 TargetUnit(유닛 대상) 또는 Target(지점)에. 사거리 밖이면 걸어가서 쓴다.</summary>
        Cast = 7,
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

        /// <summary>Build·Train 에서 지을/만들 종류.</summary>
        public Combat.UnitKind Kind;

        /// <summary>Cast 에서 쓸 능력.</summary>
        public Abilities.AbilityId Ability;

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
            w.WriteByte((byte)Kind);
            w.WriteByte((byte)Ability);
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
                Kind = (Combat.UnitKind)r.ReadByte(),
                Ability = (Abilities.AbilityId)r.ReadByte(),
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

    /// <summary>재사용 대기 중인 능력 하나 (내 유닛별).</summary>
    public struct AbilityCooldown
    {
        public int UnitId;
        public Abilities.AbilityId Ability;

        /// <summary>남은 시간(초, 0.1초 단위로 보냄).</summary>
        public float Remaining;
    }

    /// <summary>생산 대기열 하나 (건물별).</summary>
    public struct ProductionQueue
    {
        public int BuildingId;

        /// <summary>대기열 순서대로의 종류 (첫 번째가 생산 중).</summary>
        public Combat.UnitKind[] Items;

        /// <summary>첫 번째 항목 진행도 (0~1).</summary>
        public float Progress;
    }

    /// <summary>
    /// S→C 이 플레이어만의 상태: 가진 자원, 내 건물들의 생산 대기열, 내 유닛의 능력 재사용 대기
    /// (5Hz, unreliable — 다음 것이 덮어쓴다). 남의 것은 보내지 않는다.
    /// </summary>
    public struct PlayerStateMessage
    {
        /// <summary>한 메시지에 담는 최대 재사용 대기 항목 수 (패킷 크기 한도).</summary>
        public const int MaxCooldowns = 150;

        public int Ore;
        public List<ProductionQueue> Queues;

        /// <summary>대기 중인 능력만 (준비된 능력은 없음).</summary>
        public List<AbilityCooldown> Cooldowns;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.PlayerState);
            w.WriteInt(Ore);
            var queues = Queues ?? new List<ProductionQueue>();
            var count = Math.Min(queues.Count, byte.MaxValue);
            w.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
            {
                var q = queues[i];
                w.WriteUShort((ushort)q.BuildingId);
                var items = q.Items ?? Array.Empty<Combat.UnitKind>();
                w.WriteByte((byte)Math.Min(items.Length, byte.MaxValue));
                for (var k = 0; k < items.Length && k < byte.MaxValue; k++)
                {
                    w.WriteByte((byte)items[k]);
                }

                w.WriteByte((byte)Math.Round(Math.Max(0, Math.Min(1, q.Progress)) * 255));
            }

            var cooldowns = Cooldowns ?? new List<AbilityCooldown>();
            var cdCount = Math.Min(cooldowns.Count, MaxCooldowns);
            w.WriteByte((byte)cdCount);
            for (var i = 0; i < cdCount; i++)
            {
                var c = cooldowns[i];
                w.WriteUShort((ushort)c.UnitId);
                w.WriteByte((byte)c.Ability);
                w.WriteUShort((ushort)Math.Min(ushort.MaxValue, Math.Ceiling(Math.Max(0, c.Remaining) * 10)));
            }
        }

        public static PlayerStateMessage Read(ref PacketReader r)
        {
            var msg = new PlayerStateMessage { Ore = r.ReadInt(), Queues = new List<ProductionQueue>() };
            int count = r.ReadByte();
            for (var i = 0; i < count; i++)
            {
                var q = new ProductionQueue { BuildingId = r.ReadUShort() };
                q.Items = new Combat.UnitKind[r.ReadByte()];
                for (var k = 0; k < q.Items.Length; k++)
                {
                    q.Items[k] = (Combat.UnitKind)r.ReadByte();
                }

                q.Progress = r.ReadByte() / 255f;
                msg.Queues.Add(q);
            }

            msg.Cooldowns = new List<AbilityCooldown>();
            int cdCount = r.ReadByte();
            for (var i = 0; i < cdCount; i++)
            {
                msg.Cooldowns.Add(new AbilityCooldown
                {
                    UnitId = r.ReadUShort(),
                    Ability = (Abilities.AbilityId)r.ReadByte(),
                    Remaining = r.ReadUShort() / 10f,
                });
            }

            return msg;
        }
    }

    public enum AbilityEventType : byte
    {
        /// <summary>시전 시작 (선딜). Point = 대상 지점.</summary>
        CastStart = 0,

        /// <summary>시전 취소 (기절·사망·다른 명령·대상 상실).</summary>
        CastCancel = 1,

        /// <summary>직선 투사체 발사. Point = 날아갈 끝 지점(사거리 끝).</summary>
        ProjectileLaunch = 2,

        /// <summary>투사체 끝 (맞음·벽·사거리). Point = 끝난 위치.</summary>
        ProjectileEnd = 3,

        /// <summary>범위 예고 (지연이 있는 범위). Point = 중심.</summary>
        AreaTelegraph = 4,

        /// <summary>범위 발동. Point = 중심.</summary>
        AreaHit = 5,

        /// <summary>돌진 시작. Point = 도착 지점.</summary>
        DashStart = 6,

        /// <summary>정신 집중 시작.</summary>
        ChannelStart = 7,

        /// <summary>효과가 유닛에 들어감 (Target = 맞은 유닛).</summary>
        Hit = 8,
    }

    public struct AbilityEvent
    {
        public uint Tick;
        public AbilityEventType Type;
        public int Caster;
        public Abilities.AbilityId Ability;
        public int Target;
        public Vector2 Point;
    }

    /// <summary>
    /// S→C 능력 연출 이벤트 (스냅샷과 같은 주기, unreliable). 체력·상태는 스냅샷이 권위이고 이것은 보이기 위한 것.
    /// 팀마다 시전자·대상·지점 중 하나가 보일 때만 보내고, 안 보이는 유닛 ID 는 0 으로 가린다.
    /// </summary>
    public static class AbilityEventsMessage
    {
        /// <summary>패킷 하나에 담는 최대 이벤트 수 (헤더 3 + 이벤트당 14바이트).</summary>
        public const int MaxPerPacket = 70;

        public static void Write(PacketWriter w, IReadOnlyList<AbilityEvent> events, int start, int count)
        {
            w.WriteByte((byte)MessageType.AbilityEvents);
            w.WriteUShort((ushort)count);
            for (var i = start; i < start + count; i++)
            {
                var e = events[i];
                w.WriteUInt(e.Tick);
                w.WriteByte((byte)e.Type);
                w.WriteUShort((ushort)e.Caster);
                w.WriteByte((byte)e.Ability);
                w.WriteUShort((ushort)e.Target);
                w.WriteShort(SnapshotMessage.Quantize(e.Point.X));
                w.WriteShort(SnapshotMessage.Quantize(e.Point.Y));
            }
        }

        public static void Read(ref PacketReader r, List<AbilityEvent> output)
        {
            output.Clear();
            int count = r.ReadUShort();
            for (var i = 0; i < count; i++)
            {
                output.Add(new AbilityEvent
                {
                    Tick = r.ReadUInt(),
                    Type = (AbilityEventType)r.ReadByte(),
                    Caster = r.ReadUShort(),
                    Ability = (Abilities.AbilityId)r.ReadByte(),
                    Target = r.ReadUShort(),
                    Point = new Vector2(SnapshotMessage.Dequantize(r.ReadShort()), SnapshotMessage.Dequantize(r.ReadShort())),
                });
            }
        }
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

        /// <summary>일꾼이 자원을 들고 있음.</summary>
        Carrying = 2,

        /// <summary>건물이 아직 지어지는 중 (체력 비율 = 진행도).</summary>
        UnderConstruction = 4,

        /// <summary>건물이 생산 중.</summary>
        Producing = 8,

        /// <summary>기절.</summary>
        Stunned = 16,

        /// <summary>능력 시전 중 (선딜·정신 집중·돌진).</summary>
        Casting = 32,

        /// <summary>둔화.</summary>
        Slowed = 64,

        /// <summary>보호막이 있음.</summary>
        Shielded = 128,
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

