using System;
using System.Net;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests;

public class TradeVerificationIntegrityTests
{
    [Theory]
    [InlineData("BUY", 1.08500, 1.08500, false, 0.0)]     // Pocket Option ATM: Tie is NOT a win (WasWin = false)
    [InlineData("PUT", 1.08500, 1.08500, false, 0.0)]     // Pocket Option ATM: Tie is NOT a win (WasWin = false)
    [InlineData("BUY", 1.08500, 1.08550, true, 4.61)]     // BUY win
    [InlineData("BUY", 1.08500, 1.08450, false, -4.61)]   // BUY loss
    [InlineData("PUT", 1.08500, 1.08450, true, 4.61)]     // PUT win
    [InlineData("PUT", 1.08500, 1.08550, false, -4.61)]   // PUT loss
    [InlineData("HOLD", 1.08500, 1.08500, null, 0.0)]     // HOLD neutral sample
    public void TradeOutcomeRule_TieAtTheMoneyEvaluatesCorrectly(
        string direction, double entryPrice, double exitPrice, bool? expectedWasWin, double expectedPnl)
    {
        double rawPriceDiff = (exitPrice - entryPrice) / entryPrice;
        bool isExactDoji = Math.Abs(rawPriceDiff) < 1e-8;

        bool? wasCorrect;
        double pnlBps;

        bool isBuy = !string.IsNullOrEmpty(direction) && direction.EndsWith("BUY");
        bool isPut = !string.IsNullOrEmpty(direction) && direction.EndsWith("PUT");

        if (direction is "HOLD" or "SHADOW_HOLD")
        {
            wasCorrect = null;
            pnlBps = 0.0;
        }
        else if (isExactDoji)
        {
            wasCorrect = false; // Pocket Option ATM rule: Refund is NOT a win!
            pnlBps = 0.0;
        }
        else
        {
            wasCorrect = (isBuy && exitPrice > entryPrice) || (isPut && exitPrice < entryPrice);
            double effectivePnlDiff = isPut ? -rawPriceDiff : rawPriceDiff;
            pnlBps = Math.Round(effectivePnlDiff * 10000.0, 2);
        }

        Assert.Equal(expectedWasWin, wasCorrect);
        Assert.Equal(expectedPnl, pnlBps);
    }

    [Fact]
    public void HtmlInjection_WebUtilityHtmlEncodeSanitizesSpecialCharacters()
    {
        string maliciousPocketId = "12345<script>alert(1)</script>&id=999";
        string safePocketId = WebUtility.HtmlEncode(maliciousPocketId);

        Assert.DoesNotContain("<", safePocketId);
        Assert.DoesNotContain(">", safePocketId);
        Assert.Equal("12345&lt;script&gt;alert(1)&lt;/script&gt;&amp;id=999", safePocketId);

        string rawPair = "EUR/USD & GBP/USD <OTC>";
        string safePair = WebUtility.HtmlEncode(rawPair);
        Assert.DoesNotContain("<", safePair);
        Assert.DoesNotContain(">", safePair);
        Assert.Equal("EUR/USD &amp; GBP/USD &lt;OTC&gt;", safePair);
    }
}
