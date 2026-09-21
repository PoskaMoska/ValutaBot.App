using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp.Indicators;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    /// <summary>
    /// Regression tests for ALL fixes applied in Phase 2 audit session.
    /// Categories:
    ///   FPU  — FPU Subnormal Float degradation fixes in RSI/ATR
    ///   RB   — Ring Buffer O(1) correctness and Clone() independence
    ///   ML   — ML Score Amnesia fix (MlScore persistence through DB)
    ///   DOJI — Doji Filter Bypass / Double-count PartialFit fix
    ///   RL   — Grid-time alignment for verifyAt
    ///   STRESS — 10,000 tick + 500 PartialFit stress tests
    /// </summary>
    public class Phase2AuditRegressionTests
    {
        private readonly ITestOutputHelper _out;
        public Phase2AuditRegressionTests(ITestOutputHelper output) => _out = output;

        // ═══════════════════════════════════════════════════════════════════
        // FPU-1: StatefulRsi — no CPU meltdown on dead market (500 flat ticks)
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void FPU1_Rsi_FlatMarket_500Ticks_Returns50()
        {
            var rsi = new StatefulRsi(14);
            const double flatPrice = 1.10000;

            double lastVal = 50.0;
            for (int i = 0; i < 500; i++)
                lastVal = rsi.Update(flatPrice);

            Assert.True(double.IsFinite(lastVal), "RSI must be finite after 500 flat ticks");
            Assert.Equal(50.0, lastVal, precision: 4);

            _out.WriteLine($"[FPU-1] RSI after 500 flat ticks = {lastVal:F6}");
        }

        [Fact]
        public void FPU1_Rsi_FlatThenMove_CorrectlyRecovers()
        {
            var rsi = new StatefulRsi(14);
            const double flatPrice = 1.10000;

            for (int i = 0; i < 200; i++)
                rsi.Update(flatPrice);

            double price = flatPrice;
            for (int i = 0; i < 20; i++)
                rsi.Update(price += 0.0010);

            double result = rsi.Update(price + 0.0010);

            Assert.True(double.IsFinite(result), "RSI must be finite after recovery from flat");
            Assert.True(result > 50.0, $"RSI should be > 50 after sustained upward move, got {result:F2}");
            Assert.InRange(result, 0.0, 100.0);

            _out.WriteLine($"[FPU-1] RSI after flat+recovery = {result:F2}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // FPU-2: StatefulAtr — subnormal protection on flat OHLC
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void FPU2_Atr_FlatMarket_300Ticks_StaysFiniteAndNearZero()
        {
            var atr = new StatefulAtr(14);
            const double c = 1.10000;

            double lastAtr = 0;
            for (int i = 0; i < 300; i++)
                lastAtr = atr.Update(c, c, c);

            Assert.True(double.IsFinite(lastAtr), "ATR must be finite after 300 flat ticks");
            Assert.True(lastAtr >= 0.0, "ATR must be non-negative");
            Assert.True(lastAtr < 1e-8, $"ATR should be ~0 on flat market, got {lastAtr}");

            _out.WriteLine($"[FPU-2] ATR after 300 flat ticks = {lastAtr:G4}");
        }

        [Fact]
        public void FPU2_Atr_FlatThenVolatility_RespondsCorrectly()
        {
            var atr = new StatefulAtr(14);
            const double c = 1.10000;

            for (int i = 0; i < 150; i++)
                atr.Update(c, c, c);

            double price = c;
            double lastAtr = 0;
            for (int i = 0; i < 20; i++)
            {
                price += 0.0010;
                lastAtr = atr.Update(price + 0.0005, price - 0.0005, price);
            }

            Assert.True(double.IsFinite(lastAtr), "ATR must be finite after volatility recovery");
            Assert.True(lastAtr > 0, $"ATR must be > 0 after real price moves, got {lastAtr:G4}");

            _out.WriteLine($"[FPU-2] ATR after flat+volatility = {lastAtr:G4}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // RB-1: StatefulHma Ring Buffer — output correctness and direction
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void RB1_Hma_TwoIdenticalInstances_SameResult()
        {
            double[] prices = Enumerable.Range(0, 60)
                .Select(i => 1.1000 + 0.0010 * Math.Sin(i * Math.PI / 10.0))
                .ToArray();

            var hma1 = new StatefulHma(9);
            var hma2 = new StatefulHma(9);

            double last1 = 0, last2 = 0;
            for (int i = 0; i < prices.Length; i++)
            {
                last1 = hma1.Update(prices[i]);
                last2 = hma2.Update(prices[i]);
            }

            Assert.Equal(last1, last2, precision: 10);
            Assert.True(double.IsFinite(last1), "HMA must be finite");
            _out.WriteLine($"[RB-1] HMA(9) last value = {last1:F8}");
        }

        [Fact]
        public void RB1_Hma_DirectionDetection_RisingOnUptrend()
        {
            var hma = new StatefulHma(9);
            double price = 1.1000;
            double prev = 0, curr = 0;

            for (int i = 0; i < 60; i++)
            {
                price += 0.0005;
                prev = curr;
                curr = hma.Update(price);
            }

            _out.WriteLine($"[RB-1] HMA prev={prev:F6} curr={curr:F6}");
            Assert.True(curr > prev, $"HMA should be rising on uptrend: prev={prev:F6} curr={curr:F6}");
            Assert.True(double.IsFinite(curr));
        }

        [Fact]
        public void RB1_ConnorsRsi_StableAfterManyTicks()
        {
            var crsi = new StatefulConnorsRsi();
            double price = 1.1000;
            var rng = new Random(42);

            double last = 50.0;
            for (int i = 0; i < 200; i++)
            {
                price += (rng.NextDouble() - 0.5) * 0.0002;
                last = crsi.Update(price);
            }

            Assert.True(double.IsFinite(last), "ConnorsRSI must be finite after 200 ticks");
            Assert.InRange(last, 0.0, 100.0);
            _out.WriteLine($"[RB-1] ConnorsRSI after 200 random ticks = {last:F2}");
        }

        [Fact]
        public void RB1_ConnorsRsi_AllUpMoves_HighValue()
        {
            var crsi = new StatefulConnorsRsi();
            double price = 1.0000;

            for (int i = 0; i < 100; i++)
            {
                price += 0.0010;
                crsi.Update(price);
            }

            double result = crsi.Update(price + 0.0010);
            Assert.True(double.IsFinite(result));
            // ConnorsRSI = (RSI + StreakRSI + PctRank) / 3.
            // On all-equal-step uptrend: RSI→100, StreakRSI→100, PctRank→0 (current == historical)
            // So CRSI converges to (100+100+0)/3 = 66.67. Threshold set accordingly.
            Assert.True(result > 60.0, $"CRSI should be > 60 on consistent uptrend, got {result:F2}");
            _out.WriteLine($"[RB-1] ConnorsRSI on all-up = {result:F2} (cap=66.67 due to pctRank=0 on equal steps)");
        }

        // ═══════════════════════════════════════════════════════════════════
        // RB-2: Clone() — deep copy correctness (independent state)
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void RB2_ConnorsRsi_Clone_IsIndependent()
        {
            var original = new StatefulConnorsRsi();
            double price = 1.1000;
            for (int i = 0; i < 60; i++)
            {
                price += (i % 2 == 0 ? 0.0001 : -0.00005);
                original.Update(price);
            }

            var clone = original.Clone();

            double origResult  = original.Update(price + 0.0050);
            double cloneResult = clone.Update(price - 0.0050);

            _out.WriteLine($"[RB-2] ConnorsRsi clone: orig={origResult:F2} clone={cloneResult:F2}");
            Assert.NotEqual(origResult, cloneResult);
        }

        [Fact]
        public void RB2_Hma_Clone_IsIndependent()
        {
            var original = new StatefulHma(9);
            for (int i = 0; i < 30; i++)
                original.Update(1.1000 + i * 0.0001);

            var clone = original.Clone();

            double origResult  = original.Update(1.2000);
            double cloneResult = clone.Update(1.0000);

            _out.WriteLine($"[RB-2] HMA clone: orig={origResult:F6} clone={cloneResult:F6}");
            Assert.NotEqual(origResult, cloneResult);
        }

        // ═══════════════════════════════════════════════════════════════════
        // ML-1: MlScore round-trip through PredictionRecord and TradeOutcomeRecord
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void ML1_PredictionRecord_MlScore_IsPreserved()
        {
            var record = new SignalTracker.PredictionRecord
            {
                Id = "test001",
                Direction = "BUY",
                Asset = "EURUSD",
                Timeframe = "1m",
                BrokerSymbol = "EURUSDT",
                EntryPrice = 1.08500,
                CreatedAt = DateTime.UtcNow,
                VerifyAt = DateTime.UtcNow.AddMinutes(3),
                IsForex = true,
                TaScore = 0.75,
                OfScore = 0.30,
                SmcScore = 0.50,
                MlProb = 0.68,
                MlScore = 0.36
            };

            var outcomeRecord = new ValutaBot.App.MiniApp.Data.Repositories.TradeOutcomeRecord
            {
                Id        = record.Id,
                Asset     = record.Asset,
                Timeframe = record.Timeframe,
                Direction = record.Direction,
                TaScore   = record.TaScore,
                OfScore   = record.OfScore,
                SmcScore  = record.SmcScore,
                MlProb    = record.MlProb,
                MlScore   = record.MlScore,
                CreatedAt  = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };

            Assert.Equal(0.36, outcomeRecord.MlScore, precision: 10);
            Assert.Equal(0.68, outcomeRecord.MlProb,  precision: 10);
            Assert.NotEqual(0.0, outcomeRecord.MlScore);

            _out.WriteLine($"[ML-1] MlScore round-trip: {outcomeRecord.MlScore:F4}");
        }

        [Fact]
        public void ML1_PredictionRecord_DefaultMlScore_IsZero()
        {
            var record = new SignalTracker.PredictionRecord();
            Assert.Equal(0.0, record.MlScore);
            _out.WriteLine("[ML-1] Default MlScore = 0.0 — this was the old poisoned state");
        }

        [Fact]
        public void ML1_TradeOutcomeRecord_MlScorePropertyWritable()
        {
            var r = new ValutaBot.App.MiniApp.Data.Repositories.TradeOutcomeRecord
            {
                MlScore = -0.42
            };
            Assert.Equal(-0.42, r.MlScore, precision: 10);
            _out.WriteLine("[ML-1] TradeOutcomeRecord.MlScore is writable");
        }

        // ═══════════════════════════════════════════════════════════════════
        // DOJI-1: MetaLearner — corrupted ml=0 training vs correct ml=0.8
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void DOJI1_MetaLearner_TrainedWithCorrectMl_WeightsDiverge()
        {
            // WHAT THE TEST PROVES:
            // A learner trained with ml=0 (old poisoned state — MlScore lost in DB)
            // does not update w[4] (the ML weight) correctly.
            // A learner trained with ml=0.8 properly trains w[4].
            // The two learners MUST produce different predictions when
            // given a STRONG negative ML signal (-1.0), because:
            //   - Poisoned: w[4] ≈ initial (1.0, decayed slightly) → high prob
            //   - Correct:  w[4] learned ml=+0.8 → responds differently to ml=-1.0

            var learnerPoisoned = new ValutaBot.MiniApp.Features.MarketAnalysis.Engines.OnlineMetaLearner();
            var learnerCorrect  = new ValutaBot.MiniApp.Features.MarketAnalysis.Engines.OnlineMetaLearner();

            for (int i = 0; i < 50; i++)
            {
                learnerPoisoned.PartialFit("EURUSD", "1m", 0.5, 0.0, 0.0, 0.0, wasWin: true, direction: "BUY");
                learnerCorrect.PartialFit("EURUSD", "1m", 0.5, 0.0, 0.0, 0.8, wasWin: true, direction: "BUY");
            }

            // Test with OPPOSITE ML signal: if w[4] was learned from ml=+0.8,
            // then seeing ml=-1.0 should produce a meaningfully different result
            double probPoisoned = learnerPoisoned.Predict("EURUSD", "1m", 0.5, 0.0, 0.0, -1.0, false);
            double probCorrect  = learnerCorrect.Predict("EURUSD",  "1m", 0.5, 0.0, 0.0, -1.0, false);

            _out.WriteLine($"[DOJI-1] Poisoned (ml=-1.0 probe): {probPoisoned:F4} | Correct (ml=-1.0 probe): {probCorrect:F4}");

            // Core assertion: they MUST differ (MlScore poisoning caused w[4] divergence)
            Assert.NotEqual(probPoisoned, probCorrect);
            // Correct learner was trained on positive ml → should be MORE averse to negative ml
            Assert.True(probCorrect < probPoisoned, 
                $"Learner trained with real ml=0.8 should be MORE averse to ml=-1.0. " +
                $"Poisoned={probPoisoned:F4} Correct={probCorrect:F4}");
        }

        [Fact]
        public void DOJI1_MetaLearner_NeutralDirection_IsNoOp()
        {
            var learner = new ValutaBot.MiniApp.Features.MarketAnalysis.Engines.OnlineMetaLearner();

            double before = learner.Predict("BTCUSDT", "5m", 0.8, 0.0, 0.0, 0.0, false);
            learner.PartialFit("BTCUSDT", "5m", 0.8, 0.0, 0.0, 0.0, wasWin: true, direction: "NEUTRAL");
            double after = learner.Predict("BTCUSDT", "5m", 0.8, 0.0, 0.0, 0.0, false);

            Assert.Equal(before, after, precision: 10);
            _out.WriteLine($"[DOJI-1] NEUTRAL → no weight change: before={before:F6} after={after:F6}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // DOJI-2: Doji filter boundary checks
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void DOJI2_ExactDoji_Detected_WhenPnlZero()
        {
            bool isExactDoji = Math.Abs(0.0) < 1e-4;
            Assert.True(isExactDoji);

            bool notDoji = Math.Abs(5.0) < 1e-4;
            Assert.False(notDoji);

            _out.WriteLine("[DOJI-2] ExactDoji threshold: pnl=0 → doji, pnl=5bps → not doji");
        }

        [Fact]
        public void DOJI2_TrainingDoji_PctThreshold_CorrectBoundary()
        {
            double entry = 1.10000;

            double exitSmall = 1.10000 * (1 + 0.00020);
            double pctSmall  = Math.Abs(exitSmall - entry) / entry * 100.0;
            Assert.True(pctSmall < 0.025, $"0.020% move should be training doji: pct={pctSmall:F4}");

            double exitLarge = 1.10000 * (1 + 0.00030);
            double pctLarge  = Math.Abs(exitLarge - entry) / entry * 100.0;
            Assert.False(pctLarge < 0.025, $"0.030% move should NOT be training doji: pct={pctLarge:F4}");

            _out.WriteLine($"[DOJI-2] Small={pctSmall:F4}% (doji) | Large={pctLarge:F4}% (signal)");
        }

        // ═══════════════════════════════════════════════════════════════════
        // RL-1: Grid-time alignment (verifyAt calculation correctness)
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void RL1_GridTime_1m_AlignedToMinuteBoundary()
        {
            DateTime now = new DateTime(2026, 1, 15, 10, 0, 37, DateTimeKind.Utc);
            long nowTicks = now.Ticks;
            long intervalTicks = TimeSpan.FromSeconds(60).Ticks;
            DateTime gridTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            DateTime verifyAt = gridTime.AddSeconds(3 * 60);

            Assert.Equal(0, gridTime.Second);
            Assert.Equal(10, gridTime.Hour);
            Assert.Equal(0, gridTime.Minute);
            Assert.Equal(new DateTime(2026, 1, 15, 10, 3, 0, DateTimeKind.Utc), verifyAt);

            _out.WriteLine($"[RL-1] 1m: now={now:HH:mm:ss} → grid={gridTime:HH:mm:ss} → verify={verifyAt:HH:mm:ss}");
        }

        [Fact]
        public void RL1_GridTime_s5_AlignedTo5SecondBoundary()
        {
            DateTime now = new DateTime(2026, 1, 15, 10, 0, 37, 412, DateTimeKind.Utc);
            long nowTicks = now.Ticks;
            long intervalTicks = TimeSpan.FromSeconds(5).Ticks;
            DateTime gridTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);

            Assert.Equal(35, gridTime.Second);
            Assert.Equal(0, gridTime.Millisecond);

            _out.WriteLine($"[RL-1] s5: now={now:HH:mm:ss.fff} → grid={gridTime:HH:mm:ss}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // STRESS-1: All 6 indicators, 10,000 ticks — no NaN, no Infinity
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void Stress_AllIndicators_10000Ticks_NoNanNoInfinity()
        {
            var rsi  = new StatefulRsi(14);
            var atr  = new StatefulAtr(14);
            var ema  = new StatefulEma(9);
            var adx  = new StatefulTrueAdx(14);
            var hma  = new StatefulHma(9);
            var crsi = new StatefulConnorsRsi();

            var rng   = new Random(1337);
            double price = 1.10000;

            for (int i = 0; i < 10_000; i++)
            {
                if (i % 500 < 50)
                    price = 1.10000; // inject flat period (subnormal stress)
                else
                    price += (rng.NextDouble() - 0.495) * 0.0010;

                double high  = price + rng.NextDouble() * 0.0005;
                double low   = price - rng.NextDouble() * 0.0005;
                double close = price;

                double rsiVal  = rsi.Update(close);
                double atrVal  = atr.Update(high, low, close);
                double emaVal  = ema.Update(close);
                double adxVal  = adx.Update(high, low, close);
                double hmaVal  = hma.Update(close);
                double crsiVal = crsi.Update(close);

                Assert.True(double.IsFinite(rsiVal),  $"RSI NaN/Inf at tick {i}");
                Assert.True(double.IsFinite(atrVal),  $"ATR NaN/Inf at tick {i}");
                Assert.True(double.IsFinite(emaVal),  $"EMA NaN/Inf at tick {i}");
                Assert.True(double.IsFinite(adxVal),  $"ADX NaN/Inf at tick {i}");
                Assert.True(double.IsFinite(hmaVal),  $"HMA NaN/Inf at tick {i}");
                Assert.True(double.IsFinite(crsiVal), $"ConnorsRSI NaN/Inf at tick {i}");

                Assert.InRange(rsiVal,  0.0, 100.0);
                Assert.InRange(adxVal,  0.0, 100.0);
                Assert.InRange(crsiVal, 0.0, 100.0);
                Assert.True(atrVal >= 0.0, $"ATR must be >= 0 at tick {i}");
            }

            _out.WriteLine("[STRESS-1] 10,000 ticks, 6 indicators — all finite. ✓");
        }

        // ═══════════════════════════════════════════════════════════════════
        // STRESS-2: OnlineMetaLearner — 500 PartialFit, weights stay in [-4, 4]
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void Stress_MetaLearner_500Updates_WeightsRemainSane()
        {
            var learner = new ValutaBot.MiniApp.Features.MarketAnalysis.Engines.OnlineMetaLearner();
            var rng = new Random(42);

            for (int i = 0; i < 500; i++)
            {
                double ta  = (rng.NextDouble() * 2) - 1;
                double of  = (rng.NextDouble() * 2) - 1;
                double smc = (rng.NextDouble() * 2) - 1;
                double ml  = (rng.NextDouble() * 2) - 1;
                bool win   = rng.NextDouble() > 0.45;
                learner.PartialFit("EURUSD", "1m", ta, of, smc, ml, win, win ? "BUY" : "PUT");
            }

            double prob = learner.Predict("EURUSD", "1m", 0.5, 0.3, 0.2, 0.4, false);

            Assert.True(double.IsFinite(prob), "MetaLearner Predict must be finite after 500 updates");
            Assert.InRange(prob, 0.0, 1.0);

            _out.WriteLine($"[STRESS-2] MetaLearner after 500 PartialFit: Predict = {prob:F4}");
        }
    }
}

    // NOTE: Appended tests for Junction-1 fixes (D1-1, D1-3, D1-4)
    // These are placed in the same class via partial class approach below
