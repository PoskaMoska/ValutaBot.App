using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

namespace ValutaBot.Tests.Engines
{
    /// <summary>
    /// Диагностические тесты на симметрию и смещение направления.
    /// Проверяют: при прямо противоположных входных данных должно быть
    /// противоположное направление. Если нет — есть системный bias.
    /// </summary>
    public class DirectionBiasTests(ITestOutputHelper output)
    {
        static readonly TechnicalAnalysisEngine _ta = new();
        static readonly ConfluenceMatrixEngine _cm = new(null!, _ta);
        static readonly ConfluenceMatrixResult _neutralMtf = new(0.33, false, 0, "Weak", "NEUTRAL", new(), "NEUTRAL");

        // Строим 50 свечей с линейным ростом/падением
        static MiniAppController.OhlcCandle[] MakeCandles(double[] prices)
            => prices.Select((p, i) => new MiniAppController.OhlcCandle(
                i > 0 ? prices[i - 1] : p, p + 0.0002, p - 0.0002, p, 100,
                DateTime.UtcNow.AddSeconds(i - 50))).ToArray();

        // ── TEST 1: TA Score симметрия ─────────────────────────────────────────
        [Fact]
        public void TAScore_StrongUptrend_Positive_StrongDowntrend_Negative()
        {
            double[] upPrices   = Enumerable.Range(0, 50).Select(i => 1.0 + i * 0.001).ToArray();
            double[] downPrices = Enumerable.Range(0, 50).Select(i => 1.05 - i * 0.001).ToArray();
            double[] vols       = Enumerable.Repeat(100.0, 50).ToArray();

            var upRes   = _ta.ScoreTimeframe("BIAS_UP",   "m1", upPrices,   vols, MakeCandles(upPrices),
                adxOverride: 40, pdiOverride: 35, mdiOverride: 10);
            var downRes = _ta.ScoreTimeframe("BIAS_DOWN", "m1", downPrices, vols, MakeCandles(downPrices),
                adxOverride: 40, pdiOverride: 10, mdiOverride: 35);

            output.WriteLine($"Uptrend   → score={upRes.score:F4}, rsi={upRes.rsiVal:F1}");
            output.WriteLine($"Downtrend → score={downRes.score:F4}, rsi={downRes.rsiVal:F1}");
            output.WriteLine($"Asymmetry ratio |up|/|down| = {Math.Abs(upRes.score)/Math.Abs(downRes.score):F3} (ideal=1.0)");

            Assert.True(upRes.score > 0,   $"Uptrend score должен быть > 0, получен {upRes.score:F4}");
            Assert.True(downRes.score < 0, $"Downtrend score должен быть < 0, получен {downRes.score:F4}");
        }

        // ── TEST 1b: Изоляция HMA — диагностика источника асимметрии ─────────────
        [Fact]
        public void TAScore_HmaIsolation_SymmetricForUptrendAndDowntrend()
        {
            double[] upPrices   = Enumerable.Range(0, 50).Select(i => 1.0 + i * 0.001).ToArray();
            double[] downPrices = Enumerable.Range(0, 50).Select(i => 1.05 - i * 0.001).ToArray();
            double[] zeroVols   = Enumerable.Repeat(0.0, 50).ToArray();

            // Добавляем VolatilityRatio для диагностики chaos override
            double upVolRatio   = _ta.CalculateVolatilityRatio(upPrices);
            double downVolRatio = _ta.CalculateVolatilityRatio(downPrices);
            output.WriteLine($"VolatilityRatio: up={upVolRatio:F4}, down={downVolRatio:F4}  (>1.5 = chaos)");

            var upRes   = _ta.ScoreTimeframe("HMA_ISO_UP3",   "m1", upPrices,   zeroVols, MakeCandles(upPrices),
                adxOverride: 40, pdiOverride: 25, mdiOverride: 25);
            var downRes = _ta.ScoreTimeframe("HMA_ISO_DOWN3", "m1", downPrices, zeroVols, MakeCandles(downPrices),
                adxOverride: 40, pdiOverride: 25, mdiOverride: 25);

            output.WriteLine($"Up:   score={upRes.score:F4}  hma={upRes.hmaVal:F6}  rsi={upRes.rsiVal:F1}  lastP={upPrices[^1]:F6}  diff={upPrices[^1]-upRes.hmaVal:F6}");
            output.WriteLine($"Down: score={downRes.score:F4} hma={downRes.hmaVal:F6} rsi={downRes.rsiVal:F1} lastP={downPrices[^1]:F6} diff={downPrices[^1]-downRes.hmaVal:F6}");

            // Pre-tanh оценки (обратное применение tanh)
            output.WriteLine($"Pre-tanh Up:   {Math.Atanh(upRes.score):F4}");
            output.WriteLine($"Pre-tanh Down: {Math.Atanh(downRes.score):F4}");
        }

        // ── TEST 2: Consensus — при ALL-BUY должен быть BUY, при ALL-PUT → PUT ──
        [Fact]
        public async Task Consensus_AllBuySignals_ReturnsBuy_AllPutSignals_ReturnsPut()
        {
            var taB = new TaSignal(0.85, 0.85, 70.0, 1.1, 0.3, 0.0005, 35.0);
            var taP = new TaSignal(-0.85, 0.85, 30.0, 0.9, -0.3, 0.0005, 35.0);
            var smcB = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "", "", "");
            var smcP = new SmcSignal("BEARISH_BOS", "BEARISH_SWEEP", "", "", "");
            var ofB  = new OrderflowSignal(0.35, "buying");
            var ofP  = new OrderflowSignal(-0.35, "selling");
            var mlB  = new MlSignal("BUY", 0.80, null, "v1");
            var mlP  = new MlSignal("PUT", 0.80, null, "v1");
            var st   = new StateSignal("STABLE", 0.0, 0.0);

            var dB = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                taB, smcB, ofB, mlB, st, _neutralMtf);
            var dP = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                taP, smcP, ofP, mlP, st, _neutralMtf);

            output.WriteLine($"ALL-BUY  → dir={dB.FinalDirection}, prob={dB.Probability}%");
            output.WriteLine($"ALL-PUT  → dir={dP.FinalDirection}, prob={dP.Probability}%");

            Assert.Equal("BUY", dB.FinalDirection);
            Assert.Equal("PUT", dP.FinalDirection);
        }

        // ── TEST 3: Consensus — направление различается при противоположных входах? ──
        [Fact]
        public async Task Consensus_OppositeInputs_ProduceDifferentDirections()
        {
            var taB = new TaSignal(0.85, 0.85, 70.0, 1.1, 0.3, 0.0005, 35.0);
            var taP = new TaSignal(-0.85, 0.85, 30.0, 0.9, -0.3, 0.0005, 35.0);
            var smcN = new SmcSignal("", "", "", "", "");
            var ofN  = new OrderflowSignal(0.0, "flat");
            var mlB  = new MlSignal("BUY", 0.80, null, "v1");
            var mlP  = new MlSignal("PUT", 0.80, null, "v1");
            var st   = new StateSignal("STABLE", 0.0, 0.0);

            var dB = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                taB, smcN, ofN, mlB, st, _neutralMtf);
            var dP = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                taP, smcN, ofN, mlP, st, _neutralMtf);

            output.WriteLine($"TA=+0.85 ML=BUY0.8 → dir={dB.FinalDirection}, prob={dB.Probability}%");
            output.WriteLine($"TA=-0.85 ML=PUT0.8 → dir={dP.FinalDirection}, prob={dP.Probability}%");

            Assert.True(dB.FinalDirection != dP.FinalDirection,
                $"Противоположные входы должны давать противоположные направления. Получено: оба = {dB.FinalDirection}");
        }

        // ── TEST 4: MetaLearner стартовый bias ───────────────────────────────────
        [Fact]
        public void MetaLearner_FreshInstance_NeutralInputGivesNearFifty()
        {
            var meta = new OnlineMetaLearner();

            double probNeutral = meta.Predict("DIAG_FRESH", "m1", 0.0, 0.0, 0.0, 0.0, false);
            double probBuy     = meta.Predict("DIAG_FRESH", "m1", 0.3, 0.3, 0.3, 0.3, false);
            double probPut     = meta.Predict("DIAG_FRESH", "m1", -0.3, -0.3, -0.3, -0.3, false);

            output.WriteLine($"Neutral  (0,0,0,0)    → prob={probNeutral:F4}  (expected ~0.5)");
            output.WriteLine($"Moderate BUY (+0.3ea) → prob={probBuy:F4}    (expected > 0.5)");
            output.WriteLine($"Moderate PUT (-0.3ea) → prob={probPut:F4}    (expected < 0.5)");
            output.WriteLine($"Bias at neutral: {Math.Abs(probNeutral - 0.5):F4}");

            // Нейтральный вход должен давать ~0.5 (допуск ±0.1)
            Assert.True(Math.Abs(probNeutral - 0.5) < 0.1,
                $"MetaLearner bias слишком большой: при нейтральных входах prob={probNeutral:F4} (ожидалось 0.4-0.6)");

            // Симметрия: BUY > 0.5, PUT < 0.5
            Assert.True(probBuy > 0.5, $"При BUY сигналах prob={probBuy:F4} должна быть > 0.5");
            Assert.True(probPut < 0.5, $"При PUT сигналах prob={probPut:F4} должна быть < 0.5");
        }

        // ── TEST 5: При нулевой марже (prob≈0.5) — бот всегда даёт 50% ──────────
        [Fact]
        public async Task Consensus_WhenAllNeutral_ProbabilityIsExactly50()
        {
            // Все сигналы нейтральные → MetaFallback + никаких штрафов → ожидаем 50%
            var ta0  = new TaSignal(0.0, 0.5, 50.0, 1.0, 0.0, 0.0001, 20.0);
            var smc0 = new SmcSignal("", "", "", "", "");
            var of0  = new OrderflowSignal(0.0, "flat");
            var ml0  = new MlSignal("NEUTRAL", 0.5, null, "offline");
            var st0  = new StateSignal("STABLE", 0.0, 0.0);

            var d = await _cm.EvaluateMatrixAsync("EUR/USD", "m1", false, 1.0,
                ta0, smc0, of0, ml0, st0, _neutralMtf);

            output.WriteLine($"All-neutral → dir={d.FinalDirection}, prob={d.Probability}%");
            // Направление неважно (бот обязан дать сигнал), но вероятность должна быть ровно 50
            Assert.Equal(50, d.Probability);
        }
    }
}
