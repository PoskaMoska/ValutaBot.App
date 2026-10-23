using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines;

public class TimeframeNormalizerTests
{
    [Theory]
    [InlineData("5s", "s5", 5, true)]
    [InlineData("S5", "s5", 5, true)]
    [InlineData("s5", "s5", 5, true)]
    [InlineData("5с", "s5", 5, true)]
    [InlineData("10s", "s10", 10, true)]
    [InlineData("S10", "s10", 10, true)]
    [InlineData("s10", "s10", 10, true)]
    [InlineData("10с", "s10", 10, true)]
    [InlineData("15s", "s15", 15, true)]
    [InlineData("S15", "s15", 15, true)]
    [InlineData("s15", "s15", 15, true)]
    [InlineData("15с", "s15", 15, true)]
    [InlineData("30s", "s30", 30, true)]
    [InlineData("S30", "s30", 30, true)]
    [InlineData("s30", "s30", 30, true)]
    [InlineData("30с", "s30", 30, true)]
    [InlineData("1m", "m1", 60, false)]
    [InlineData("M1", "m1", 60, false)]
    [InlineData("m1", "m1", 60, false)]
    [InlineData("1min", "m1", 60, false)]
    [InlineData("1мин", "m1", 60, false)]
    [InlineData("1м", "m1", 60, false)]
    [InlineData("5m", "m5", 300, false)]
    [InlineData("m5", "m5", 300, false)]
    [InlineData("1h", "h1", 3600, false)]
    [InlineData("h1", "h1", 3600, false)]
    [InlineData("1d", "d1", 86400, false)]
    [InlineData("d1", "d1", 86400, false)]
    public void Normalize_HandlesAllVariationsAccurately(string input, string expectedNorm, int expectedSec, bool expectedSub)
    {
        string norm = TimeframeNormalizer.Normalize(input);
        int sec = TimeframeNormalizer.ToSeconds(input);
        bool isSub = TimeframeNormalizer.IsSubminute(input);

        Assert.Equal(expectedNorm, norm);
        Assert.Equal(expectedSec, sec);
        Assert.Equal(expectedSub, isSub);
    }
}
