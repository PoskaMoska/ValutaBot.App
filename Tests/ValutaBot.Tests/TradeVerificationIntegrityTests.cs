using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
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

    [Theory]
    [InlineData("08006", true)]  // Connection failure (broken socket)
    [InlineData("57P01", true)]  // Admin shutdown (cluster restarting)
    [InlineData("57P02", true)]  // Crash shutdown
    [InlineData("57P03", true)]  // Cannot connect now
    [InlineData("53300", true)]  // Too many connections
    [InlineData("42P01", false)] // Undefined table (not transient, permanent schema defect)
    [InlineData("23505", false)] // Unique violation (not transient)
    [InlineData("42601", false)] // Syntax error (not transient)
    public void DbConnectionFactory_IsNetworkTransient_ClassifiesCorrectly(string sqlState, bool expectedTransient)
    {
        var ex = new Npgsql.PostgresException("msg", "severity", "invariantSeverity", sqlState);
        bool actual = ValutaBot.App.MiniApp.Data.DbConnectionFactory.IsNetworkTransient(ex);
        Assert.Equal(expectedTransient, actual);
    }

    [Fact]
    public void DbConnectionFactory_IOException_And_TimeoutException_AreTransient()
    {
        var ioEx = new System.IO.IOException("The socket is broken");
        var timeoutEx = new TimeoutException("Database connection wait timeout");

        Assert.True(ValutaBot.App.MiniApp.Data.DbConnectionFactory.IsNetworkTransient(ioEx));
        Assert.True(ValutaBot.App.MiniApp.Data.DbConnectionFactory.IsNetworkTransient(timeoutEx));
    }

    [Fact]
    public void PostgresDdl_CreateIndex_And_ExceptionBlocks_HaveValidClauseOrder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ValutaBot.App.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var targets = new List<string>
        {
            Path.Combine(dir!.FullName, "MiniApp", "Data", "DbConnectionFactory.cs"),
            Path.Combine(dir.FullName, "MiniApp", "Services", "WeekendShadowExperimentService.cs"),
            Path.Combine(dir.FullName, "MiniApp", "Services", "PaperTradingService.cs")
        };
        string migrationsDir = Path.Combine(dir.FullName, "MiniApp", "Data", "Migrations");
        if (Directory.Exists(migrationsDir))
        {
            targets.AddRange(Directory.GetFiles(migrationsDir, "*.sql"));
        }

        foreach (string path in targets)
        {
            string raw = File.ReadAllText(path);
            string stripped = Regex.Replace(raw, @"--[^\r\n]*", string.Empty);

            foreach (Match idx in Regex.Matches(stripped, @"CREATE(?:\s+UNIQUE)?\s+INDEX\b[^;]+;", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                Match withMatch = Regex.Match(idx.Value, @"\bWITH\s*\(", RegexOptions.IgnoreCase);
                Match whereMatch = Regex.Match(idx.Value, @"\bWHERE\b", RegexOptions.IgnoreCase);
                if (withMatch.Success && whereMatch.Success)
                {
                    Assert.True(
                        withMatch.Index < whereMatch.Index,
                        $"Invalid PostgreSQL CREATE INDEX clause order in {Path.GetFileName(path)}: WITH (...) must precede WHERE: {idx.Value}");
                }
            }

            Assert.DoesNotMatch(
                new Regex(@"\bEXCEPTION\s+WHEN\s+\w+\s+THEN\s+END\s*;", RegexOptions.IgnoreCase),
                stripped);
        }
    }
}
