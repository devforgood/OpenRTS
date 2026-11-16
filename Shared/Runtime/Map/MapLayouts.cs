using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenRTS.Shared.Map
{
    /// <summary>맵 종류. 서버와 클라이언트가 같은 함수로 벽을 만들므로 맵 데이터를 따로 보내지 않는다.</summary>
    public enum MapKind : byte
    {
        /// <summary>벽 없음.</summary>
        Open = 0,

        /// <summary>맵 가운데를 세로로 막는 벽에 좁은 통로 하나 (군집 통과 확인용).</summary>
        Gate = 1,

        /// <summary>사각 장애물 여러 개 (경로 탐색 확인용).</summary>
        Blocks = 2,
    }

    /// <summary>축 정렬 사각형 벽.</summary>
    public readonly struct WallRect
    {
        public readonly Vector2 Min;
        public readonly Vector2 Max;

        public WallRect(Vector2 min, Vector2 max)
        {
            Min = Vector2.Min(min, max);
            Max = Vector2.Max(min, max);
        }

        public Vector2 Center => (Min + Max) * 0.5f;
        public Vector2 Size => Max - Min;

        public bool Contains(Vector2 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y;

        /// <summary>사각형 위(경계 포함)에서 <paramref name="p"/> 에 가장 가까운 점.</summary>
        public Vector2 ClosestPoint(Vector2 p) => Vector2.Clamp(p, Min, Max);

        /// <summary>반시계 방향 꼭짓점 (RVO2 장애물 규약).</summary>
        public Vector2[] CounterClockwise() => new[]
        {
            new Vector2(Min.X, Min.Y),
            new Vector2(Max.X, Min.Y),
            new Vector2(Max.X, Max.Y),
            new Vector2(Min.X, Max.Y),
        };
    }

    public static class MapLayouts
    {
        /// <summary>Gate 맵 통로 폭.</summary>
        public const float GateGapWidth = 4f;

        /// <summary>Gate 맵 벽 두께.</summary>
        public const float GateWallThickness = 2f;

        public static IReadOnlyList<WallRect> Create(MapKind kind)
        {
            var h = SimConstants.MapHalfSize;
            switch (kind)
            {
                case MapKind.Gate:
                    var t = GateWallThickness / 2;
                    var g = GateGapWidth / 2;
                    return new[]
                    {
                        new WallRect(new Vector2(-t, -h), new Vector2(t, -g)),
                        new WallRect(new Vector2(-t, g), new Vector2(t, h)),
                    };

                case MapKind.Blocks:
                    // 서로 닿지 않는 사각형만 둔다 (RVO2 장애물은 닫힌 다각형 하나씩으로 다룬다).
                    return new[]
                    {
                        Rect(-20, -6, -16, 30),
                        Rect(-10, -30, 10, -26),
                        Rect(4, -12, 16, 0),
                        Rect(16, 8, 20, 36),
                        Rect(-4, 10, 8, 14),
                        Rect(-36, -24, -28, -16),
                        Rect(28, -30, 36, -18),
                        Rect(-38, 20, -30, 28),
                    };

                default:
                    return Array.Empty<WallRect>();
            }
        }

        private static WallRect Rect(float x0, float y0, float x1, float y1) => new WallRect(new Vector2(x0, y0), new Vector2(x1, y1));
    }
}
