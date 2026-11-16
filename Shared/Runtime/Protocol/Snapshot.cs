using System;
using System.Collections.Generic;
using System.Numerics;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Shared.Protocol
{
    /// <summary>스냅샷 항목에서 이번에 실린 필드 묶음. 델타는 바뀐 묶음만 보낸다.</summary>
    [Flags]
    public enum SnapshotFields : byte
    {
        None = 0,

        /// <summary>X, Y (4바이트).</summary>
        Position = 1,

        /// <summary>Owner, Team, Kind (3바이트) — 거의 바뀌지 않는다.</summary>
        Info = 2,

        /// <summary>Health (1바이트).</summary>
        Health = 4,

        /// <summary>AttackTarget, Flags (3바이트).</summary>
        Action = 8,

        All = Position | Info | Health | Action,

        /// <summary>baseline 에서 사라진 유닛 (다른 필드 없음).</summary>
        Removed = 0x80,
    }

    /// <summary>
    /// 스냅샷에 실리는 유닛 하나 (양자화된 값). 델타 비교도 이 값으로 하므로
    /// 서버와 클라이언트가 같은 값을 기준으로 삼는다. <see cref="Fields"/> 는 전송할 때만 의미가 있다.
    /// </summary>
    public struct SnapshotEntry
    {
        public ushort Id;
        public SnapshotFields Fields;
        public short X;
        public short Y;
        public byte Owner;
        public byte Team;
        public UnitKind Kind;

        /// <summary>체력 비율 1~255 (살아 있으면 0 이 아니다).</summary>
        public byte Health;

        public ushort AttackTarget;
        public UnitFlags Flags;

        public bool IsRemoved => (Fields & SnapshotFields.Removed) != 0;

        public static SnapshotEntry From(in UnitState u) => new SnapshotEntry
        {
            Id = (ushort)u.Id,
            Fields = SnapshotFields.All,
            X = SnapshotMessage.Quantize(u.Position.X),
            Y = SnapshotMessage.Quantize(u.Position.Y),
            Owner = u.Owner,
            Team = u.Team,
            Kind = u.Kind,
            Health = QuantizeHealth(u.Health),
            AttackTarget = (ushort)u.AttackTarget,
            Flags = u.Flags,
        };

        public static SnapshotEntry Removed(ushort id) => new SnapshotEntry { Id = id, Fields = SnapshotFields.Removed };

        public UnitState ToState() => new UnitState
        {
            Id = Id,
            Position = new Vector2(SnapshotMessage.Dequantize(X), SnapshotMessage.Dequantize(Y)),
            Owner = Owner,
            Team = Team,
            Kind = Kind,
            Health = Health / 255f,
            AttackTarget = AttackTarget,
            Flags = Flags,
        };

        /// <summary>살아 있는 유닛은 아무리 적어도 1 이 되도록 올림한다.</summary>
        public static byte QuantizeHealth(float ratio)
        {
            if (!(ratio > 0))
            {
                return 0;
            }

            return (byte)Math.Max(1, Math.Min(255, (int)Math.Ceiling(ratio * 255)));
        }

        /// <summary>두 항목에서 값이 다른 필드 묶음.</summary>
        public static SnapshotFields Changed(in SnapshotEntry a, in SnapshotEntry b)
        {
            var f = SnapshotFields.None;
            if (a.X != b.X || a.Y != b.Y) f |= SnapshotFields.Position;
            if (a.Owner != b.Owner || a.Team != b.Team || a.Kind != b.Kind) f |= SnapshotFields.Info;
            if (a.Health != b.Health) f |= SnapshotFields.Health;
            if (a.AttackTarget != b.AttackTarget || a.Flags != b.Flags) f |= SnapshotFields.Action;
            return f;
        }

        /// <summary>값이 같은지 (Fields 는 비교하지 않음).</summary>
        public bool SameValues(in SnapshotEntry o) => Id == o.Id && Changed(this, o) == SnapshotFields.None;

        /// <summary><paramref name="delta"/> 에 실린 필드만 이 항목에 덮어쓴다.</summary>
        public void Merge(in SnapshotEntry delta)
        {
            var f = delta.Fields;
            if ((f & SnapshotFields.Position) != 0) { X = delta.X; Y = delta.Y; }
            if ((f & SnapshotFields.Info) != 0) { Owner = delta.Owner; Team = delta.Team; Kind = delta.Kind; }
            if ((f & SnapshotFields.Health) != 0) { Health = delta.Health; }
            if ((f & SnapshotFields.Action) != 0) { AttackTarget = delta.AttackTarget; Flags = delta.Flags; }
        }

        public override string ToString() => $"#{Id} {Fields} ({X},{Y}) o{Owner} t{Team} {Kind} hp{Health} ->{AttackTarget} {Flags}";
    }

    /// <summary>
    /// 한 서버 틱의 유닛 상태. 위치는 맵 범위 기준 16비트로 양자화하고(해상도 약 0.0015),
    /// 한 패킷(MTU)에 다 들어가지 않으면 여러 조각으로 나눠 보낸다.
    ///
    /// <para><b>델타</b>: <c>baseTick</c> 이 0 이 아니면 그 틱(클라이언트가 확인 응답한 스냅샷) 대비
    /// 바뀐 유닛의 바뀐 필드 묶음과, 제거된 유닛만 담는다. 0 이면 전체 스냅샷.</para>
    /// 클라이언트는 <see cref="OpenRTS.Shared.Interpolation.SnapshotReceiver"/> 로 조각을 모아 전체 상태를 복원한다.
    /// </summary>
    public static class SnapshotMessage
    {
        /// <summary>전체 스냅샷 표시 (baseTick).</summary>
        public const uint NoBaseline = 0;

        /// <summary>헤더: type 1 + tick 4 + baseTick 4 + part 1 + partCount 1 + count 2.</summary>
        public const int HeaderSize = 13;

        /// <summary>항목 공통: id 2 + fields 1.</summary>
        public const int EntryHeaderSize = 3;

        /// <summary>모든 필드가 실린 항목 크기: 3 + 위치 4 + 정보 3 + 체력 1 + 행동 3.</summary>
        public const int FullEntrySize = 14;

        /// <summary>LiteNetLib 가 MTU 탐색 전에 허용하는 unreliable 패킷 크기.</summary>
        public const int DefaultMaxPacketSize = 1023;

        public const int MaxUnitId = ushort.MaxValue;

        public static int EntrySize(in SnapshotEntry e)
        {
            var size = EntryHeaderSize;
            var f = e.Fields;
            if ((f & SnapshotFields.Removed) != 0) return size;
            if ((f & SnapshotFields.Position) != 0) size += 4;
            if ((f & SnapshotFields.Info) != 0) size += 3;
            if ((f & SnapshotFields.Health) != 0) size += 1;
            if ((f & SnapshotFields.Action) != 0) size += 3;
            return size;
        }

        /// <summary>
        /// 항목을 패킷 크기에 맞게 조각으로 나눈다. <paramref name="parts"/> 에 (시작, 개수)를 채운다.
        /// 항목이 없어도 빈 조각 하나는 보낸다 (클라이언트가 그 틱을 확인 응답할 수 있게).
        /// </summary>
        public static void Plan(IReadOnlyList<SnapshotEntry> entries, int maxPacketSize, List<(int Start, int Count)> parts)
        {
            parts.Clear();
            var budget = Math.Max(FullEntrySize, maxPacketSize - HeaderSize);
            var start = 0;
            var used = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var size = EntrySize(entries[i]);
                if (used + size > budget && i > start)
                {
                    parts.Add((start, i - start));
                    start = i;
                    used = 0;
                }

                used += size;
            }

            parts.Add((start, entries.Count - start));
            if (parts.Count > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), "Too many units for one snapshot.");
            }
        }

        /// <summary>조각 하나를 쓴다.</summary>
        public static void WritePart(PacketWriter w, uint tick, uint baseTick, IReadOnlyList<SnapshotEntry> entries, int start, int count, int part, int partCount)
        {
            w.WriteByte((byte)MessageType.Snapshot);
            w.WriteUInt(tick);
            w.WriteUInt(baseTick);
            w.WriteByte((byte)part);
            w.WriteByte((byte)partCount);
            w.WriteUShort((ushort)count);
            for (var i = start; i < start + count; i++)
            {
                var e = entries[i];
                w.WriteUShort(e.Id);
                w.WriteByte((byte)e.Fields);
                var f = e.Fields;
                if ((f & SnapshotFields.Removed) != 0)
                {
                    continue;
                }

                if ((f & SnapshotFields.Position) != 0)
                {
                    w.WriteShort(e.X);
                    w.WriteShort(e.Y);
                }

                if ((f & SnapshotFields.Info) != 0)
                {
                    w.WriteByte(e.Owner);
                    w.WriteByte(e.Team);
                    w.WriteByte((byte)e.Kind);
                }

                if ((f & SnapshotFields.Health) != 0)
                {
                    w.WriteByte(e.Health);
                }

                if ((f & SnapshotFields.Action) != 0)
                {
                    w.WriteUShort(e.AttackTarget);
                    w.WriteByte((byte)e.Flags);
                }
            }
        }

        /// <summary>전체를 한 번에 쓰기 (테스트·작은 스냅샷용). 조각 하나에 다 들어가야 한다.</summary>
        public static void WriteSingle(PacketWriter w, uint tick, uint baseTick, IReadOnlyList<SnapshotEntry> entries) =>
            WritePart(w, tick, baseTick, entries, 0, entries.Count, 0, 1);

        /// <summary>메시지 타입 바이트를 읽은 뒤 호출한다. <paramref name="entries"/> 는 비운 뒤 채운다.</summary>
        public static uint ReadPart(ref PacketReader r, List<SnapshotEntry> entries, out uint baseTick, out int part, out int partCount)
        {
            entries.Clear();
            var tick = r.ReadUInt();
            baseTick = r.ReadUInt();
            part = r.ReadByte();
            partCount = r.ReadByte();
            int count = r.ReadUShort();
            if (partCount == 0 || part >= partCount)
            {
                throw new PacketFormatException($"Bad snapshot part {part}/{partCount}.");
            }

            if (baseTick != NoBaseline && baseTick >= tick)
            {
                throw new PacketFormatException($"Bad snapshot baseline {baseTick} for tick {tick}.");
            }

            for (var i = 0; i < count; i++)
            {
                var e = new SnapshotEntry { Id = r.ReadUShort(), Fields = (SnapshotFields)r.ReadByte() };
                var f = e.Fields;
                if ((f & SnapshotFields.Removed) == 0)
                {
                    if ((f & SnapshotFields.Position) != 0)
                    {
                        e.X = r.ReadShort();
                        e.Y = r.ReadShort();
                    }

                    if ((f & SnapshotFields.Info) != 0)
                    {
                        e.Owner = r.ReadByte();
                        e.Team = r.ReadByte();
                        e.Kind = (UnitKind)r.ReadByte();
                    }

                    if ((f & SnapshotFields.Health) != 0)
                    {
                        e.Health = r.ReadByte();
                    }

                    if ((f & SnapshotFields.Action) != 0)
                    {
                        e.AttackTarget = r.ReadUShort();
                        e.Flags = (UnitFlags)r.ReadByte();
                    }
                }

                entries.Add(e);
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

    /// <summary>ID 순으로 정렬된 스냅샷 목록끼리의 차이 계산과 복원.</summary>
    public static class SnapshotDelta
    {
        public static readonly Comparison<SnapshotEntry> ById = (a, b) => a.Id.CompareTo(b.Id);

        /// <summary>
        /// <paramref name="current"/> 를 만들기 위해 <paramref name="baseline"/> 에 적용할 변경분.
        /// 새 유닛은 모든 필드, 바뀐 유닛은 바뀐 필드 묶음만, 사라진 유닛은 Removed 로 담는다.
        /// 두 입력은 ID 순 정렬이어야 하고, 출력도 ID 순이다.
        /// </summary>
        public static void Diff(IReadOnlyList<SnapshotEntry> baseline, IReadOnlyList<SnapshotEntry> current, List<SnapshotEntry> output)
        {
            output.Clear();
            int i = 0, j = 0;
            while (i < baseline.Count || j < current.Count)
            {
                if (j >= current.Count || (i < baseline.Count && baseline[i].Id < current[j].Id))
                {
                    output.Add(SnapshotEntry.Removed(baseline[i].Id));
                    i++;
                }
                else if (i >= baseline.Count || current[j].Id < baseline[i].Id)
                {
                    var e = current[j];
                    e.Fields = SnapshotFields.All;
                    output.Add(e);
                    j++;
                }
                else
                {
                    var changed = SnapshotEntry.Changed(baseline[i], current[j]);
                    if (changed != SnapshotFields.None)
                    {
                        var e = current[j];
                        e.Fields = changed;
                        output.Add(e);
                    }

                    i++;
                    j++;
                }
            }
        }

        /// <summary>
        /// baseline 에 변경분을 적용한 전체 목록을 <paramref name="output"/> 에 쓴다.
        /// <paramref name="delta"/> 는 정렬되어 있지 않아도 되며(조각 도착 순서), 이 함수가 정렬한다.
        /// </summary>
        public static void Apply(IReadOnlyList<SnapshotEntry> baseline, List<SnapshotEntry> delta, List<SnapshotEntry> output)
        {
            delta.Sort(ById);
            output.Clear();
            int i = 0, j = 0;
            while (i < baseline.Count || j < delta.Count)
            {
                if (j >= delta.Count || (i < baseline.Count && baseline[i].Id < delta[j].Id))
                {
                    output.Add(baseline[i]);
                    i++;
                    continue;
                }

                var d = delta[j];
                var hasBase = i < baseline.Count && baseline[i].Id == d.Id;
                if (!d.IsRemoved)
                {
                    var e = hasBase ? baseline[i] : default;
                    e.Id = d.Id;
                    e.Merge(d);
                    e.Fields = SnapshotFields.All;
                    output.Add(e);
                }

                if (hasBase)
                {
                    i++;
                }

                j++;
            }
        }
    }

    /// <summary>
    /// 최근 스냅샷(전체 상태, ID 순 정렬)을 틱별로 보관한다. 서버는 보낸 상태를, 클라이언트는 복원한 상태를 넣는다.
    /// 용량을 넘으면 가장 오래된 것부터 버린다.
    /// </summary>
    public sealed class SnapshotHistory
    {
        private readonly uint[] _ticks;
        private readonly List<SnapshotEntry>[] _states;
        private int _next;

        public SnapshotHistory(int capacity = 64)
        {
            _ticks = new uint[capacity];
            _states = new List<SnapshotEntry>[capacity];
            for (var i = 0; i < capacity; i++)
            {
                _states[i] = new List<SnapshotEntry>();
            }
        }

        public void Store(uint tick, IReadOnlyList<SnapshotEntry> sortedState)
        {
            if (tick == SnapshotMessage.NoBaseline)
            {
                return; // 0 은 "기준 없음" 표시로 쓰므로 기준점이 될 수 없다
            }

            var slot = _states[_next];
            slot.Clear();
            for (var i = 0; i < sortedState.Count; i++)
            {
                slot.Add(sortedState[i]);
            }

            _ticks[_next] = tick;
            _next = (_next + 1) % _ticks.Length;
        }

        public bool TryGet(uint tick, out IReadOnlyList<SnapshotEntry> state)
        {
            if (tick != SnapshotMessage.NoBaseline)
            {
                for (var i = 0; i < _ticks.Length; i++)
                {
                    if (_ticks[i] == tick)
                    {
                        state = _states[i];
                        return true;
                    }
                }
            }

            state = Array.Empty<SnapshotEntry>();
            return false;
        }

        public void Clear()
        {
            Array.Clear(_ticks, 0, _ticks.Length);
            foreach (var s in _states)
            {
                s.Clear();
            }
        }
    }
}
