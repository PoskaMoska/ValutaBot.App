using System;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class WalkForwardValidationEngineTests
{
    [Fact]
    public void RecordTradeOutcome_TriggersCooloffAtFiveConsecutiveLosses_NotThree()
    {
        var engine = new WalkForwardValidationEngine();
        string asset = $"WF_CONS_{Guid.NewGuid():N}";

        for (int i = 0; i < 4; i++)
        {
            engine.RecordTradeOutcome(asset, "s5", isWin: false);
        }
        Assert.False(engine.ValidateWalkForward(asset, "s5").IsCooloffActive);

        engine.RecordTradeOutcome(asset, "s5", isWin: false);
        var result = engine.ValidateWalkForward(asset, "s5");
        Assert.True(result.IsCooloffActive);
        Assert.True(result.CooloffUntil > DateTime.UtcNow.AddMinutes(8));
    }

    [Fact]
    public void RecordTradeOutcome_TriggersCooloffWhenRollingWinRateDropsBelow35Pct()
    {
        var engine = new WalkForwardValidationEngine();
        string asset = $"WF_ROLL_{Guid.NewGuid():N}";

        // Alternating 3 wins / 7 losses => 30% rolling WR without hitting 5 consecutive losses
        bool[] sequence = { false, false, true, false, false, true, false, false, true, false };
        for (int i = 0; i < sequence.Length - 1; i++)
        {
            engine.RecordTradeOutcome(asset, "s30", sequence[i]);
            Assert.False(engine.ValidateWalkForward(asset, "s30").IsCooloffActive);
        }

        engine.RecordTradeOutcome(asset, "s30", sequence[^1]);
        var result = engine.ValidateWalkForward(asset, "s30");
        Assert.True(result.IsCooloffActive);
        Assert.Equal(0.10, result.WeightMultiplier);
    }
}
