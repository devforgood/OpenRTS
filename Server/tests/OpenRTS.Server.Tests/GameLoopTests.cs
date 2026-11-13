using OpenRTS.Server;

namespace OpenRTS.Server.Tests;

public class GameLoopTests
{
    [Fact]
    public void Advance_RunsOneTickPerTickLength()
    {
        var loop = new GameLoop(tickRate: 10);
        var ticks = new List<long>();

        loop.Advance(TimeSpan.FromMilliseconds(250), ticks.Add);

        Assert.Equal(new long[] { 0, 1 }, ticks);
        Assert.Equal(2, loop.Tick);
    }

    [Fact]
    public void Advance_CarriesRemainderToNextCall()
    {
        var loop = new GameLoop(tickRate: 10);

        Assert.Equal(0, loop.Advance(TimeSpan.FromMilliseconds(60), _ => { }));
        Assert.Equal(1, loop.Advance(TimeSpan.FromMilliseconds(60), _ => { }));
    }

    [Fact]
    public void Advance_CapsTicksAndDropsBacklog()
    {
        var loop = new GameLoop(tickRate: 10, maxTicksPerFrame: 3);

        Assert.Equal(3, loop.Advance(TimeSpan.FromSeconds(10), _ => { }));
        // 밀린 시간은 버려졌으므로 다음 호출에서 따라잡지 않는다.
        Assert.Equal(0, loop.Advance(TimeSpan.Zero, _ => { }));
    }
}
