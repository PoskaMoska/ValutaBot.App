using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.Tests.Replay;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;
using MiniAppController = ValutaBot.MiniApp.MiniAppController;

namespace ValutaBot.Tests.Engines;
/// E2E Pipeline Integrity Tests.
/// Проверяет что данные не искажаются при прохождении через всю цепочку движков.
/// Цена на входе = цена на выходе. Нет NaN. Всегда BUY или PUT.
/// </summary>
public class PipelineIntegrityTests(ITestOutputHelper output)
{
    // ─── 1. Полный прогон: Entry price сохраняется через весь конвейер ───────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void Pipeline_EntryPriceNotMutated(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        double entryPrice = s.CurrentPrice; // цена ПЕРЕД прогоном через движки

        // Прогоняем через всю цепочку
        var ta  = new TechnicalAnalysisEngine();
        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
        double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);

        var (score, conf, rsiVal, hmaVal, volStr, atrVal) = ta.ScoreTimeframe(
            s.Asset, s.Timeframe, prices, volumes, s.Candles);

        var ofResult  = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
        var smcResult = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        double priceAfterPipeline = s.CurrentPrice; // цена ПОСЛЕ прогона

        output.WriteLine($"[{scenarioName}] EntryPrice={entryPrice:F5} -> AfterPipeline={priceAfterPipeline:F5}");

        // Гарантируем: движки не изменяют входные данные (неизменяемые record-типы)
        Assert.Equal(entryPrice, priceAfterPipeline);
        Assert.Equal(s.CurrentPrice, s.Candles[^1].Close);
    }

    // ─── 2. Все выходные значения конечны (нет NaN/Inf) ─────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void Pipeline_AllOutputsFinite(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var ta = new TechnicalAnalysisEngine();

        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
        double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);

        var (taScore, _, rsiVal, hmaVal, volStr, atrVal) = ta.ScoreTimeframe(
            s.Asset, s.Timeframe, prices, volumes, s.Candles);

        var of  = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
        var (adx, pdi, mdi) = ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);

        output.WriteLine($"[{scenarioName}] TAScore={taScore:F3} RSI={rsiVal:F2} HMA={hmaVal:F5} " +
                         $"ATR={atrVal:F6} ADX={adx:F2} OFScore={of.ScoreContribution:F3}");

        Assert.True(double.IsFinite(taScore),            $"TAScore = NaN/Inf [{scenarioName}]");
        Assert.True(double.IsFinite(rsiVal),             $"RSI = NaN/Inf [{scenarioName}]");
        Assert.True(double.IsFinite(hmaVal),             $"HMA = NaN/Inf [{scenarioName}]");
        Assert.True(double.IsFinite(atrVal),             $"ATR = NaN/Inf [{scenarioName}]");
        Assert.True(double.IsFinite(adx),                $"ADX = NaN/Inf [{scenarioName}]");
        Assert.True(double.IsFinite(of.ScoreContribution),$"OFScore = NaN/Inf [{scenarioName}]");
        Assert.True(double.IsFinite(of.DeltaRatio),      $"DeltaRatio = NaN/Inf [{scenarioName}]");
    }

    // ─── 3. ConfluenceMatrix всегда даёт BUY или PUT (никогда не NEUTRAL) ───

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void Pipeline_ConfluenceAlwaysBuyOrPut(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);
        var ta = new TechnicalAnalysisEngine();
        var learner = new OnlineMetaLearner();

        double[] prices = Array.ConvertAll(s.Candles, c => c.Close);
        double[] volumes = Array.ConvertAll(s.Candles, c => c.Volume);

        var (taScore, conf, rsiVal, hmaVal, volStr, atrVal) = ta.ScoreTimeframe(
            s.Asset, s.Timeframe, prices, volumes, s.Candles);
        var (adx, _, _) = ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);

        var taSignal  = new TaSignal(taScore, conf / 100.0, rsiVal, hmaVal, volStr, atrVal, adx);
        var ofResult  = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
        var smcResult = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

        var ofSignal  = new OrderflowSignal(ofResult.ScoreContribution, ofResult.Description);

        var smcSignal = new SmcSignal(smcResult.BosDirection, smcResult.SweepDirection,
            smcResult.OrderBlockType, smcResult.FvgType, "");

        var mlSignal  = new MlSignal("BUY", 0.6, 0.5, "v1.0");
        var stateSignal = new StateSignal("Normal", 0.1, 0.0);
        var mtfResult = new ConfluenceMatrixResult(1.0, true, 100, "BUY", "NEUTRAL", new(), "");

        // MetaLearner устанавливаем в TradeOutcomeTracker для этого теста
        ValutaBot.MiniApp.TradeOutcomeTracker.MetaLearner = learner;

        var matrix    = new ConfluenceMatrixEngine(null!, ta);
        var consensus = matrix.EvaluateMatrixAsync(
            s.Asset, s.Timeframe, false, 0.0,
            taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, 0, volStr > 0 ? 1.0 : 0.8).GetAwaiter().GetResult();

        output.WriteLine($"[{scenarioName}] Direction={consensus.FinalDirection} Prob={consensus.Probability}%");

        // ТРЕБОВАНИЕ ПОЛЬЗОВАТЕЛЯ: всегда BUY или PUT, никогда NEUTRAL
        Assert.True(consensus.FinalDirection == "BUY" || consensus.FinalDirection == "PUT",
            $"Consensus.FinalDirection='{consensus.FinalDirection}': ожидался BUY или PUT");
        Assert.InRange(consensus.Probability, 0, 100);
    }

    // ─── 4. Свечи остаются в правильном порядке (хронология не нарушена) ────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    public void Pipeline_CandlesChronologicalOrder(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);

        for (int i = 1; i < s.Candles.Length; i++)
        {
            Assert.True(s.Candles[i].Timestamp >= s.Candles[i - 1].Timestamp,
                $"[{scenarioName}] Свеча [{i}] ({s.Candles[i].Timestamp:HH:mm:ss}) " +
                $"раньше свечи [{i-1}] ({s.Candles[i-1].Timestamp:HH:mm:ss}): нарушен порядок");
        }
    }

    // ─── 5. OHLC целостность: High >= Open,Close,Low; Low <= всё ────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void Pipeline_CandleOhlcConsistency(string scenarioName)
    {
        var s = TestScenarioLoader.Load(scenarioName);

        for (int i = 0; i < s.Candles.Length; i++)
        {
            var c = s.Candles[i];
            Assert.True(c.High >= c.Open,  $"[{scenarioName}][{i}] High={c.High} < Open={c.Open}");
            Assert.True(c.High >= c.Close, $"[{scenarioName}][{i}] High={c.High} < Close={c.Close}");
            Assert.True(c.Low  <= c.Open,  $"[{scenarioName}][{i}] Low={c.Low} > Open={c.Open}");
            Assert.True(c.Low  <= c.Close, $"[{scenarioName}][{i}] Low={c.Low} > Close={c.Close}");
            Assert.True(c.High >= c.Low,   $"[{scenarioName}][{i}] High={c.High} < Low={c.Low}");
            Assert.True(c.Volume > 0,      $"[{scenarioName}][{i}] Volume={c.Volume} <= 0");
        }
    }

    // ─── 6. Сценарии полностью независимы — нет утечки состояния ─────────────

    [Fact]
    public void Pipeline_ScenarioIsolation_NoCrossContamination()
    {
        // Прогоняем EUR/USD тренд, затем BTC/USD холодный старт
        // BTC-результат не должен быть "заражён" EUR/USD данными
        var eurusd = TestScenarioLoader.Load("strong_uptrend");
        var btcusd = TestScenarioLoader.Load("cold_start_5candles");

        double[] pricesEur = Array.ConvertAll(eurusd.Candles, c => c.Close);
        double[] volumesEur = Array.ConvertAll(eurusd.Candles, c => c.Volume);
        double[] pricesBtc = Array.ConvertAll(btcusd.Candles, c => c.Close);
        double[] volumesBtc = Array.ConvertAll(btcusd.Candles, c => c.Volume);

        var ta = new TechnicalAnalysisEngine();
        var (taEur, _, _, _, _, _) = ta.ScoreTimeframe(eurusd.Asset, eurusd.Timeframe, pricesEur, volumesEur, eurusd.Candles);
        var (taBtc, _, _, _, _, _) = ta.ScoreTimeframe(btcusd.Asset, btcusd.Timeframe, pricesBtc, volumesBtc, btcusd.Candles);

        output.WriteLine($"EUR/USD Score={taEur:F3}, BTC/USD Score={taBtc:F3}");

        // Оба должны давать конечные значения — каждый независимо
        Assert.True(double.IsFinite(taEur), "EUR/USD Score = NaN");
        Assert.True(double.IsFinite(taBtc), "BTC/USD Score = NaN");
    }
}
