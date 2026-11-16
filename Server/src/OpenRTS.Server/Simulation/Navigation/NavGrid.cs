using System.Numerics;
using OpenRTS.Shared;
using OpenRTS.Shared.Map;

namespace OpenRTS.Server.Simulation.Navigation;

/// <summary>
/// 벽 사각형으로 만든 길찾기 격자.
/// <list type="bullet">
/// <item>셀마다 "셀 중심에서 가장 가까운 벽(맵 경계 포함)까지 거리(clearance)"를 미리 구해 두고,
/// 반지름 r 유닛은 clearance ≥ r 인 셀만 지난다. 그래서 같은 통로도 큰 유닛은 못 지나간다.</item>
/// <item>8방향 A* (대각선은 양옆 직교 셀이 모두 열려 있을 때만 — 모서리 끼임 방지), 옥타일 휴리스틱.</item>
/// <item>찾은 셀 경로는 벽과의 정확한 선분 거리 검사로 줄인다(string pulling).</item>
/// <item>목표에 갈 수 없으면 도달 가능한 셀 중 목표에 가장 가까운 곳까지의 경로를 돌려준다.</item>
/// </list>
/// 참고 서버(LeagueSandbox)의 navgrid 는 미리 구운 클라이언트 파일을 읽었지만, 여기서는 맵 정의(벽 사각형)에서 직접 만든다.
/// </summary>
public sealed class NavGrid
{
    public const float DefaultCellSize = 0.5f;

    /// <summary>A* 한 번에 펼치는 최대 셀 수 (맵 전체 셀 수보다 크면 사실상 무제한).</summary>
    public const int MaxExpansions = 60_000;

    private static readonly (int Dx, int Dy)[] Directions =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    private readonly WallRect[] _walls;
    private readonly float[] _clearance;
    private readonly float[] _g;
    private readonly int[] _parent;
    private readonly int[] _visited; // 세대 번호 — 배열을 매번 지우지 않으려고
    private readonly int[] _closed;
    private readonly PriorityQueue<int, float> _open = new();
    private readonly List<int> _cells = new();
    private int _generation;

    public NavGrid(IReadOnlyList<WallRect> walls, float cellSize = DefaultCellSize)
    {
        _walls = walls.ToArray();
        CellSize = cellSize;
        Origin = new Vector2(-SimConstants.MapHalfSize);
        Width = (int)MathF.Ceiling(SimConstants.MapHalfSize * 2 / cellSize);
        Height = Width;

        var n = Width * Height;
        _clearance = new float[n];
        _g = new float[n];
        _parent = new int[n];
        _visited = new int[n];
        _closed = new int[n];
        for (var i = 0; i < n; i++)
        {
            _clearance[i] = Clearance(CellCenter(i));
        }
    }

    public int Width { get; }
    public int Height { get; }
    public float CellSize { get; }
    public Vector2 Origin { get; }
    public IReadOnlyList<WallRect> Walls => _walls;
    public bool HasWalls => _walls.Length > 0;

    /// <summary>마지막 FindPath 에서 펼친 셀 수 (성능 확인용).</summary>
    public int LastExpansions { get; private set; }

    public int CellCount => Width * Height;

    public int CellIndex(Vector2 p)
    {
        var x = Math.Clamp((int)((p.X - Origin.X) / CellSize), 0, Width - 1);
        var y = Math.Clamp((int)((p.Y - Origin.Y) / CellSize), 0, Height - 1);
        return y * Width + x;
    }

    public Vector2 CellCenter(int index) => Origin + new Vector2(index % Width + 0.5f, index / Width + 0.5f) * CellSize;

    /// <summary>
    /// 점에서 가장 가까운 벽까지의 거리. 벽 안이면 0.
    /// 맵 경계는 넣지 않는다 — 유닛 중심이 경계까지 갈 수 있으므로(ClampToMap) 경계는 벽이 아니다.
    /// </summary>
    public float Clearance(Vector2 p)
    {
        var best = float.MaxValue;
        foreach (var w in _walls)
        {
            if (w.Contains(p))
            {
                return 0;
            }

            best = MathF.Min(best, Vector2.Distance(p, w.ClosestPoint(p)));
        }

        return MathF.Max(0, best);
    }

    /// <summary>벽 겹침 판정용 별칭.</summary>
    public float WallDistance(Vector2 p) => Clearance(p);


    public bool IsCellPassable(int index, float radius) => _clearance[index] >= radius;

    /// <summary>반지름 <paramref name="radius"/> 원이 <paramref name="a"/> 에서 <paramref name="b"/> 로 벽에 닿지 않고 직선 이동할 수 있는지.</summary>
    public bool HasLineOfSight(Vector2 a, Vector2 b, float radius)
    {
        foreach (var w in _walls)
        {
            if (SegmentRectDistance(a, b, w) < radius)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="p"/> 에서 가장 가까운, 반지름 <paramref name="radius"/> 유닛이 설 수 있는 점.
    /// 이미 설 수 있으면 그대로 돌려준다.
    /// </summary>
    public Vector2 NearestStandable(Vector2 p, float radius)
    {
        if (Clearance(p) >= radius)
        {
            return p;
        }

        var cell = NearestPassableCell(CellIndex(p), radius);
        if (cell < 0)
        {
            return p;
        }

        // 셀 중심에서 원래 점 쪽으로 최대한 당긴다 (벽에 바짝 붙은 자연스러운 위치).
        var center = CellCenter(cell);
        var lo = 0f;
        var hi = 1f;
        for (var i = 0; i < 12; i++)
        {
            var mid = (lo + hi) / 2;
            if (Clearance(Vector2.Lerp(center, p, mid)) >= radius)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Vector2.Lerp(center, p, lo);
    }

    /// <summary>
    /// 경로를 찾는다. <paramref name="path"/> 에는 출발점을 뺀 경유점이 들어가고 마지막 점이 도착점이다.
    /// <paramref name="extraCost"/> 는 셀별 추가 비용(막힘 시 재경로에서 멈춰 있는 유닛이 차지한 셀을 피할 때)이다.
    /// </summary>
    /// <returns>목표(또는 목표에 가장 가까운 도달 가능 지점)까지 경로를 만들었으면 true. 출발 셀조차 없으면 false.</returns>
    public bool FindPath(Vector2 start, Vector2 goal, float radius, List<Vector2> path, float[]? extraCost = null)
    {
        path.Clear();
        goal = NearestStandable(ClampToMap(goal), radius);

        if (HasLineOfSight(start, goal, radius))
        {
            path.Add(goal);
            LastExpansions = 0;
            return true;
        }

        var startCell = NearestPassableCell(CellIndex(start), radius);
        var goalCell = NearestPassableCell(CellIndex(goal), radius);
        if (startCell < 0 || goalCell < 0)
        {
            return false;
        }

        var reachedCell = Search(startCell, goalCell, radius, extraCost);

        _cells.Clear();
        for (var c = reachedCell; c != startCell; c = _parent[c])
        {
            _cells.Add(c);
        }

        _cells.Reverse();
        var end = reachedCell == goalCell ? goal : CellCenter(reachedCell);
        Smooth(start, end, radius, path);
        return true;
    }

    /// <summary>A*. 목표에 닿지 못하면 휴리스틱이 가장 작았던(목표에 가장 가까운) 셀을 돌려준다.</summary>
    private int Search(int startCell, int goalCell, float radius, float[]? extraCost)
    {
        _generation++;
        _open.Clear();
        var goalX = goalCell % Width;
        var goalY = goalCell / Width;

        Visit(startCell, 0, startCell);
        _open.Enqueue(startCell, Heuristic(startCell, goalX, goalY));
        var best = startCell;
        var bestH = Heuristic(startCell, goalX, goalY);
        var expansions = 0;

        while (_open.TryDequeue(out var cell, out _))
        {
            if (_closed[cell] == _generation)
            {
                continue;
            }

            _closed[cell] = _generation;
            if (cell == goalCell)
            {
                best = cell;
                break;
            }

            var h = Heuristic(cell, goalX, goalY);
            if (h < bestH)
            {
                bestH = h;
                best = cell;
            }

            if (++expansions > MaxExpansions)
            {
                break;
            }

            var x = cell % Width;
            var y = cell / Width;
            foreach (var (dx, dy) in Directions)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Width || ny >= Height)
                {
                    continue;
                }

                var next = ny * Width + nx;
                if (_closed[next] == _generation || !IsCellPassable(next, radius))
                {
                    continue;
                }

                var diagonal = dx != 0 && dy != 0;
                if (diagonal && (!IsCellPassable(y * Width + nx, radius) || !IsCellPassable(ny * Width + x, radius)))
                {
                    continue; // 모서리 끼임 방지
                }

                var step = (diagonal ? 1.41421356f : 1f) * CellSize;
                if (extraCost != null)
                {
                    step += extraCost[next];
                }

                var g = _g[cell] + step;
                if (_visited[next] == _generation && g >= _g[next])
                {
                    continue;
                }

                Visit(next, g, cell);
                _open.Enqueue(next, g + Heuristic(next, goalX, goalY));
            }
        }

        LastExpansions = expansions;
        return best;
    }

    private void Visit(int cell, float g, int parent)
    {
        _visited[cell] = _generation;
        _g[cell] = g;
        _parent[cell] = parent;
    }

    private float Heuristic(int cell, int goalX, int goalY)
    {
        var dx = Math.Abs(cell % Width - goalX);
        var dy = Math.Abs(cell / Width - goalY);
        return (Math.Max(dx, dy) + 0.41421356f * Math.Min(dx, dy)) * CellSize;
    }

    /// <summary>셀 경로를 직선으로 이어도 벽에 닿지 않는 만큼 건너뛴다.</summary>
    private void Smooth(Vector2 start, Vector2 end, float radius, List<Vector2> path)
    {
        // 후보: 셀 중심들 + 최종 도착점
        var anchor = start;
        var i = 0;
        var count = _cells.Count; // 마지막 셀은 end 로 대체
        while (true)
        {
            if (HasLineOfSight(anchor, end, radius))
            {
                path.Add(end);
                return;
            }

            // anchor 에서 보이는 가장 먼 셀까지 전진
            var farthest = i;
            for (var k = i; k < count - 1; k++)
            {
                if (HasLineOfSight(anchor, CellCenter(_cells[k]), radius))
                {
                    farthest = k;
                }
                else if (k > farthest + 8)
                {
                    break; // 한참 안 보이면 더 볼 필요 없음
                }
            }

            if (farthest >= count - 1)
            {
                path.Add(end);
                return;
            }

            anchor = CellCenter(_cells[farthest]);
            path.Add(anchor);
            i = farthest + 1;
            if (i >= count)
            {
                path.Add(end);
                return;
            }
        }
    }

    private int NearestPassableCell(int cell, float radius)
    {
        if (IsCellPassable(cell, radius))
        {
            return cell;
        }

        var cx = cell % Width;
        var cy = cell / Width;
        var maxRing = Math.Max(Width, Height);
        for (var ring = 1; ring < maxRing; ring++)
        {
            var best = -1;
            var bestDist = float.MaxValue;
            for (var dy = -ring; dy <= ring; dy++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Abs(dx) != ring && Math.Abs(dy) != ring)
                    {
                        continue; // 고리 테두리만
                    }

                    var x = cx + dx;
                    var y = cy + dy;
                    if (x < 0 || y < 0 || x >= Width || y >= Height)
                    {
                        continue;
                    }

                    var index = y * Width + x;
                    var d = dx * dx + dy * dy;
                    if (d < bestDist && IsCellPassable(index, radius))
                    {
                        best = index;
                        bestDist = d;
                    }
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return -1;
    }

    /// <summary>선분과 축 정렬 사각형 사이 최단 거리 (겹치면 0).</summary>
    public static float SegmentRectDistance(Vector2 a, Vector2 b, WallRect r)
    {
        if (SegmentIntersectsRect(a, b, r))
        {
            return 0;
        }

        // 겹치지 않으면 최단 거리는 "선분 끝점 ↔ 사각형" 또는 "사각형 꼭짓점 ↔ 선분" 중 하나.
        var best = MathF.Min(Vector2.Distance(a, r.ClosestPoint(a)), Vector2.Distance(b, r.ClosestPoint(b)));
        best = MathF.Min(best, PointSegmentDistance(new Vector2(r.Min.X, r.Min.Y), a, b));
        best = MathF.Min(best, PointSegmentDistance(new Vector2(r.Max.X, r.Min.Y), a, b));
        best = MathF.Min(best, PointSegmentDistance(new Vector2(r.Max.X, r.Max.Y), a, b));
        best = MathF.Min(best, PointSegmentDistance(new Vector2(r.Min.X, r.Max.Y), a, b));
        return best;
    }

    public static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared();
        var t = lenSq > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + t * ab);
    }

    /// <summary>Liang–Barsky 클리핑으로 선분이 사각형과 겹치는지.</summary>
    private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, WallRect r)
    {
        var d = b - a;
        var t0 = 0f;
        var t1 = 1f;
        return Clip(-d.X, a.X - r.Min.X, ref t0, ref t1)
            && Clip(d.X, r.Max.X - a.X, ref t0, ref t1)
            && Clip(-d.Y, a.Y - r.Min.Y, ref t0, ref t1)
            && Clip(d.Y, r.Max.Y - a.Y, ref t0, ref t1);
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (MathF.Abs(p) < 1e-12f)
        {
            return q >= 0;
        }

        var t = q / p;
        if (p < 0)
        {
            if (t > t1)
            {
                return false;
            }

            if (t > t0)
            {
                t0 = t;
            }
        }
        else
        {
            if (t < t0)
            {
                return false;
            }

            if (t < t1)
            {
                t1 = t;
            }
        }

        return true;
    }

    private static Vector2 ClampToMap(Vector2 p) => Vector2.Clamp(p, new Vector2(-SimConstants.MapHalfSize), new Vector2(SimConstants.MapHalfSize));
}
