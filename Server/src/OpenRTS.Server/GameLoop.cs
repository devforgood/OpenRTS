using System.Diagnostics;

namespace OpenRTS.Server;

/// <summary>
/// 고정 틱 게임 루프. 경과 시간을 누적해 틱 길이만큼씩 시뮬레이션을 진행한다.
/// 서버가 느려져도 한 번에 처리하는 틱 수를 제한해 무한히 밀리지 않게 한다.
/// </summary>
public sealed class GameLoop
{
    private readonly TimeSpan _tickLength;
    private readonly int _maxTicksPerFrame;
    private TimeSpan _accumulator;

    public GameLoop(int tickRate, int maxTicksPerFrame = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTicksPerFrame);
        _tickLength = TimeSpan.FromSeconds(1.0 / tickRate);
        _maxTicksPerFrame = maxTicksPerFrame;
    }

    /// <summary>지금까지 진행한 틱 수.</summary>
    public long Tick { get; private set; }

    public TimeSpan TickLength => _tickLength;

    /// <summary>
    /// 경과 시간을 반영하고, 처리해야 할 틱마다 <paramref name="onTick"/> 을 호출한다.
    /// </summary>
    /// <returns>이번 호출에서 처리한 틱 수.</returns>
    public int Advance(TimeSpan elapsed, Action<long> onTick)
    {
        _accumulator += elapsed;
        var processed = 0;
        while (_accumulator >= _tickLength && processed < _maxTicksPerFrame)
        {
            _accumulator -= _tickLength;
            onTick(Tick);
            Tick++;
            processed++;
        }

        // 너무 밀렸으면 남은 시간을 버린다 (spiral of death 방지).
        if (processed == _maxTicksPerFrame && _accumulator >= _tickLength)
        {
            _accumulator = TimeSpan.Zero;
        }

        return processed;
    }

    /// <summary>취소될 때까지 실시간으로 루프를 돈다.</summary>
    public async Task RunAsync(Action<long> onTick, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        while (!ct.IsCancellationRequested)
        {
            var now = sw.Elapsed;
            Advance(now - last, onTick);
            last = now;

            try
            {
                await Task.Delay(1, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
