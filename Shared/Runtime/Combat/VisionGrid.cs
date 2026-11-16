using System;
using System.Collections.Generic;
using System.Numerics;
using OpenRTS.Shared.Map;

namespace OpenRTS.Shared.Combat
{
    /// <summary>
    /// 한 팀의 시야. 맵을 1×1 칸으로 나누고, 팀 유닛마다 시야 반지름 안에 중심이 든 칸을 "보임"으로 표시한다.
    /// 벽을 넘겨 주면 유닛에서 칸 중심까지의 직선이 벽에 걸리는 칸은 가려진다 (벽 너머는 안 보임).
    ///
    /// 서버(전송 필터)와 클라이언트(Fog of War 표시)가 같은 코드로 계산하므로, 클라이언트는 아군 위치만으로
    /// 서버와 같은 시야를 그릴 수 있고 시야 데이터를 따로 받지 않는다.
    /// 유닛이 보이는지는 그 유닛 중심이 든 칸으로 판정한다.
    /// </summary>
    public sealed class VisionGrid
    {
        public const float CellSize = 1f;

        private readonly int[] _stamp;
        private readonly List<WallRect> _nearWalls = new List<WallRect>();
        private int _generation = 1;

        public VisionGrid()
        {
            Size = (int)Math.Ceiling(SimConstants.MapHalfSize * 2 / CellSize);
            _stamp = new int[Size * Size];
        }

        /// <summary>한 변의 칸 수.</summary>
        public int Size { get; }

        /// <summary>모든 칸을 가린다 (배열을 지우지 않고 세대 번호만 올린다).</summary>
        public void Clear()
        {
            _generation++;
            if (_generation == int.MaxValue)
            {
                Array.Clear(_stamp, 0, _stamp.Length);
                _generation = 1;
            }
        }

        /// <summary><paramref name="center"/> 에서 <paramref name="radius"/> 안에 중심이 든 칸을 보이게 한다 (가림 없음).</summary>
        public void Reveal(Vector2 center, float radius) => Reveal(center, radius, Array.Empty<WallRect>());

        /// <summary>
        /// <paramref name="center"/> 에서 <paramref name="radius"/> 안에 중심이 들고, 그 중심까지 직선이
        /// <paramref name="walls"/> 에 걸리지 않는 칸을 보이게 한다. 시야 원에 걸치는 벽만 검사하므로
        /// 벽에서 먼 유닛은 가림 없는 경우와 비용이 같다.
        /// </summary>
        public void Reveal(Vector2 center, float radius, IReadOnlyList<WallRect> walls)
        {
            _nearWalls.Clear();
            for (var i = 0; i < walls.Count; i++)
            {
                if (walls[i].OverlapsBox(center, radius))
                {
                    _nearWalls.Add(walls[i]);
                }
            }

            var origin = -SimConstants.MapHalfSize;
            var x0 = Math.Max(0, (int)Math.Floor((center.X - radius - origin) / CellSize));
            var x1 = Math.Min(Size - 1, (int)Math.Floor((center.X + radius - origin) / CellSize));
            var y0 = Math.Max(0, (int)Math.Floor((center.Y - radius - origin) / CellSize));
            var y1 = Math.Min(Size - 1, (int)Math.Floor((center.Y + radius - origin) / CellSize));
            var r2 = radius * radius;
            for (var y = y0; y <= y1; y++)
            {
                var dy = origin + (y + 0.5f) * CellSize - center.Y;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = origin + (x + 0.5f) * CellSize - center.X;
                    if (dx * dx + dy * dy <= r2 && !Blocked(center, new Vector2(center.X + dx, center.Y + dy)))
                    {
                        _stamp[y * Size + x] = _generation;
                    }
                }
            }
        }

        private bool Blocked(Vector2 from, Vector2 to)
        {
            for (var i = 0; i < _nearWalls.Count; i++)
            {
                if (_nearWalls[i].IntersectsSegment(from, to))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsCellVisible(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size && _stamp[y * Size + x] == _generation;

        public bool IsVisible(Vector2 p)
        {
            var origin = -SimConstants.MapHalfSize;
            var x = (int)Math.Floor((p.X - origin) / CellSize);
            var y = (int)Math.Floor((p.Y - origin) / CellSize);
            x = Math.Max(0, Math.Min(Size - 1, x));
            y = Math.Max(0, Math.Min(Size - 1, y));
            return _stamp[y * Size + x] == _generation;
        }
    }
}
