using System.Numerics;
using OpenRTS.Shared;

namespace OpenRTS.Server.Simulation;

/// <summary>
/// 맵 전체를 덮는 균일 격자. 매 틱 위치 목록으로 다시 만들고(계수 정렬), 반경 안의 인덱스를 찾는다.
/// </summary>
public sealed class SpatialGrid
{
    private float _cellSize = 1f;
    private float _origin;
    private int _cols;
    private int[] _cellStart = Array.Empty<int>();
    private int[] _cellCount = Array.Empty<int>();
    private int[] _items = Array.Empty<int>();
    private int[] _itemCell = Array.Empty<int>();
    private IReadOnlyList<Vector2> _positions = Array.Empty<Vector2>();

    public void Build(IReadOnlyList<Vector2> positions, float cellSize)
    {
        _positions = positions;
        _cellSize = Math.Max(cellSize, 0.1f);
        _origin = -SimConstants.MapHalfSize;
        _cols = Math.Max(1, (int)MathF.Ceiling(SimConstants.MapHalfSize * 2 / _cellSize) + 1);

        var cells = _cols * _cols;
        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cellCount = new int[cells];
        }
        else
        {
            Array.Clear(_cellCount, 0, cells);
        }

        if (_items.Length < positions.Count)
        {
            _items = new int[positions.Count * 2];
            _itemCell = new int[positions.Count * 2];
        }

        for (var i = 0; i < positions.Count; i++)
        {
            var c = CellIndex(positions[i]);
            _itemCell[i] = c;
            _cellCount[c]++;
        }

        _cellStart[0] = 0;
        for (var c = 0; c < cells; c++)
        {
            _cellStart[c + 1] = _cellStart[c] + _cellCount[c];
        }

        Array.Clear(_cellCount, 0, cells); // 채우기용 커서로 재사용
        for (var i = 0; i < positions.Count; i++)
        {
            var c = _itemCell[i];
            _items[_cellStart[c] + _cellCount[c]++] = i;
        }
    }

    /// <summary><paramref name="center"/> 에서 <paramref name="range"/> 안에 있는 항목 인덱스를 <paramref name="results"/> 에 추가한다.</summary>
    public void Query(Vector2 center, float range, List<int> results)
    {
        var rangeSq = range * range;
        var minX = CellCoord(center.X - range);
        var maxX = CellCoord(center.X + range);
        var minY = CellCoord(center.Y - range);
        var maxY = CellCoord(center.Y + range);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var c = y * _cols + x;
                for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    var i = _items[k];
                    if (Vector2.DistanceSquared(_positions[i], center) <= rangeSq)
                    {
                        results.Add(i);
                    }
                }
            }
        }
    }

    private int CellCoord(float v) => Math.Clamp((int)((v - _origin) / _cellSize), 0, _cols - 1);

    private int CellIndex(Vector2 p) => CellCoord(p.Y) * _cols + CellCoord(p.X);
}
