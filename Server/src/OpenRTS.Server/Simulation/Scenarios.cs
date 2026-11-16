using System.Numerics;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;
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

            case BotScenario.Pass:
                // 한 무리가 왼쪽 → 오른쪽. Gate 맵이면 가운데 좁은 통로를 지나야 한다.
                foreach (var offset in Block(n, spacing))
                {
                    yield return (new Vector2(-30, 0) + offset, new Vector2(30, 0) + offset);
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
    /// Battle: 봇을 반씩 1팀(왼쪽 x=-30)·2팀(오른쪽 x=+30)에 격자로 놓는다.
    /// 상대 쪽 앞줄 절반은 근접, 뒷줄 절반은 원거리, 20명당 영웅 하나(앞줄 가운데).
    /// </summary>
    public static IEnumerable<(Vector2 Position, byte Team, UnitKind Kind)> BattleLayout(SimSettings s)
    {
        var spacing = s.UnitRadius * 2f * 1.5f;
        var perTeam = new[] { s.BotCount / 2, s.BotCount - s.BotCount / 2 };
        for (var t = 0; t < 2; t++)
        {
            var team = (byte)(t + 1);
            var facing = t == 0 ? 1f : -1f; // 상대 쪽 방향 (x)
            var center = new Vector2(-30f * facing, 0);
            var offsets = Block(perTeam[t], spacing).OrderByDescending(o => o.X * facing).ToList();

            // 영웅: 20명당 하나 (6명 이상일 때 최소 하나). 앞줄 가운데 쪽에 둔다.
            var heroes = offsets.Count >= 6 ? Math.Max(1, offsets.Count / 20) : 0;
            var heroSlots = Enumerable.Range(0, Math.Min(offsets.Count, (int)MathF.Ceiling(MathF.Sqrt(offsets.Count))))
                .OrderBy(i => MathF.Abs(offsets[i].Y)).Take(heroes).ToHashSet();
            for (var i = 0; i < offsets.Count; i++)
            {
                var kind = heroSlots.Contains(i) ? UnitKind.Hero
                    : i < (offsets.Count + 1) / 2 ? UnitKind.Melee : UnitKind.Ranged;
                yield return (center + offsets[i], team, kind);
            }
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
