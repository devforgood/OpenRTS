using System.Collections.Generic;
using OpenRTS.Shared.Protocol;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Shared.Interpolation
{
    /// <summary>
    /// 클라이언트 쪽 스냅샷 수신: 조각 조립 → (델타면) 기준 스냅샷에 적용 → 복원한 전체 상태 보관.
    /// 완성된 틱은 서버에 <see cref="SnapshotAck"/> 로 알려야 다음 델타의 기준이 된다.
    /// Unity 클라이언트와 서버 통합 테스트가 같은 코드를 쓴다.
    /// </summary>
    public sealed class SnapshotReceiver
    {
        private readonly SnapshotAssembler _assembler = new SnapshotAssembler();
        private readonly SnapshotHistory _history;
        private readonly List<SnapshotEntry> _part = new List<SnapshotEntry>();
        private readonly List<SnapshotEntry> _full = new List<SnapshotEntry>();
        private readonly List<UnitState> _states = new List<UnitState>();

        public SnapshotReceiver(int historySize = 64)
        {
            _history = new SnapshotHistory(historySize);
        }

        /// <summary>조각이 다 모이지 않아 버린 스냅샷 수.</summary>
        public int IncompleteDropped => _assembler.IncompleteDropped;

        /// <summary>기준 스냅샷이 없어 복원하지 못한 델타 수.</summary>
        public int MissingBaseline { get; private set; }

        public int FullReceived { get; private set; }
        public int DeltaReceived { get; private set; }

        /// <summary>
        /// 스냅샷 조각 하나를 처리한다 (메시지 타입 바이트를 읽은 뒤 호출).
        /// 이 조각으로 한 틱이 완성·복원되면 true 와 전체 상태를 돌려준다 (반환 리스트는 다음 호출 때 재사용).
        /// </summary>
        public bool Receive(ref PacketReader r, out uint tick, out IReadOnlyList<UnitState> states)
        {
            states = _states;
            var partTick = SnapshotMessage.ReadPart(ref r, _part, out var baseTick, out var part, out var partCount);
            if (!_assembler.Add(partTick, baseTick, part, partCount, _part, out tick, out var entries))
            {
                return false;
            }

            if (baseTick == SnapshotMessage.NoBaseline)
            {
                _full.Clear();
                _full.AddRange(entries);
                _full.Sort(SnapshotDelta.ById);
                FullReceived++;
            }
            else
            {
                if (!_history.TryGet(baseTick, out var baseline))
                {
                    MissingBaseline++;
                    return false;
                }

                SnapshotDelta.Apply(baseline, entries, _full);
                DeltaReceived++;
            }

            _history.Store(tick, _full);
            _states.Clear();
            foreach (var e in _full)
            {
                _states.Add(e.ToState());
            }

            return true;
        }

        public void Reset()
        {
            _history.Clear();
        }
    }
}
