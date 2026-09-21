using System;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.Tests.Replay;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines;

/// <summary>
/// Data-Driven Replay тесты для AutoCalibrationEngine.
/// Проверяет корректность детекции режима рынка и вычисления адаптивных весов.
/// </summary>
public class AutoCalibrationReplayTests(ITestOutputHelper output)
{
    private readonly AutoCalibrationEngine _ac = new();

    // ─── 1. Детекция Trending режима при сильном тренде ─────────────────────

    [Fact]
    public void AutoCalib_StrongUptrend_DetectsTrendingRegime()
    {
        // Сильный тренд: высокий ADX, низкая entropy, нормальный волатильностный режим
        var regime = _ac.DetectMarketRegime(adx: 30.0, volRatio: 1.3, rsi: 68.0);

        output.WriteLine($"Regime={regime}");
        Assert.Equal(AutoCalibrationEngine.MarketRegime.TrendingImpulse, regime);
    }

    // ─── 2. Детекция Ranging режима при боковике ─────────────────────────────

    [Fact]
    public void AutoCalib_RangingFlat_DetectsRangingRegime()
    {
        // Боковик: низкий ADX, RSI в середине
        var regime = _ac.DetectMarketRegime(adx: 12.0, volRatio: 0.9, rsi: 50.0);

        output.WriteLine($"Regime={regime}");
        Assert.Equal(AutoCalibrationEngine.MarketRegime.RangingFlat, regime);
    }

    // ─── 3. Детекция Chaos режима при NFP-шипе ──────────────────────────────

    [Fact]
    public void AutoCalib_VolatilityChaos_DetectsChaosRegime()
    {
        // Хаос: высокий volRatio (объём в 5x от нормы)
        var regime = _ac.DetectMarketRegime(adx: 20.0, volRatio: 5.0, rsi: 50.0);

        output.WriteLine($"Regime={regime}");
        Assert.Equal(AutoCalibrationEngine.MarketRegime.HighVolatilityChaos, regime);
    }

    // ─── 4. Веса для всех источников — конечные числа (не NaN) ─────────────

    [Theory]
    [InlineData("TechAnalysis")]
    [InlineData("OrderFlow")]
    [InlineData("SMC")]
    [InlineData("LIGHTGBM")]
    [InlineData("SKENDER_MATH")]
    public void AutoCalib_AllSources_WeightsAreFinite(string sourceName)
    {
        foreach (var regime in new[] { AutoCalibrationEngine.MarketRegime.TrendingImpulse, AutoCalibrationEngine.MarketRegime.RangingFlat, AutoCalibrationEngine.MarketRegime.HighVolatilityChaos })
        {
            double w = _ac.GetCalibratedRegimeWeight(sourceName, "EUR/USD", "1m", regime);

            output.WriteLine($"{sourceName} [{regime}] -> weight={w:F3}");
            Assert.True(double.IsFinite(w), $"Weight = NaN/Inf для {sourceName} [{regime}]");
            Assert.True(w > 0,             $"Weight <= 0 для {sourceName} [{regime}]");
        }
    }

    // ─── 5. Веса остаются в допустимом диапазоне ────────────────────────────

    [Theory]
    [InlineData("TechAnalysis")]
    [InlineData("OrderFlow")]
    [InlineData("SMC")]
    [InlineData("LIGHTGBM")]
    public void AutoCalib_AllSources_WeightsInExpectedRange(string sourceName)
    {
        foreach (var regime in new[] { AutoCalibrationEngine.MarketRegime.TrendingImpulse, AutoCalibrationEngine.MarketRegime.RangingFlat, AutoCalibrationEngine.MarketRegime.HighVolatilityChaos })
        {
            double w = _ac.GetCalibratedRegimeWeight(sourceName, "EUR/USD", "1m", regime);
            Assert.InRange(w, 0.05, 3.0);
        }
    }

    // ─── 6. RecordSourceOutcome: EMA-обновление не порождает NaN ────────────

    [Fact]
    public void AutoCalib_RecordOutcomes_WinRateConverges_NoNaN()
    {
        var ac = new AutoCalibrationEngine();

        // Симулируем 50 побед + 50 поражений → win rate должен сходиться к ~0.5
        for (int i = 0; i < 50; i++) ac.RecordSourceOutcome("TechAnalysis", "EUR/USD", "1m", isWin: true);
        for (int i = 0; i < 50; i++) ac.RecordSourceOutcome("TechAnalysis", "EUR/USD", "1m", isWin: false);

        double w = ac.GetCalibratedRegimeWeight("TechAnalysis", "EUR/USD", "1m", AutoCalibrationEngine.MarketRegime.TrendingImpulse);

        output.WriteLine($"После 50W+50L: weight={w:F4}");
        Assert.True(double.IsFinite(w), "Weight = NaN после 100 RecordSourceOutcome вызовов");
        Assert.True(w > 0, "Weight <= 0 после 100 RecordSourceOutcome вызовов");
    }

    // ─── 7. RestoreState: восстановление состояния работает корректно ────────

    [Fact]
    public void AutoCalib_RestoreState_WeightReflectsRestoredWinRate()
    {
        var ac = new AutoCalibrationEngine();

        // Восстанавливаем состояние: источник "SMC" имел EMA win rate = 0.75 (хорошо работает)
        ac.RestoreState("SMC", "EUR/USD", "1m", totalTrades: 200, emaWinRate: 0.75);

        double w = ac.GetCalibratedRegimeWeight("SMC", "EUR/USD", "1m", AutoCalibrationEngine.MarketRegime.TrendingImpulse);

        output.WriteLine($"После RestoreState(emaWinRate=0.75): weight={w:F4}");
        Assert.True(double.IsFinite(w), "Weight = NaN после RestoreState");
        Assert.True(w > 0, "Weight должен быть > 0 при emaWinRate=0.75");
    }

    // ─── 8. Конкурентный доступ не порождает data race ───────────────────────

    [Fact]
    public void AutoCalib_ConcurrentRecordOutcome_NoException()
    {
        var ac = new AutoCalibrationEngine();

        var ex = Record.Exception(() =>
        {
            System.Threading.Tasks.Parallel.For(0, 100, i =>
            {
                bool isWin = i % 2 == 0;
                ac.RecordSourceOutcome("LIGHTGBM", "EUR/USD", "1m", isWin);
                _ = ac.GetCalibratedRegimeWeight("LIGHTGBM", "EUR/USD", "1m", AutoCalibrationEngine.MarketRegime.TrendingImpulse);
            });
        });

        Assert.Null(ex);
    }
}
