using PotDanmaku.Core;

namespace PotDanmaku.Tests;

public class GraphicsRecoveryTests
{
    [Theory]
    [InlineData(0x887A0005u, true)] [InlineData(0x887A0007u, true)] [InlineData(0x8899000Cu, true)]
    [InlineData(0x887A0001u, false)] [InlineData(0x80070057u, false)]
    public void OnlyTransientDeviceFailuresAreRetried(uint code, bool expected) =>
        Assert.Equal(expected, GraphicsRecovery.IsRecoverable(unchecked((int)code)));
    [Fact]
    public void RetriesBackOffAndAreBounded()
    {
        Assert.Equal(5, GraphicsRecovery.MaxAttempts);
        Assert.Equal([500, 1000, 2000, 4000, 8000], Enumerable.Range(1, 5).Select(GraphicsRecovery.RetryDelayMs));
        Assert.Equal(8000, GraphicsRecovery.RetryDelayMs(100));
    }
}
