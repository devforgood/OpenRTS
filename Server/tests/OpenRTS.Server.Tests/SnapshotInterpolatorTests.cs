using System.Numerics;
using OpenRTS.Shared.Interpolation;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class SnapshotInterpolatorTests
{
    // 틱 0.1초, 렌더 지연 0.2초로 계산을 단순하게 한다.
    private static SnapshotInterpolator Create() => new(tickDelta: 0.1f, interpolationDelay: 0.2f);

    private static UnitState[] At(float x) => new[] { new UnitState(1, new Vector2(x, 0)) };

    /// <summary>스냅샷을 실제처럼 틱 간격으로 도착시킨다.</summary>
    private static void Feed(SnapshotInterpolator interp, uint firstTick, params UnitState[][] snapshots)
    {
        for (var i = 0; i < snapshots.Length; i++)
        {
            if (i > 0)
            {
                interp.Advance(0.1f);
            }

            interp.AddSnapshot(firstTick + (uint)i, snapshots[i]);
        }
    }

    [Fact]
    public void Interpolates_BetweenSnapshots_AtDelayedTime()
    {
        var interp = Create();
        Feed(interp, 10, At(0), At(10), At(20)); // t = 1.0, 1.1, 1.2 → 시계 1.2

        interp.Advance(0.05f);                     // 시계 1.25 → 렌더 1.05
        var output = new Dictionary<int, Vector2>();
        interp.Sample(output);

        Assert.Equal(1.05, interp.RenderTime, 6);
        Assert.Equal(5f, output[1].X, 3);
        Assert.False(interp.IsExtrapolating);
    }

    [Fact]
    public void BurstArrival_DoesNotJumpClock()
    {
        // 스냅샷이 한꺼번에 몰려 와도 시계는 서서히만 움직여 화면이 튀지 않는다.
        var interp = Create();
        interp.AddSnapshot(10, At(0));
        interp.AddSnapshot(11, At(10));
        interp.AddSnapshot(12, At(20));

        Assert.InRange(interp.ServerClock, 1.0, 1.05);
    }

    [Fact]
    public void Extrapolates_WhenSnapshotsStop_UpToLimit()
    {
        var interp = Create();
        interp.AddSnapshot(10, At(0));
        interp.AddSnapshot(11, At(1)); // 속도 10/초

        interp.Advance(5f); // 스냅샷이 오래 끊김
        var output = new Dictionary<int, Vector2>();
        interp.Sample(output);

        Assert.True(interp.IsExtrapolating);
        Assert.Equal(1 + 10 * interp.MaxExtrapolation, output[1].X, 3);
    }

    [Fact]
    public void AdaptiveDelay_GrowsWithGaps_AndShrinksBackSlowly()
    {
        var interp = Create(); // 최소 지연 0.2초
        uint tick = 0;
        interp.AddSnapshot(tick, At(0));

        // 정상: 0.1초마다 도착 → 목표 지연은 최소값
        for (var i = 0; i < 20; i++)
        {
            interp.Advance(0.1f);
            interp.AddSnapshot(++tick, At(tick));
        }

        Assert.Equal(0.2f, interp.TargetInterpolationDelay, 3);

        // 스냅샷 3개 연속 손실 → 0.4초 공백
        interp.Advance(0.4f);
        tick += 4;
        interp.AddSnapshot(tick, At(tick));
        interp.Advance(0.01f);
        Assert.InRange(interp.TargetInterpolationDelay, 0.45f, 0.5f);

        // 서서히 늘어난다 (튀지 않음)
        var before = interp.InterpolationDelay;
        interp.Advance(0.1f);
        Assert.InRange(interp.InterpolationDelay - before, 0.01f, 0.03f);

        // 공백이 GapWindow 밖으로 밀려나면 다시 최소값을 향해 천천히 줄어든다
        for (var i = 0; i < 60; i++)
        {
            interp.Advance(0.1f);
            interp.AddSnapshot(++tick, At(tick));
        }

        // 위에서 추가로 흘린 0.11초만큼의 시계 오차가 아직 조금 남아 있어 최소값보다 약간 클 수 있다.
        Assert.InRange(interp.TargetInterpolationDelay, 0.2f, 0.21f);
        Assert.True(interp.InterpolationDelay < 0.45f);
    }

    [Fact]
    public void FixedDelay_WhenAdaptiveDisabled()
    {
        var interp = Create();
        interp.AdaptiveDelay = false;
        interp.AddSnapshot(0, At(0));
        interp.Advance(1f);
        interp.AddSnapshot(10, At(10));
        interp.Advance(0.1f);

        Assert.Equal(0.2f, interp.InterpolationDelay);
    }

    [Fact]
    public void Drops_OutOfOrderSnapshots()
    {
        var interp = Create();
        interp.AddSnapshot(10, At(0));
        interp.AddSnapshot(12, At(2));
        interp.AddSnapshot(11, At(999));

        Assert.Equal(1, interp.DroppedOutOfOrder);
        Assert.Equal(12u, interp.LatestTick);
    }

    [Fact]
    public void NewUnit_AppearsAtItsPosition_RemovedUnit_Disappears()
    {
        var interp = Create();
        Feed(interp, 10,
            new[] { new UnitState(1, Vector2.Zero) },
            new[] { new UnitState(2, new Vector2(5, 5)) },
            new[] { new UnitState(2, new Vector2(5, 5)) });
        interp.Advance(0.05f); // 렌더 1.05: 10(유닛1)과 11(유닛2) 사이

        var output = new Dictionary<int, Vector2>();
        interp.Sample(output);

        Assert.False(output.ContainsKey(1));
        Assert.Equal(new Vector2(5, 5), output[2]);
    }

    [Fact]
    public void ClockFollowsSnapshots_WithSteadyArrival()
    {
        var interp = Create();
        for (uint tick = 0; tick < 100; tick++)
        {
            interp.AddSnapshot(tick, At(tick));
            interp.Advance(0.1f);
        }

        // 일정한 간격으로 받으면 시계는 최신 스냅샷 시각과 거의 같아야 한다.
        Assert.InRange(interp.ServerClock - 99 * 0.1, -0.01, 0.11);
        Assert.True(interp.BufferedCount <= 5);
    }
}
