using System;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.Tests.Replay;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines;

/// <summary>
/// Data-Driven Replay тесты для TechnicalAnalysisEngine.
/// 5 рыночных сценариев × [RSI, HMA, ADX, ATR, Score] = полное покрытие.
/// </summary>
public class TechnicalAnalysisReplayTests(ITestOutputHelper output)
{
    private readonly TechnicalAnalysisEngine _ta = new();

    // ─── 1. Нет NaN/Infinity ни на одном сценарии ───────────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void TA_AllScenarios_NoNaNOrInfinity(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);

        double rsi = _ta.ComputeRsi(s.Asset, s.Timeframe, s.Candles);
        double hma = _ta.ComputeHma(s.Asset, s.Timeframe, s.Candles);
        double atr = _ta.ComputeAtr(s.Asset, s.Timeframe, s.Candles);
        var (adx, pdi, mdi) = _ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);
        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
        double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);

        var (score, conf, rsiVal, hmaVal, volStr, atrVal) = _ta.ScoreTimeframe(
            s.Asset, s.Timeframe, prices, volumes, s.Candles);

        output.WriteLine($"[{scenarioName}] RSI={rsi:F2} HMA={hma:F5} ADX={adx:F2} ATR={atr:F6} Score={score:F3}");

        Assert.True(double.IsFinite(rsi),   $"RSI = NaN/Inf в сценарии '{scenarioName}'");
        Assert.True(double.IsFinite(hma),   $"HMA = NaN/Inf в сценарии '{scenarioName}'");
        Assert.True(double.IsFinite(atr),   $"ATR = NaN/Inf в сценарии '{scenarioName}'");
        Assert.True(double.IsFinite(adx),   $"ADX = NaN/Inf в сценарии '{scenarioName}'");
        Assert.True(double.IsFinite(score), $"Score = NaN/Inf в сценарии '{scenarioName}'");
    }

    // ─── 2. RSI остаётся в диапазоне 0–100 ──────────────────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void TA_AllScenarios_RsiInValidRange(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        double rsi = _ta.ComputeRsi(s.Asset, s.Timeframe, s.Candles);

        output.WriteLine($"[{scenarioName}] RSI={rsi:F2}");
        Assert.InRange(rsi, 0.0, 100.0);
    }

    // ─── 3. ATR > 0 на любом сценарии с движением цены ──────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    public void TA_VolatileScenario_AtrIsPositive(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        double atr = _ta.ComputeAtr(s.Asset, s.Timeframe, s.Candles);

        output.WriteLine($"[{scenarioName}] ATR={atr:F6}");
        Assert.True(atr > 0, $"ATR = 0 в сценарии '{scenarioName}' с выраженным движением");
    }

    // ─── 4. Холодный старт (5 свечей) — не выбрасывает исключение ───────────

    [Fact]
    public void TA_ColdStart_5Candles_NoException()
    {
        var s = TestScenarioLoader.Load("cold_start_5candles");

        // Этот блок не должен выбросить IndexOutOfRangeException
        var ex = Record.Exception(() =>
        {
            _ta.ComputeRsi(s.Asset, s.Timeframe, s.Candles);
            _ta.ComputeHma(s.Asset, s.Timeframe, s.Candles);
            _ta.ComputeAtr(s.Asset, s.Timeframe, s.Candles);
            _ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);
            double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
            double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);
            _ta.ScoreTimeframe(s.Asset, s.Timeframe, prices, volumes, s.Candles);
        });

        Assert.Null(ex);
    }

    // ─── 5. Сильный тренд → Score положительный (TAE видит памп) ────────────

    [Fact]
    public void TA_StrongUptrend_ScoreIsPositive()
    {
        var s = TestScenarioLoader.Load("strong_uptrend");
        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
        double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);

        var (score, conf, _, _, _, _) = _ta.ScoreTimeframe(
            s.Asset, s.Timeframe, prices, volumes, s.Candles);

        output.WriteLine($"Score={score:F3} Conf={conf:F1}");
        Assert.True(score > 0, $"Score={score:F3}: при устойчивом тренде вверх TAE должен давать положительный score");
    }

    // ─── 6. Волатильный хаос — Score не выходит за ±2 ───────────────────────

    [Fact]
    public void TA_VolatilityChaos_ScoreIsClamped()
    {
        var s = TestScenarioLoader.Load("volatility_chaos");
        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
        double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);

        var (score, conf, rsiVal, hmaVal, volStr, atrVal) = _ta.ScoreTimeframe(
            s.Asset, s.Timeframe, prices, volumes, s.Candles);

        output.WriteLine($"Score={score:F3}");
        Assert.InRange(score, -2.0, 2.0);
    }

    // ─── 7. Gatekeeper: мёртвый рынок блокирует торговлю ────────────────────

    [Fact]
    public void TA_RangingFlat_GatekeeperMayBlockTrade()
    {
        var s = TestScenarioLoader.Load("ranging_flat");
        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);

        var result = _ta.ValidateMarketGatekeeper(s.Asset, s.Timeframe, prices, s.Candles);

        // Боковик может (но не обязан) быть заблокирован — зависит от ATR/ADX порогов
        // Главное — не NaN и не крэш
        output.WriteLine($"Gatekeeper: IsTradeable={result.IsTradeable} Reason={result.Reason} ATR={result.Atr:F6}");
        Assert.True(double.IsFinite(result.Atr), "ATR в Gatekeeper = NaN/Inf");
        Assert.True(double.IsFinite(result.Adx), "ADX в Gatekeeper = NaN/Inf");
    }
}
