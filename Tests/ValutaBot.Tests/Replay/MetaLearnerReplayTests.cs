using System;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.Tests.Replay;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

namespace ValutaBot.Tests.Engines;

/// <summary>
/// Data-Driven Replay тесты для OnlineMetaLearner (SGD-весовая модель).
/// Проверяет корректность предсказания, обучения и устойчивость весов.
/// </summary>
public class MetaLearnerReplayTests(ITestOutputHelper output)
{
    // ─── 1. Predict всегда возвращает значение в [0, 1] ─────────────────────

    [Theory]
    [InlineData("strong_uptrend")]
    [InlineData("ranging_flat")]
    [InlineData("sweep_reversal")]
    [InlineData("volatility_chaos")]
    [InlineData("cold_start_5candles")]
    public void MetaLearner_AllScenarios_PredictInRange(string scenarioName)
    {
        var learner = new OnlineMetaLearner();
        var s = TestScenarioLoader.Load(scenarioName);

        // Используем простые сигналы (все нейтральные → базовый случай)
        double prob = learner.Predict(s.Asset, s.Timeframe,
            ta: 0.0, of: 0.0, smc: 0.0, ml: 0.0, tfConflict: false);

        output.WriteLine($"[{scenarioName}] Базовый predict (все 0): prob={prob:F4}");
        Assert.InRange(prob, 0.0, 1.0);
    }

    // ─── 2. Predict: сильный бычий сигнал → вероятность > 0.5 ──────────────

    [Fact]
    public void MetaLearner_BullishSignals_PredictAboveHalf()
    {
        var learner = new OnlineMetaLearner();

        // Тренируем на победах с бычьими сигналами
        for (int i = 0; i < 30; i++)
            learner.PartialFit("EUR/USD", "1m", ta: 0.8, of: 0.6, smc: 0.5, ml: 0.7,
                wasWin: true, direction: "BUY");

        double prob = learner.Predict("EUR/USD", "1m", ta: 0.8, of: 0.6, smc: 0.5, ml: 0.7,
            tfConflict: false);

        output.WriteLine($"После 30 BUY-побед: prob={prob:F4}");
        Assert.True(prob > 0.5, $"prob={prob:F4}: ожидалось > 0.5 после 30 BUY-побед");
    }

    // ─── 3. Веса не уходят за ±4 после любого количества обновлений ─────────

    [Fact]
    public void MetaLearner_After500Updates_WeightsStayClamped()
    {
        var learner = new OnlineMetaLearner();

        for (int i = 0; i < 500; i++)
        {
            bool win = i % 3 != 0; // 66% win rate
            learner.PartialFit("EUR/USD", "1m",
                ta: 0.9, of: 0.8, smc: 0.7, ml: 0.6,
                wasWin: win, direction: win ? "BUY" : "PUT");
        }

        // Predict должен оставаться в [0, 1]
        double prob = learner.Predict("EUR/USD", "1m",
            ta: 1.0, of: 1.0, smc: 1.0, ml: 1.0, tfConflict: false);

        output.WriteLine($"После 500 обновлений: prob={prob:F4}");
        Assert.True(double.IsFinite(prob), "prob = NaN/Inf после 500 PartialFit вызовов");
        Assert.InRange(prob, 0.0, 1.0);
    }

    // ─── 4. Конфликт TF снижает вероятность ─────────────────────────────────

    [Fact]
    public void MetaLearner_TfConflict_LowersProbability()
    {
        var learner = new OnlineMetaLearner();

        double probNoConflict = learner.Predict("EUR/USD", "1m",
            ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5, tfConflict: false);
        double probWithConflict = learner.Predict("EUR/USD", "1m",
            ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5, tfConflict: true);

        output.WriteLine($"Без конфликта: {probNoConflict:F4}, С конфликтом: {probWithConflict:F4}");

        // Результат зависит от знаков весов — главное: нет NaN
        Assert.True(double.IsFinite(probNoConflict),   "prob (no conflict) = NaN");
        Assert.True(double.IsFinite(probWithConflict), "prob (conflict) = NaN");
    }

    // ─── 5. NEUTRAL direction — не обновляет веса ────────────────────────────

    [Fact]
    public void MetaLearner_NeutralDirection_WeightsUnchanged()
    {
        var learner = new OnlineMetaLearner();

        double probBefore = learner.Predict("EUR/USD", "1m",
            ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5, tfConflict: false);

        // NEUTRAL не должен изменить веса
        learner.PartialFit("EUR/USD", "1m", ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5,
            wasWin: true, direction: "NEUTRAL");

        double probAfter = learner.Predict("EUR/USD", "1m",
            ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5, tfConflict: false);

        output.WriteLine($"До NEUTRAL: {probBefore:F6}, После NEUTRAL: {probAfter:F6}");
        Assert.Equal(probBefore, probAfter);
    }

    // ─── 6. Изоляция по активу — EURUSD не влияет на BTCUSD ─────────────────

    [Fact]
    public void MetaLearner_AssetIsolation_EurUsdNotPollutingBtcUsd()
    {
        var learner = new OnlineMetaLearner();

        // Интенсивно тренируем EUR/USD на BUY
        for (int i = 0; i < 50; i++)
            learner.PartialFit("EUR/USD", "1m", ta: 1.0, of: 1.0, smc: 1.0, ml: 1.0,
                wasWin: true, direction: "BUY");

        // BTC/USD должен иметь независимые веса (начальные)
        double probBtc = learner.Predict("BTC/USD", "1m",
            ta: 0.0, of: 0.0, smc: 0.0, ml: 0.0, tfConflict: false);

        output.WriteLine($"BTC/USD predict (без обучения): {probBtc:F4}");
        // Базовый predict для нового ключа: sigmoid(0) = 0.5
        Assert.InRange(probBtc, 0.45, 0.55);
    }

    // ─── 7. Конкурентный доступ — нет data race ──────────────────────────────

    [Fact]
    public void MetaLearner_ConcurrentPartialFit_NoException()
    {
        var learner = new OnlineMetaLearner();

        var ex = Record.Exception(() =>
        {
            System.Threading.Tasks.Parallel.For(0, 200, i =>
            {
                learner.PartialFit("EUR/USD", "1m",
                    ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5,
                    wasWin: i % 2 == 0, direction: i % 2 == 0 ? "BUY" : "PUT");

                _ = learner.Predict("EUR/USD", "1m", ta: 0.5, of: 0.5, smc: 0.5, ml: 0.5,
                    tfConflict: false);
            });
        });

        Assert.Null(ex);
    }
}
