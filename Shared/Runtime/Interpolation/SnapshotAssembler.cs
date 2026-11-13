using System.Collections.Generic;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Shared.Interpolation
{
    /// <summary>
    /// 여러 조각으로 나뉜 스냅샷을 모은다. 한 틱의 조각이 모두 모여야 완성으로 본다.
    /// 더 새로운 틱의 조각이 오면 덜 모인 이전 틱은 버린다 (조각 하나라도 빠지면 그 틱 전체가 손실).
    /// </summary>
    public sealed class SnapshotAssembler
    {
        private readonly List<UnitState> _units = new List<UnitState>();
        private bool[] _received = new bool[0];
        private uint _tick;
        private bool _active;
        private int _receivedCount;
        private int _partCount;
        private uint _lastCompleted;
        private bool _hasCompleted;

        /// <summary>조각이 다 모이지 않은 채 버려진 틱 수.</summary>
        public int IncompleteDropped { get; private set; }

        /// <summary>
        /// 조각을 추가한다. 이 조각으로 틱이 완성되면 true 와 함께 전체 유닛 목록을 돌려준다
        /// (반환한 리스트는 다음 호출 때 재사용된다).
        /// </summary>
        public bool Add(uint tick, int part, int partCount, List<UnitState> partUnits, out uint completedTick, out IReadOnlyList<UnitState> units)
        {
            completedTick = 0;
            units = _units; // 반환값이 false 면 내용은 의미 없음

            if (_hasCompleted && tick <= _lastCompleted)
            {
                return false; // 이미 완성했거나 더 오래된 틱
            }

            if (!_active || tick > _tick)
            {
                if (_active)
                {
                    IncompleteDropped++;
                }

                Begin(tick, partCount);
            }
            else if (tick < _tick || partCount != _partCount)
            {
                return false; // 이미 넘어간 틱의 늦은 조각, 또는 형식 불일치
            }

            if (_received[part])
            {
                return false;
            }

            _received[part] = true;
            _receivedCount++;
            _units.AddRange(partUnits);

            if (_receivedCount < _partCount)
            {
                return false;
            }

            _active = false;
            _hasCompleted = true;
            _lastCompleted = tick;
            completedTick = tick;
            units = _units;
            return true;
        }

        private void Begin(uint tick, int partCount)
        {
            _tick = tick;
            _partCount = partCount;
            _receivedCount = 0;
            _active = true;
            _units.Clear();
            if (_received.Length < partCount)
            {
                _received = new bool[partCount];
            }

            for (var i = 0; i < partCount; i++)
            {
                _received[i] = false;
            }
        }
    }
}
