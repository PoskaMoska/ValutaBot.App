using System;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.Tests.Replay;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines;

/// <summary>
/// Data-Driven Replay тесты для SmcEngine (Smart Money Concepts).
/// Проверяет детекцию BOS, Liquidity Sweep, FVG, Order Block на реальных сценариях.
/// </summary>
public class SmcReplayTests(ITestOutputHelper output)
{
    // ─── 1. Нет исключений ни на одном сценарии ─────────────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void SMC_AllScenarios_NoException(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);

        var ex = Record.Exception(() =>
            SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice));

        Assert.Null(ex);
    }

    // ─── 2. Все строковые поля — допустимые значения (не null) ──────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void SMC_AllScenarios_OutputFieldsNotNull(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var result = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"[{scenarioName}] BOS={result.BosDirection} Sweep={result.SweepDirection} " +
                         $"FVG={result.FvgType} OB={result.OrderBlockType}");

        Assert.NotNull(result.BosDirection);
        Assert.NotNull(result.SweepDirection);
        Assert.NotNull(result.FvgType);
        Assert.NotNull(result.OrderBlockType);
    }

    // ─── 3. FVG уровни не пересекаются (Top > Bottom) ───────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    public void SMC_WhenFvgDetected_TopIsAboveBottom(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var result = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"[{scenarioName}] HasFVG={result.HasFvg} Top={result.FvgTop:F5} Bottom={result.FvgBottom:F5} Gap={result.FvgGapSize:F6}");

        if (result.HasFvg)
        {
            Assert.True(result.FvgTop > result.FvgBottom,
                $"FVG.Top ({result.FvgTop:F5}) <= FVG.Bottom ({result.FvgBottom:F5}): геометрия FVG нарушена");
            Assert.True(result.FvgGapSize > 0, "FVG.GapSize <= 0 при HasFvg=true");
        }
    }

    // ─── 4. Вынос + разворот → детектируется Bullish Sweep ──────────────────

    [Fact]
    public void SMC_SweepReversal_DetectsBullishSweep()
    {
        var s = TestScenarioLoader.Load("sweep_reversal");
        // Sweep happens at index 13, so we check the state right after candle 13 is formed
        var sweepSlice = s.Candles.AsSpan(0, 14);
        var result = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, sweepSlice, sweepSlice[^1].Close);

        output.WriteLine($"SweepDirection={result.SweepDirection} HasSweep={result.HasLiquiditySweep}");

        // Сценарий спроектирован как вынос вниз с разворотом → Bullish Sweep
        Assert.True(result.HasLiquiditySweep,
            "SMC не детектировал Liquidity Sweep при паттерне вынос-разворот");
        Assert.Equal("BULLISH_SWEEP", result.SweepDirection);
    }

    // ─── 5. OrderBlock уровень — конечное число ──────────────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("sweep_reversal")]
    public void SMC_WhenObDetected_LevelIsFinite(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var result = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"[{scenarioName}] HasOB={result.HasOrderBlock} OB Level={result.OrderBlockLevel:F5}");

        if (result.HasOrderBlock)
        {
            Assert.True(double.IsFinite(result.OrderBlockLevel),
                $"OrderBlock.Level = NaN/Inf в сценарии '{scenarioName}'");
            Assert.True(result.OrderBlockLevel > 0,
                $"OrderBlock.Level = {result.OrderBlockLevel:F5}: должен быть > 0");
        }
    }

    // ─── 6. MTF Validation не крашится ──────────────────────────────────────

    [Fact]
    public void SMC_MtfValidation_NoException()
    {
        var s1 = TestScenarioLoader.Load("strong_uptrend");
        var s2 = TestScenarioLoader.Load("ranging_flat");

        var mainSmc = SmcEngine.AnalyzeSmcStructure(s1.Asset, s1.Timeframe, s1.Candles, s1.CurrentPrice);
        var htfSmc  = SmcEngine.AnalyzeSmcStructure(s2.Asset, s2.Timeframe, s2.Candles, s2.CurrentPrice);

        var ex = Record.Exception(() => SmcEngine.ValidateMtfSmcAlignment(mainSmc, htfSmc));

        Assert.Null(ex);
    }

    // ─── 7. Холодный старт (< 10 свечей) → возвращает пустой результат ──────

    [Fact]
    public void SMC_ColdStart_5Candles_ReturnsEmptyResult()
    {
        var s = TestScenarioLoader.Load("cold_start_5candles");
        var result = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"HasBOS={result.HasBos} HasSweep={result.HasLiquiditySweep} HasFVG={result.HasFvg}");

        // SmcEngine требует >= 10 свечей; с 5 должен вернуть пустой результат
        Assert.False(result.HasBos,           "SMC не должен детектировать BOS при < 10 свечах");
        Assert.False(result.HasLiquiditySweep,"SMC не должен детектировать Sweep при < 10 свечах");
    }
}
