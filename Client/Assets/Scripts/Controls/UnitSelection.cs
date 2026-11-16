using System.Collections.Generic;

namespace OpenRTS.Client.Controls
{
    /// <summary>선택한 내 유닛과 부대 지정(Ctrl+숫자). 사라진 유닛은 <see cref="Prune"/> 에서 빠진다.</summary>
    public sealed class UnitSelection
    {
        private readonly HashSet<int> _selected = new HashSet<int>();
        private readonly List<int> _ordered = new List<int>();
        private readonly Dictionary<int, List<int>> _groups = new Dictionary<int, List<int>>();
        private bool _orderDirty;

        public int Count => _selected.Count;

        public bool Contains(int id) => _selected.Contains(id);

        /// <summary>선택한 유닛 ID (오름차순).</summary>
        public IReadOnlyList<int> Ids
        {
            get
            {
                if (_orderDirty)
                {
                    _ordered.Clear();
                    _ordered.AddRange(_selected);
                    _ordered.Sort();
                    _orderDirty = false;
                }

                return _ordered;
            }
        }

        public void Set(IEnumerable<int> ids, bool additive)
        {
            if (!additive)
            {
                _selected.Clear();
            }

            foreach (var id in ids)
            {
                _selected.Add(id);
            }

            _orderDirty = true;
        }

        public void Toggle(int id)
        {
            if (!_selected.Remove(id))
            {
                _selected.Add(id);
            }

            _orderDirty = true;
        }

        public void Clear()
        {
            _selected.Clear();
            _orderDirty = true;
        }

        public void SaveGroup(int number)
        {
            _groups[number] = new List<int>(Ids);
        }

        /// <returns>부대가 있어서 선택했으면 true.</returns>
        public bool RecallGroup(int number, bool additive)
        {
            if (!_groups.TryGetValue(number, out var ids) || ids.Count == 0)
            {
                return false;
            }

            Set(ids, additive);
            return true;
        }

        /// <summary>더 이상 내 유닛이 아닌(사라진) ID 를 선택과 부대에서 뺀다.</summary>
        public void Prune(System.Func<int, bool> stillMine)
        {
            if (_selected.RemoveWhere(id => !stillMine(id)) > 0)
            {
                _orderDirty = true;
            }

            foreach (var group in _groups.Values)
            {
                group.RemoveAll(id => !stillMine(id));
            }
        }
    }
}
