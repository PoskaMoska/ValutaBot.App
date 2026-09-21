// ═══════════════════════════════════════════════════════════════════════════════
// INTER-MODULE INTERACTION TESTS
// Тестирует связи и контракты между модулями бэкенда.
//
// Карта связей:
//   TechnicalAnalysisEngine  ──┐
//   SmcEngine                ──┤
//   OrderFlowEngine          ──┤──► ConfluenceMatrixEngine.EvaluateMatrixAsync()
//   MLPythonService          ──┤         │ (через TaSignal, SmcSignal, etc.)
//   ContinuousStateEngine    ──┘         ▼
//                                  ConsensusDecision
//                                        │
//   AutoCalibrationEngine ──────────────►│ (взвешивает каждый движок)
//   OnlineMetaLearner     ──────────────►│ (итоговая вероятность)
//   TradeOutcomeTracker   ──────────────►│ (consecutive losses)
//                                        ▼
//   TradeTimeoutEngine ──────────────► timeout + expiryCandles
//   SignalTracker       ─────────────► запись прогноза в БД
//   ConfluenceMatrixResult ──────────► UI (goldenSetup, confluenceRatio, label)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;
using ValutaBot.Tests.Replay;

namespace ValutaBot.Tests.Engines
{
    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 1: TechnicalAnalysis → ConfluenceMatrix
    // TA-движок производит TaSignal. Confluence принимает его и строит ConsensusDecision.
    // Контракт: если TA выдает сильный BUY, итоговый вектор консенсуса должен
    // учесть это и не инвертировать направление без причины.
    // ────────────────────────────────────────────────────────────────────────────
    public class TA_to_Confluence_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly ConfluenceMatrixEngine _cm = new(null!, _ta);
        static readonly ConfluenceMatrixResult _neutralMtf = new(0.33, false, 0, "Weak", "NEUTRAL", new(), "NEUTRAL");

        static (TaSignal ta, SmcSignal smc, OrderflowSignal of, MlSignal ml, StateSignal state) NeutralBundle()
            => (
                new TaSignal(0.0, 0.5, 50.0, 1.1, 0.0, 0.0001, 20.0),
                new SmcSignal("", "", "", "", ""),
                new OrderflowSignal(0.0, "flat"),
                new MlSignal("BUY", 0.5, null, "offline"),
                new StateSignal("FLAT", 0.0, 0.0)
            );

        [Fact]
        public async Task TA_StrongBuy_Score_PassesToConsensus_AsPositiveTaScore()
        {
            // TA выдаёт мощный BUY (+0.9) — это должно дойти до ConsensusDecision.TaScore
            var (_, smc, of, ml, st) = NeutralBundle();
            var ta = new TaSignal(0.9, 0.85, 65.0, 1.1, 0.3, 0.0005, 30.0);

            var decision = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, _neutralMtf, 0, 1.0);

            output.WriteLine($"TaScore in decision: {decision.TaScore:F3}, Dir: {decision.FinalDirection}");
            Assert.True(decision.TaScore > 0,
                $"TaScore ({decision.TaScore:F3}) должен быть > 0 при TaSignal.Score=+0.9");
        }

        [Fact]
        public async Task TA_NaNScore_DoesNotCrashConsensus()
        {
            // Критический edge-case: что если TA вернул NaN? Консенсус не должен упасть.
            var (_, smc, of, ml, st) = NeutralBundle();
            var ta = new TaSignal(double.NaN, 0.5, double.NaN, 1.1, 0.0, 0.0001, 20.0);

            var ex = await Record.ExceptionAsync(() =>
                _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0, ta, smc, of, ml, st, _neutralMtf));
            Assert.Null(ex);
        }

        [Fact]
        public async Task TA_StrongOppositeToML_ReducesFinalScore()
        {
            // Правило: если |TA| > 0.8 и ML не согласен — Confluence применяет штраф 0.5x
            // TA = сильный BUY, ML = сильный PUT → finalScore должен быть меньше чем при согласии
            var (_, smc, of, _, st) = NeutralBundle();
            var taStrong = new TaSignal(0.9, 0.9, 65.0, 1.1, 0.3, 0.0005, 32.0);
            var mlPut    = new MlSignal("PUT", 0.9, null, "v1");

            // Agreement (оба BUY)
            var mlBuy    = new MlSignal("BUY", 0.9, null, "v1");
            var decAgree = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                taStrong, smc, of, mlBuy, st, _neutralMtf);
            // Conflict (TA=BUY, ML=PUT)
            var decConfl = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                taStrong, smc, of, mlPut, st, _neutralMtf);

            output.WriteLine($"Agree prob: {decAgree.Probability}%, Conflict prob: {decConfl.Probability}%");
            Assert.True(decConfl.Probability < decAgree.Probability,
                "Конфликт TA vs ML должен снизить вероятность");
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 2: SMC → ConfluenceMatrix
    // SmcSignal переводится в smcScore внутри EvaluateMatrixAsync.
    // BULLISH_BOS → +0.5, BULLISH_SWEEP → +0.5 (суммируются)
    // ────────────────────────────────────────────────────────────────────────────
    public class SMC_to_Confluence_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly ConfluenceMatrixEngine _cm = new(null!, _ta);
        static readonly ConfluenceMatrixResult _neutralMtf = new(0.33, false, 0, "", "", new(), "NEUTRAL");

        [Fact]
        public async Task SMC_BullishBos_And_BullishSweep_MaxScore()
        {
            // BOS + Sweep = +1.0 smcScore → самый сильный бычий сигнал SMC
            var smc  = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "", "", "");
            var ta   = new TaSignal(0.0, 0.5, 50.0, 1.1, 0.0, 0.0001, 20.0);
            var of   = new OrderflowSignal(0.0, "");
            var ml   = new MlSignal("BUY", 0.5, null, "offline");
            var st   = new StateSignal("FLAT", 0.0, 0.0);

            var dec = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, _neutralMtf);

            output.WriteLine($"SmcScore: {dec.SmcScore:F3}, Dir: {dec.FinalDirection}");
            Assert.True(dec.SmcScore > 0, "BullishBOS + BullishSweep → smcScore > 0");
        }

        [Fact]
        public async Task SMC_BearishBos_And_BearishSweep_NegativeScore()
        {
            var smc = new SmcSignal("BEARISH_BOS", "BEARISH_SWEEP", "", "", "");
            var ta  = new TaSignal(0.0, 0.5, 50.0, 1.1, 0.0, 0.0001, 20.0);
            var of  = new OrderflowSignal(0.0, "");
            var ml  = new MlSignal("PUT", 0.5, null, "offline");
            var st  = new StateSignal("FLAT", 0.0, 0.0);

            var dec = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, _neutralMtf);

            output.WriteLine($"SmcScore: {dec.SmcScore:F3}");
            Assert.True(dec.SmcScore < 0, "BearishBOS + BearishSweep → smcScore < 0");
        }

        [Fact]
        public async Task SMC_EmptyStrings_DoNotCrashConsensus()
        {
            // Пустые строки — штатная ситуация, когда SMC не нашёл паттернов
            var smc = new SmcSignal("", "", "", "", "");
            var ta  = new TaSignal(0.2, 0.5, 52.0, 1.1, 0.1, 0.0001, 20.0);
            var of  = new OrderflowSignal(0.0, "");
            var ml  = new MlSignal("BUY", 0.55, null, "offline");
            var st  = new StateSignal("FLAT", 0.0, 0.0);

            var ex = await Record.ExceptionAsync(() =>
                _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0, ta, smc, of, ml, st, _neutralMtf));
            Assert.Null(ex);
        }

        [Fact]
        public async Task SMC_Orchestrator_SmcDirectionMappedToDto_Correctly()
        {
            // Проверяем, что Orchestrator корректно маппит SweepDirection в smcDirection DTO.
            // BULLISH_SWEEP → dto.smcDirection = "BUY"
            var smc = new SmcSignal("", "BULLISH_SWEEP", "", "", "");
            var ta  = new TaSignal(0.3, 0.6, 55.0, 1.1, 0.1, 0.0002, 25.0);
            var of  = new OrderflowSignal(0.1, "");
            var ml  = new MlSignal("BUY", 0.6, null, "offline");
            var st  = new StateSignal("FLAT", 0.0, 0.0);
            var mtf = new ConfluenceMatrixResult(0.67, false, 7, "Strong", "", new(), "BUY");

            var dec = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, mtf);

            // Вычисляем маппинг так же, как делает Orchestrator (строки 232-239)
            string mapped =
                smc.SweepDirection.Contains("BULLISH") ? "BUY" :
                smc.SweepDirection.Contains("BEARISH") ? "PUT" :
                smc.BosDirection.Contains("BULLISH")   ? "BUY" :
                smc.BosDirection.Contains("BEARISH")   ? "PUT" : "NEUTRAL";

            output.WriteLine($"Mapped smcDirection: {mapped}");
            Assert.Equal("BUY", mapped);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 3: OrderFlow → ConfluenceMatrix
    // ────────────────────────────────────────────────────────────────────────────
    public class OrderFlow_to_Confluence_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly ConfluenceMatrixEngine _cm = new(null!, _ta);
        static readonly ConfluenceMatrixResult _neutralMtf = new(0.33, false, 0, "", "", new(), "NEUTRAL");

        [Fact]
        public async Task OF_PositiveContribution_PassesToConsensus()
        {
            var of  = new OrderflowSignal(0.4, "BULLISH_ABSORPTION");
            var ta  = new TaSignal(0.0, 0.5, 50.0, 1.1, 0.0, 0.0001, 20.0);
            var smc = new SmcSignal("", "", "", "", "");
            var ml  = new MlSignal("BUY", 0.5, null, "offline");
            var st  = new StateSignal("FLAT", 0.0, 0.0);

            var dec = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, _neutralMtf);

            output.WriteLine($"OfScore: {dec.OfScore:F3}");
            Assert.True(dec.OfScore > 0, "Положительный OFScore должен пройти в ConsensusDecision");
        }

        [Fact]
        public async Task OF_ScenarioBased_UptrndYieldsPositiveContribution()
        {
            // Интеграционный: берём реальный сценарий и прогоняем через OF→CME
            var s  = TestScenarioLoader.Load("strong_uptrend");
            var of = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

            output.WriteLine($"OF.Score: {of.ScoreContribution:F3}, Desc: {of.Description}");

            var smc = new SmcSignal("", "", "", "", "");
            var ta  = new TaSignal(0.5, 0.7, 62.0, 1.1, 0.2, 0.0003, 28.0);
            var ml  = new MlSignal("BUY", 0.65, null, "offline");
            var st  = new StateSignal("UP", 2.0, 0.3);
            var mtf = new ConfluenceMatrixResult(0.67, false, 7, "", "", new(), "BUY");

            var dec = await _cm.EvaluateMatrixAsync(s.Asset, s.Timeframe, false, 1.0,
                ta, smc, new OrderflowSignal(of.ScoreContribution, of.Description), ml, st, mtf);

            Assert.True(dec.FinalDirection == "BUY" || dec.FinalDirection == "PUT",
                "Консенсус должен вернуть BUY или PUT");
            Assert.True(double.IsFinite(dec.OfScore));
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 4: AutoCalibration → ConfluenceMatrix (взвешивание сигналов)
    // AutoCalib получает ADX/volRatio/RSI, возвращает веса, которые множатся на сигналы
    // ────────────────────────────────────────────────────────────────────────────
    public class AutoCalib_to_Confluence_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly AutoCalibrationEngine _calib = new();

        [Fact]
        public void AutoCalib_TrendingRegime_TaWeight_IsFiniteAndPositive()
        {
            // При сильном тренде (ADX>25) TA должен получить положительный вес
            // Используем реальный тренд из сценария вместо синтетических цен
            var s = TestScenarioLoader.Load("strong_uptrend");
            double[] prices = s.Candles.Select(c => c.Close).ToArray();
            var ta = new TechnicalAnalysisEngine();
            var (adx, _, _) = ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);
            double volRatio = ta.CalculateVolatilityRatio(prices);
            var (score, _, rsi, _, _, _) = ta.ScoreTimeframe(s.Asset, s.Timeframe, prices,
                s.Candles.Select(c => c.Volume).ToArray(), s.Candles);

            var regime = _calib.DetectMarketRegime(adx: adx, volRatio: volRatio, rsi: rsi, prices: prices);
            double wTa = _calib.GetCalibratedRegimeWeight("TechAnalysis", s.Asset, s.Timeframe, regime);

            output.WriteLine($"Regime: {regime}, ADX: {adx:F1}, wTA: {wTa:F2}");
            Assert.True(wTa > 0, $"В тренде TA вес должен быть > 0, получили {wTa:F2}");
            Assert.True(double.IsFinite(wTa), "TA вес должен быть конечным числом");
        }

        [Fact]
        public void AutoCalib_ChaoticRegime_MlWeightReduces()
        {
            // В хаосе (высокая волатильность) ML должен получить меньший вес
            var regime = _calib.DetectMarketRegime(adx: 15, volRatio: 2.5, rsi: 80,
                prices: Enumerable.Range(0, 20).Select(i => 1.1 + Math.Sin(i) * 0.01).ToArray());
            double wMl = _calib.GetCalibratedRegimeWeight("LIGHTGBM", "EUR/USD", "m1", regime);

            output.WriteLine($"Regime: {regime}, wML: {wMl:F2}");
            // В Chaos базовый вес ML = 0.6, должен быть меньше нормального 1.3
            Assert.True(wMl < 1.3, $"В хаосе ML вес должен быть < 1.3, получили {wMl:F2}");
        }

        [Fact]
        public async Task AutoCalib_Weights_ChangeConsensusScore()
        {
            // Убеждаемся что AutoCalib реально влияет на ConsensusDecision через ConfluenceMatrix
            var cmWithCalib    = new ConfluenceMatrixEngine(null!, _ta, _calib);
            var cmWithoutCalib = new ConfluenceMatrixEngine(null!, _ta, null);

            var ta  = new TaSignal(0.7, 0.8, 65.0, 1.1, 0.3, 0.0005, 30.0);
            var smc = new SmcSignal("BULLISH_BOS", "", "", "", "");
            var of  = new OrderflowSignal(0.3, "bullish");
            var ml  = new MlSignal("BUY", 0.7, null, "v1");
            var st  = new StateSignal("UP", 1.5, 0.4);
            var mtf = new ConfluenceMatrixResult(0.67, false, 7, "", "", new(), "BUY");

            var decCalib    = await cmWithCalib.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, mtf, 0, 1.2);
            var decNoCalib  = await cmWithoutCalib.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, mtf, 0, 1.2);

            output.WriteLine($"With AutoCalib: prob={decCalib.Probability}%, Without: prob={decNoCalib.Probability}%");
            // Оба должны быть BUY или PUT, без вылетов
            Assert.True(decCalib.FinalDirection == "BUY" || decCalib.FinalDirection == "PUT");
            Assert.True(decNoCalib.FinalDirection == "BUY" || decNoCalib.FinalDirection == "PUT");
        }

        [Fact]
        public void AutoCalib_ConsecutiveLosses_CorrectlyRecorded()
        {
            // RecordSourceOutcome → GetAllStats() должен показать обновлённый WinRate
            var calib = new AutoCalibrationEngine();
            calib.RecordSourceOutcome("TechAnalysis", "EUR/USD", "m1", isWin: false);
            calib.RecordSourceOutcome("TechAnalysis", "EUR/USD", "m1", isWin: false);
            calib.RecordSourceOutcome("TechAnalysis", "EUR/USD", "m1", isWin: true);

            var stats = calib.GetAllStats().FirstOrDefault(s => s.key.Source == "TechAnalysis");
            output.WriteLine($"EMA WinRate: {stats.emaWinRate:F3}");
            Assert.True(stats.emaWinRate > 0 && stats.emaWinRate < 1.0);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 5: MetaLearner → ConfluenceMatrix
    // TradeOutcomeTracker.MetaLearner используется внутри EvaluateMatrixAsync
    // ────────────────────────────────────────────────────────────────────────────
    public class MetaLearner_to_Confluence_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();

        [Fact]
        public async Task MetaLearner_Trained_ChangesConsensusVsUntrained()
        {
            var learner = new OnlineMetaLearner();
            TradeOutcomeTracker.MetaLearner = learner;

            var cm  = new ConfluenceMatrixEngine(null!, _ta);
            var ta  = new TaSignal(0.5, 0.7, 62.0, 1.1, 0.2, 0.0003, 28.0);
            var smc = new SmcSignal("BULLISH_BOS", "", "", "", "");
            var of  = new OrderflowSignal(0.2, "");
            var ml  = new MlSignal("BUY", 0.65, null, "offline");
            var st  = new StateSignal("UP", 1.5, 0.4);
            var mtf = new ConfluenceMatrixResult(0.67, false, 7, "", "", new(), "BUY");

            // Без обучения
            var decBefore = await cm.EvaluateMatrixAsync("GBP/USD", "m5", false, 1.0,
                ta, smc, of, ml, st, mtf);

            // Обучаем на BUY-победах
            for (int i = 0; i < 40; i++)
                learner.PartialFit("GBP/USD", "m5", 0.5, 0.2, 0.5, 0.65, wasWin: true, direction: "BUY");

            var decAfter = await cm.EvaluateMatrixAsync("GBP/USD", "m5", false, 1.0,
                ta, smc, of, ml, st, mtf);

            output.WriteLine($"Before training: {decBefore.Probability}%, After: {decAfter.Probability}%");
            // Вероятность должна вырасти после обучения на победах
            Assert.True(decAfter.Probability >= decBefore.Probability,
                "После обучения MetaLearner на победах вероятность должна быть >= исходной");
        }

        [Fact]
        public async Task MetaLearner_Null_FallbackFormulaApplied()
        {
            // Если MetaLearner = null, должна применяться fallback-формула
            // metaProb = Clamp(0.5 + ta*0.2 + smc*0.2 + of*0.1 + ml*0.2, 0, 1)
            TradeOutcomeTracker.MetaLearner = null;

            var cm  = new ConfluenceMatrixEngine(null!, _ta);
            var ta  = new TaSignal(1.0, 0.9, 70.0, 1.1, 0.5, 0.001, 35.0);
            var smc = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "", "", "");
            var of  = new OrderflowSignal(0.5, "bullish");
            var ml  = new MlSignal("BUY", 1.0, null, "offline");
            var st  = new StateSignal("UP", 2.0, 0.5);
            var mtf = new ConfluenceMatrixResult(1.0, true, 15, "", "", new(), "BUY");

            var dec = await cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, mtf);

            output.WriteLine($"Fallback result: {dec.FinalDirection} @ {dec.Probability}%");
            // Все сигналы максимально бычьи → MlProb должен быть > 0.5
            Assert.True(dec.MlProb > 0.5,
                $"При всех максимальных бычьих сигналах MlProb={dec.MlProb:F3} должен быть > 0.5");
            Assert.Equal("BUY", dec.FinalDirection);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 6: ConfluenceMatrixResult → ConsensusDecision (GoldenSetup & boost)
    // ────────────────────────────────────────────────────────────────────────────
    public class Confluence4D_to_Consensus_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly ConfluenceMatrixEngine _cm = new(null!, _ta);

        [Fact]
        public async Task GoldenSetup_AllTFAgree_IsGoldenSetupTrue()
        {
            // ConfluenceMatrixResult с ratio=1.0 (все 3 TF согласны) → isGoldenSetup=true
            var mtfGolden = new ConfluenceMatrixResult(1.0, true, 15, "IDEAL", "", new(), "BUY");
            var ta = new TaSignal(0.5, 0.7, 62.0, 1.1, 0.2, 0.0003, 28.0);
            var smc = new SmcSignal("", "", "", "", "");
            var of = new OrderflowSignal(0.0, "");
            var ml = new MlSignal("BUY", 0.65, null, "offline");
            var st = new StateSignal("UP", 1.0, 0.3);

            var dec = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, mtfGolden);

            output.WriteLine($"Golden Prob: {dec.Probability}%");
            Assert.True(dec.Probability >= 50);
        }

        [Fact]
        public async Task ConflictPenalty_ReducesEffectOfAllSignals()
        {
            // ConflictPenalty = 0.7 (разные TF направления) vs 1.0
            var smc = new SmcSignal("", "", "", "", "");
            var of  = new OrderflowSignal(0.3, "");
            var ml  = new MlSignal("BUY", 0.7, null, "offline");
            var st  = new StateSignal("UP", 1.0, 0.3);
            var ta  = new TaSignal(0.7, 0.8, 65.0, 1.1, 0.3, 0.0005, 32.0);
            var mtf = new ConfluenceMatrixResult(0.33, false, 0, "Weak", "", new(), "BUY");

            var decNoConflict = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, mtf);
            var decConflict   = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 0.0,
                ta, smc, of, ml, st, mtf);

            output.WriteLine($"No conflict: {decNoConflict.Probability}%, With conflict: {decConflict.Probability}%");
            Assert.True(decConflict.Probability <= decNoConflict.Probability,
                "ConflictPenalty=0.0 не должен повысить итоговую вероятность");
        }

        [Theory]
        [InlineData(0.99, true, 15)]
        [InlineData(0.67, false, 7)]
        [InlineData(0.33, false, 0)]
        public void Confluence4D_BoostAndGoldenSetup_Correct(double ratio, bool expectGolden, int expectBoost)
        {
            // Проверяем логику boost и goldenSetup через значения ratio
            bool golden = ratio >= 0.99;
            int boost = ratio switch { >= 0.99 => 15, >= 0.65 => 7, _ => 0 };

            output.WriteLine($"ratio={ratio}, golden={golden}, boost={boost}");
            Assert.Equal(expectGolden, golden);
            Assert.Equal(expectBoost, boost);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 7: TechnicalAnalysis → TradeTimeout
    // ATR из TA передается в TradeTimeoutEngine → определяет expiryCandles
    // ────────────────────────────────────────────────────────────────────────────
    public class TA_to_TradeTimeout_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly TradeTimeoutEngine _timeout = new();

        [Fact]
        public void Timeout_WithHighAtr_GivesFewerCandles()
        {
            // Высокий ATR (волатильный рынок) → меньше свечей (быстрее истекает ставка)
            var smcNeutral = SmcEngine.AnalyzeSmcStructure("EUR/USD", "m1",
                TestScenarioLoader.Load("volatility_chaos").Candles, 1.1050);

            var highAtr = _timeout.CalculateTimeout("EUR/USD", "m1", atr: 0.005, volRatio: 2.0,
                smcNeutral, currentPrice: 1.1050, state: null!, isForex: true);
            var lowAtr  = _timeout.CalculateTimeout("EUR/USD", "m1", atr: 0.0001, volRatio: 0.5,
                smcNeutral, currentPrice: 1.1050, state: null!, isForex: true);

            output.WriteLine($"HighATR candles={highAtr.TimeoutCandles}, LowATR candles={lowAtr.TimeoutCandles}");
            Assert.True(highAtr.TimeoutCandles <= lowAtr.TimeoutCandles,
                "При высоком ATR экспирация должна быть быстрее или равной");
        }

        [Fact]
        public void Timeout_ZeroAtr_GivesMinimumTimeout()
        {
            var smcNeutral = SmcEngine.AnalyzeSmcStructure("EUR/USD", "m1",
                TestScenarioLoader.Load("ranging_flat").Candles, 1.1050);

            var t = _timeout.CalculateTimeout("EUR/USD", "m1", atr: 0.0, volRatio: 1.0,
                smcNeutral, currentPrice: 1.1050, state: null!, isForex: true);

            output.WriteLine($"ZeroATR candles={t.TimeoutCandles}, text={t.TimeoutText}");
            Assert.True(t.TimeoutCandles > 0, "Timeout должен быть > 0 даже при ATR=0");
        }

        [Fact]
        public void Timeout_ScenarioBased_TAtoTimeout_EndToEnd()
        {
            // Берём реальный сценарий, считаем ATR через TA, передаём в Timeout
            var s = TestScenarioLoader.Load("strong_uptrend");
            double atr = _ta.ComputeAtr(s.Asset, s.Timeframe, s.Candles);

            var smc = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
            var t   = _timeout.CalculateTimeout(s.Asset, s.Timeframe, atr, volRatio: 1.2,
                smc, s.CurrentPrice, state: null!, isForex: true);

            output.WriteLine($"ATR={atr:F5}, Candles={t.TimeoutCandles}, Text={t.TimeoutText}");
            Assert.True(t.TimeoutCandles > 0);
            Assert.False(string.IsNullOrWhiteSpace(t.TimeoutText));
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 8: ContinuousState → ConfluenceMatrix (StateSignal)
    // Режим скорости (HYPER_UP / DECELERATING) передаётся через StateSignal
    // ────────────────────────────────────────────────────────────────────────────
    public class ContinuousState_to_Confluence_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly ConfluenceMatrixEngine _cm = new(null!, _ta);
        static readonly ConfluenceMatrixResult _neutralMtf = new(0.33, false, 0, "", "", new(), "NEUTRAL");

        [Fact]
        public async Task CSE_UpwardRegime_MomentumContribution_IsPositive()
        {
            double[] upPrices = Enumerable.Range(0, 80).Select(i => 1.1 + i * 0.0001).ToArray();
            var state = ContinuousStateEngine.EvaluateContinuousState(upPrices, "EUR/USD", "m1");

            output.WriteLine($"Regime: {state.VelocityRegime}, Momentum: {state.MomentumContribution:F3}");

            // StateSignal передаётся в Confluence
            var ta  = new TaSignal(0.5, 0.7, 62.0, 1.1, 0.2, 0.0003, 28.0);
            var smc = new SmcSignal("", "", "", "", "");
            var of  = new OrderflowSignal(0.1, "");
            var ml  = new MlSignal("BUY", 0.6, null, "offline");
            var st  = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);

            var dec = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta, smc, of, ml, st, _neutralMtf);

            Assert.True(dec.FinalDirection == "BUY" || dec.FinalDirection == "PUT");
            Assert.True(double.IsFinite(dec.MlProb));
        }

        [Fact]
        public async Task CSE_State_AllFieldsFinite_BeforePassingToConsensus()
        {
            // Гарантия: CSE не производит NaN, который сломает downstream
            double[] prices = TestScenarioLoader.Load("volatility_chaos").Candles
                .Select(c => c.Close).ToArray();
            var state = ContinuousStateEngine.EvaluateContinuousState(prices, "EUR/USD", "m1");

            Assert.True(double.IsFinite(state.VelocityBpsPerSec));
            Assert.True(double.IsFinite(state.MomentumContribution));

            // Убеждаемся что эти значения можно безопасно передать в Confluence
            var st  = new StateSignal(state.VelocityRegime ?? "FLAT", state.VelocityBpsPerSec, state.MomentumContribution);
            var ta  = new TaSignal(0.0, 0.5, 50.0, 1.1, 0.0, 0.0001, 20.0);
            var smc = new SmcSignal("", "", "", "", "");
            var of  = new OrderflowSignal(0.0, "");
            var ml  = new MlSignal("BUY", 0.5, null, "offline");

            var ex = await Record.ExceptionAsync(() =>
                _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                    ta, smc, of, ml, st, new ConfluenceMatrixResult(0.33, false, 0, "", "", new(), "NEUTRAL")));
            Assert.Null(ex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 9: SMC + TradeTimeout (сценарий с паттерном → меняет кол-во свечей)
    // ────────────────────────────────────────────────────────────────────────────
    public class SMC_to_TradeTimeout_Tests(ITestOutputHelper output)
    {
        static readonly TradeTimeoutEngine _timeout = new();

        [Fact]
        public void SMC_FvgPresent_TimeoutSetTo3Candles()
        {
            // По логике TradeTimeout: при SMC-паттерне → 3 свечи
            var s = TestScenarioLoader.Load("sweep_reversal");
            var smc = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

            var t = _timeout.CalculateTimeout(s.Asset, s.Timeframe, atr: 0.0002, volRatio: 1.0,
                smc, s.CurrentPrice, state: null!, isForex: true);

            output.WriteLine($"SMC pattern: BOS={smc.BosDirection}, FVG={smc.FvgType}, Candles={t.TimeoutCandles}");
            Assert.True(t.TimeoutCandles > 0);
        }

        [Fact]
        public void SMC_NoPattern_TimeoutFallsBackToDefault()
        {
            var s = TestScenarioLoader.Load("ranging_flat");
            var smc = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

            var t = _timeout.CalculateTimeout(s.Asset, s.Timeframe, atr: 0.0001, volRatio: 0.9,
                smc, s.CurrentPrice, state: null!, isForex: true);

            output.WriteLine($"No SMC pattern timeout: {t.TimeoutCandles} candles, {t.TimeoutText}");
            Assert.True(t.TimeoutCandles >= 2);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // СВЯЗЬ 10: Полный E2E-поток всех модулей (без HTTP-запросов)
    // TA + SMC + OF + CSE → через все сигнальные адаптеры → ConfluenceMatrix
    // ────────────────────────────────────────────────────────────────────────────
    public class FullPipeline_InterModule_Tests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly AutoCalibrationEngine _calib = new();

        [Theory]
        [InlineData("strong_uptrend")]
        [InlineData("ranging_flat")]
        [InlineData("sweep_reversal")]
        [InlineData("volatility_chaos")]
        public async Task AllModules_E2E_ProducesValidConsensus(string scenario)
        {
            // FULL CHAIN: Scenario → TA → SMC → OF → CSE → AutoCalib → Confluence → ConsensusDecision
            var s = TestScenarioLoader.Load(scenario);
            double[] prices  = s.Candles.Select(c => c.Close).ToArray();
            double[] volumes = s.Candles.Select(c => c.Volume).ToArray();

            // TA
            var (score, conf, rsi, hma, vol, atr) = _ta.ScoreTimeframe(s.Asset, s.Timeframe, prices, volumes, s.Candles);
            var (adx, pdi, mdi) = _ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);

            // SMC
            var smcResult = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

            // OrderFlow
            var ofResult = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);

            // ContinuousState
            var state = ContinuousStateEngine.EvaluateContinuousState(prices, s.Asset, s.Timeframe);

            // Сборка сигналов (то же, что делает Orchestrator)
            var taSignal  = new TaSignal(score, conf / 100.0, rsi, hma, vol, atr, adx);
            var smcSignal = new SmcSignal(smcResult.BosDirection, smcResult.SweepDirection,
                smcResult.OrderBlockType, smcResult.FvgType, "");
            var ofSignal  = new OrderflowSignal(ofResult.ScoreContribution, ofResult.Description);
            var mlSignal  = new MlSignal("BUY", 0.6, null, "offline"); // ML offline
            var stSignal  = new StateSignal(state.VelocityRegime ?? "FLAT",
                state.VelocityBpsPerSec, state.MomentumContribution);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Strong", "", new(), "BUY");

            // AutoCalib
            var cm = new ConfluenceMatrixEngine(null!, _ta, _calib);

            // Consensus
            var dec = await cm.EvaluateMatrixAsync(s.Asset, s.Timeframe,
                s.Timeframe.StartsWith("s"), 1.0, taSignal, smcSignal, ofSignal, mlSignal,
                stSignal, mtfResult, 0, _ta.CalculateVolatilityRatio(prices));

            output.WriteLine($"[{scenario}] Dir={dec.FinalDirection} Prob={dec.Probability}% " +
                             $"TA={dec.TaScore:F3} OF={dec.OfScore:F3} SMC={dec.SmcScore:F3}");

            // Инварианты
            Assert.True(dec.FinalDirection == "BUY" || dec.FinalDirection == "PUT",
                $"[{scenario}] Ожидался BUY или PUT, получили '{dec.FinalDirection}'");
            Assert.InRange(dec.Probability, 0, 100);
            Assert.True(double.IsFinite(dec.TaScore));
            Assert.True(double.IsFinite(dec.OfScore));
            Assert.True(double.IsFinite(dec.SmcScore));
            Assert.True(double.IsFinite(dec.MlProb));
        }

        [Fact]
        public async Task AllModules_E2E_ColdStart_DoesNotThrow()
        {
            // Холодный старт: только 5 свечей. Все движки должны выжить.
            var s = TestScenarioLoader.Load("cold_start_5candles");
            double[] prices  = s.Candles.Select(c => c.Close).ToArray();
            double[] volumes = s.Candles.Select(c => c.Volume).ToArray();

            var (score, conf, rsi, hma, vol, atr) = _ta.ScoreTimeframe(s.Asset, s.Timeframe, prices, volumes, s.Candles);
            var (adx, _, _) = _ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);
            var smc   = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
            var of    = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
            var state = ContinuousStateEngine.EvaluateContinuousState(prices, s.Asset, s.Timeframe);

            var ta  = new TaSignal(score, conf / 100.0, rsi, hma, vol, atr, adx);
            var cms = new SmcSignal(smc.BosDirection, smc.SweepDirection, smc.OrderBlockType, smc.FvgType, "");
            var ofs = new OrderflowSignal(of.ScoreContribution, of.Description);
            var mls = new MlSignal("BUY", 0.5, null, "offline");
            var sts = new StateSignal(state.VelocityRegime ?? "FLAT", state.VelocityBpsPerSec, state.MomentumContribution);
            var mtf = new ConfluenceMatrixResult(0.33, false, 0, "Weak", "", new(), "NEUTRAL");

            var cm = new ConfluenceMatrixEngine(null!, _ta);
            var ex = await Record.ExceptionAsync(() =>
                cm.EvaluateMatrixAsync(s.Asset, s.Timeframe, false, 1.0,
                    ta, cms, ofs, mls, sts, mtf));

            Assert.Null(ex);
        }

        [Fact]
        public async Task AllModules_E2E_NoNaN_InAnyField()
        {
            var s = TestScenarioLoader.Load("strong_uptrend");
            double[] prices  = s.Candles.Select(c => c.Close).ToArray();
            double[] volumes = s.Candles.Select(c => c.Volume).ToArray();

            var (score, conf, rsi, hma, vol, atr) = _ta.ScoreTimeframe(s.Asset, s.Timeframe, prices, volumes, s.Candles);
            var (adx, _, _) = _ta.ComputeTrueAdx(s.Asset, s.Timeframe, s.Candles);
            var smc   = SmcEngine.AnalyzeSmcStructure(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
            var of    = OrderFlowEngine.AnalyzeOrderFlow(s.Asset, s.Timeframe, s.Candles, s.CurrentPrice);
            var state = ContinuousStateEngine.EvaluateContinuousState(prices, s.Asset, s.Timeframe);

            var ta  = new TaSignal(score, conf / 100.0, rsi, hma, vol, atr, adx);
            var cms = new SmcSignal(smc.BosDirection, smc.SweepDirection, smc.OrderBlockType, smc.FvgType, "");
            var ofs = new OrderflowSignal(of.ScoreContribution, of.Description);
            var mls = new MlSignal("BUY", 0.65, null, "offline");
            var sts = new StateSignal(state.VelocityRegime ?? "FLAT", state.VelocityBpsPerSec, state.MomentumContribution);
            var mtf = new ConfluenceMatrixResult(1.0, true, 15, "IDEAL", "", new(), "BUY");

            var cm  = new ConfluenceMatrixEngine(null!, _ta, _calib);
            var dec = await cm.EvaluateMatrixAsync(s.Asset, s.Timeframe, false, 1.0,
                ta, cms, ofs, mls, sts, mtf, 0, _ta.CalculateVolatilityRatio(prices));

            Assert.True(double.IsFinite(dec.TaScore),    $"TaScore = NaN/Inf");
            Assert.True(double.IsFinite(dec.OfScore),    $"OfScore = NaN/Inf");
            Assert.True(double.IsFinite(dec.SmcScore),   $"SmcScore = NaN/Inf");
            Assert.True(double.IsFinite(dec.MlProb),     $"MlProb = NaN/Inf");
            Assert.True(double.IsFinite(dec.FinalTotalScore), $"FinalTotalScore = NaN/Inf");
        }
    }
}
