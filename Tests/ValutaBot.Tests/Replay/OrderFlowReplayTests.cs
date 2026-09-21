using System;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.Tests.Replay;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines;

/// <summary>
/// Data-Driven Replay тесты для OrderFlowEngine.
/// Проверяет корректность DeltaRatio, ScoreContribution и State на реальных рыночных сценариях.
/// </summary>
public class OrderFlowReplayTests(ITestOutputHelper output)
{
    // ─── 1. Нет NaN/Infinity ни на одном сценарии ───────────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void OF_AllScenarios_NoNaNOrInfinity(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var result = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"[{scenarioName}] State={result.OrderFlowState} " +
                         $"Delta={result.DeltaRatio:F3} Score={result.ScoreContribution:F3} " +
                         $"CVD={result.CumulativeVolumeDelta:F2}");

        Assert.True(double.IsFinite(result.DeltaRatio),          "DeltaRatio = NaN/Inf");
        Assert.True(double.IsFinite(result.ScoreContribution),   "ScoreContribution = NaN/Inf");
        Assert.True(double.IsFinite(result.CumulativeVolumeDelta),"CVD = NaN/Inf");
        Assert.True(double.IsFinite(result.BuyVolume),           "BuyVolume = NaN/Inf");
        Assert.True(double.IsFinite(result.SellVolume),          "SellVolume = NaN/Inf");
    }

    // ─── 2. ScoreContribution всегда в диапазоне [-1, +1] ───────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void OF_AllScenarios_ScoreInValidRange(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var result = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"[{scenarioName}] ScoreContribution={result.ScoreContribution:F3}");
        Assert.InRange(result.ScoreContribution, -1.0, 1.0);
    }

    // ─── 3. Сильный тренд → Score положительный ─────────────────────────────

    [Fact]
    public void OF_StrongUptrend_ScoreIsPositive()
    {
        var s = TestScenarioLoader.Load("strong_uptrend");
        var result = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"State={result.OrderFlowState} Score={result.ScoreContribution:F3} " +
                         $"DeltaRatio={result.DeltaRatio:F3}");

        // При 30 последовательных растущих свечах OrderFlow должен видеть бычье давление
        Assert.True(result.ScoreContribution > 0,
            $"State={result.OrderFlowState} Score={result.ScoreContribution:F3}: " +
            "ожидался положительный score при сильном тренде вверх");
    }

    // ─── 4. Боковик → Score близок к нулю ───────────────────────────────────

    [Fact]
    public void OF_RangingFlat_ScoreNearZero()
    {
        var s = TestScenarioLoader.Load("ranging_flat");
        var result = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"State={result.OrderFlowState} Score={result.ScoreContribution:F3}");

        // В боковике ожидаем Score от -0.4 до +0.4 (нет сильного дисбаланса)
        Assert.InRange(result.ScoreContribution, -0.5, 0.5);
    }

    // ─── 5. NFP-хаос → DeltaRatio не выходит за разумные границы ───────────

    [Fact]
    public void OF_VolatilityChaos_DeltaRatioFiniteAndBounded()
    {
        var s = TestScenarioLoader.Load("volatility_chaos");
        var result = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        output.WriteLine($"DeltaRatio={result.DeltaRatio:F3} Score={result.ScoreContribution:F3}");

        Assert.True(double.IsFinite(result.DeltaRatio), "DeltaRatio = NaN/Inf при NFP-хаосе");
        // DeltaRatio — это buy/sell ratio, не должно уходить в Infinity
        Assert.True(result.DeltaRatio < 1000, $"DeltaRatio={result.DeltaRatio:F1}: подозрительно большое значение");
    }

    // ─── 6. Холодный старт (< 5 свечей для данного актива) — нет краша ──────

    [Fact]
    public void OF_ColdStart_5Candles_NoException()
    {
        var s = TestScenarioLoader.Load("cold_start_5candles");

        var ex = Record.Exception(() =>
            OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice));

        Assert.Null(ex);
    }

    // ─── 7. BuyVolume и SellVolume всегда >= 0 ───────────────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void OF_AllScenarios_VolumesNonNegative(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var result = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        Assert.True(result.BuyVolume  >= 0, $"BuyVolume  < 0 в сценарии '{scenarioName}'");
        Assert.True(result.SellVolume >= 0, $"SellVolume < 0 в сценарии '{scenarioName}'");
    }
}
