using System.Numerics;
using OpenRTS.Shared;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Simulation;

/// <summary>봇 시나리오별 시작 위치(A)와 왕복 목표(B).</summary>
public static class Scenarios
{
    private const float MaxRingRadius = SimConstants.MapHalfSize - 5f;

    public static IEnumerable<(Vector2 A, Vector2 B)> Layout(SimSettings s, Random random)
    {
        var n = (int)s.BotCount;
        var spacing = s.UnitRadius * 2f * 1.5f; // 처음엔 서로 겹치지 않게 반지름 합의 1.5배 간격

        switch (s.Scenario)
        {
            case BotScenario.Converge:
                foreach (var p in Rings(n, spacing))
                {
                    yield return (p, Vector2.Zero);
                }

                break;

            case BotScenario.CircleSwap:
                foreach (var p in Rings(n, spacing))
                {
                    yield return (p, -p);
                }

                break;

            case BotScenario.Crossing:
                var half = n / 2;
                // 왼쪽 무리 → 오른쪽, 아래쪽 무리 → 위쪽. 교차점은 맵 중앙.
                foreach (var offset in Block(half, spacing))
                {
                    yield return (new Vector2(-30, 0) + offset, new Vector2(30, 0) + offset);
                }

                foreach (var offset in Block(n - half, spacing))
                {
                    yield return (new Vector2(0, -30) + offset, new Vector2(0, 30) + offset);
                }

                break;

            default: // Wander
                foreach (var p in Block(n, spacing * 2))
                {
                    var jitter = new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * spacing;
                    yield return (p + jitter, p);
                }

                break;
        }
    }

    /// <summary>
    /// 맵 중앙을 둘러싼 동심원에 <paramref name="n"/> 개를 고르게 배치한다.
    /// 한 원에 다 들어가면 원 하나, 아니면 바깥 원부터 안쪽으로 채운다.
    /// </summary>
    public static IEnumerable<Vector2> Rings(int n, float spacing)
    {
        var radius = Math.Clamp(n * spacing / (2 * MathF.PI), 8f, MaxRingRadius);
        var remaining = n;
        while (remaining > 0 && radius > spacing)
        {
            var capacity = Math.Max(1, (int)(2 * MathF.PI * radius / spacing));
            var count = Math.Min(capacity, remaining);
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * MathF.PI * i / count;
                yield return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            }

            remaining -= count;
            radius -= spacing;
        }

        // 원에 다 못 넣으면 중앙에 겹쳐 둔다 (ORCA 가 밀어낸다).
        for (var i = 0; i < remaining; i++)
        {
            yield return Vector2.Zero;
        }
    }

    /// <summary>원점 중심 정사각 격자 배치의 오프셋.</summary>
    public static IEnumerable<Vector2> Block(int n, float spacing)
    {
        if (n <= 0)
        {
            yield break;
        }

        var cols = (int)MathF.Ceiling(MathF.Sqrt(n));
        var size = (cols - 1) * spacing;
        for (var i = 0; i < n; i++)
        {
            var x = i % cols;
            var y = i / cols;
            yield return new Vector2(x * spacing - size / 2, y * spacing - size / 2);
        }
    }
}
