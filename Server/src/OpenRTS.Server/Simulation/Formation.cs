using System.Numerics;

namespace OpenRTS.Server.Simulation;

/// <summary>
/// 여러 유닛에 한 지점을 찍었을 때 각자의 도착 지점을 정한다.
/// 모두 같은 점으로 보내면 도착 지점에서 서로 밀치다 막힘 판정으로 멈추므로(군집 이동의 대표적인 문제),
/// 목표를 중심으로 이동 방향을 향한 격자 대형을 만들고 유닛이 서로 엇갈리지 않게 칸을 배정한다.
/// </summary>
public static class Formation
{
    /// <returns><paramref name="positions"/> 와 같은 순서의 도착 지점.</returns>
    public static Vector2[] Assign(IReadOnlyList<Vector2> positions, Vector2 target, float spacing)
    {
        var n = positions.Count;
        var result = new Vector2[n];
        if (n == 0)
        {
            return result;
        }

        var centroid = Vector2.Zero;
        foreach (var p in positions)
        {
            centroid += p;
        }

        centroid /= n;
        var forward = target - centroid;
        forward = forward.LengthSquared() > 1e-6f ? Vector2.Normalize(forward) : Vector2.UnitX;
        var right = new Vector2(forward.Y, -forward.X);

        var cols = (int)MathF.Ceiling(MathF.Sqrt(n));
        var rows = (n + cols - 1) / cols;

        // 앞에 있는 유닛이 앞줄로: 진행 방향 투영이 큰 순으로 줄을 나누고, 줄 안에서는 좌우 순서대로 칸에 넣는다.
        var order = Enumerable.Range(0, n).OrderByDescending(i => Vector2.Dot(positions[i], forward)).ToArray();
        for (var row = 0; row < rows; row++)
        {
            var start = row * cols;
            var count = Math.Min(cols, n - start);
            var rowUnits = order.Skip(start).Take(count).OrderBy(i => Vector2.Dot(positions[i], right)).ToArray();
            var rowOffset = ((rows - 1) / 2f - row) * spacing; // 0번 줄이 맨 앞
            for (var k = 0; k < count; k++)
            {
                var colOffset = (k - (count - 1) / 2f) * spacing;
                result[rowUnits[k]] = target + forward * rowOffset + right * colOffset;
            }
        }

        return result;
    }
}
