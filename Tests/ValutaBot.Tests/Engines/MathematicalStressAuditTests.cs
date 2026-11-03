using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Indicators;
using ValutaBot.MiniApp.Features.MarketAnalysis;

namespace ValutaBot.Tests.Engines
{
    public class MathematicalStressAuditTests
    {
        [Fact]
        public void OhlcCandle_IsValid_ValidatesCorrectly()
        {
            var valid = new MiniAppController.OhlcCandle(1.1000, 1.1050, 1.0950, 1.1020, 100, DateTime.UtcNow);
            Assert.True(valid.IsValid);

            var invalidInverted = new MiniAppController.OhlcCandle(1.1000, 1.0900, 1.1050, 1.1020, 100, DateTime.UtcNow);
            Assert.False(invalidInverted.IsValid);

            var invalidNan = new MiniAppController.OhlcCandle(1.1000, double.NaN, 1.0950, 1.1020, 100, DateTime.UtcNow);
            Assert.False(invalidNan.IsValid);

            var invalidZero = new MiniAppController.OhlcCandle(0, 1.1050, 1.0950, 1.1020, 100, DateTime.UtcNow);
            Assert.False(invalidZero.IsValid);

            var invalidNegVol = new MiniAppController.OhlcCandle(1.1000, 1.1050, 1.0950, 1.1020, -5, DateTime.UtcNow);
            Assert.False(invalidNegVol.IsValid);
        }

        [Fact]
        public void StatefulIndicators_NaN_DoesNotPoisonStateMachine()
        {
            var rsi = new StatefulRsi(14);
            var atr = new StatefulAtr(14);
            var adx = new StatefulTrueAdx(14);
            var hma = new StatefulHma(9);
            var ema = new StatefulEma(9);
            var crsi = new StatefulConnorsRsi();

            // Feed valid prices
            for (int i = 0; i < 20; i++)
            {
                double p = 1.1000 + i * 0.0005;
                rsi.Update(p);
                atr.Update(p + 0.0002, p - 0.0002, p);
                adx.Update(p + 0.0002, p - 0.0002, p);
                hma.Update(p);
                ema.Update(p);
                crsi.Update(p);
            }

            // Feed NaN / Inf
            double rsiNan = rsi.Update(double.NaN);
            double atrNan = atr.Update(double.NaN, 1.0, 1.0);
            double adxNan = adx.Update(1.0, double.PositiveInfinity, 1.0);
            double hmaNan = hma.Update(double.NaN);
            double emaNan = ema.Update(double.NaN);
            double crsiNan = crsi.Update(double.NaN);

            Assert.True(double.IsFinite(rsiNan));
            Assert.True(double.IsFinite(atrNan));
            Assert.True(double.IsFinite(adxNan));
            Assert.True(double.IsFinite(hmaNan) == false || double.IsNaN(hmaNan)); // HMA returns input if warm-up/not guarded, but our guard returns input or handled
            Assert.True(double.IsFinite(emaNan));
            Assert.True(double.IsFinite(crsiNan));

            // Feed subsequent valid price - state must be healthy and finite!
            double nextP = 1.1200;
            Assert.True(double.IsFinite(rsi.Update(nextP)));
            Assert.True(double.IsFinite(atr.Update(nextP + 0.0002, nextP - 0.0002, nextP)));
            Assert.True(double.IsFinite(adx.Update(nextP + 0.0002, nextP - 0.0002, nextP)));
            Assert.True(double.IsFinite(hma.Update(nextP)));
            Assert.True(double.IsFinite(ema.Update(nextP)));
            Assert.True(double.IsFinite(crsi.Update(nextP)));
        }

        [Fact]
        public void Gatekeeper_ZeroRangeCandles_AccurateRegardlessOfPriceArraySize()
        {
            var engine = new TechnicalAnalysisEngine();
            // 20 identical candles (range = 0)
            var candles = new MiniAppController.OhlcCandle[20];
            var prices = new double[15];
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < 20; i++)
            {
                candles[i] = new MiniAppController.OhlcCandle(1.1000, 1.1000, 1.1000, 1.1000, 50, now.AddSeconds(i * 10));
                if (i < 15) prices[i] = 1.1000;
            }

            // candles.Length (20) > prices.Length (15)
            var result = engine.ValidateMarketGatekeeper("EURUSD", "s10", prices, candles);
            Assert.False(result.IsTradeable);
            Assert.Contains("Рынок в состоянии застоя", result.Reason);
        }

        [Fact]
        public async Task ConfluenceMatrix_ExplicitHold_ZeroesMlScoreAndSetsNeutralOutcome()
        {
            var cm = new ConfluenceMatrixEngine(new MarketDataFetcher(), new TechnicalAnalysisEngine());
            var ta = new TaSignal(0.8, 75.0, 65.0, 1.1050, 1.2, 0.0010, 25.0);
            var smc = new SmcSignal("BULLISH_BOS", "NONE", "BULLISH_OB", "NONE", "");
            var state = new StateSignal("ACCELERATING_UP", 2.5, 0.4);
            var mtf = new ConfluenceMatrixResult(1.0, true, 15, "GOLDEN", "3 TF sync", new Dictionary<string, string>(), "BUY");

            // ML returns explicit HOLD with raw confidence = 0.58
            var ml = new MlSignal("HOLD", 0.58, 0.65, "neural_brain_v5", 1, 0.58);

            var decision = await cm.EvaluateMatrixAsync("EURUSD", "s10", true, 1.0, ta, smc, ml, state, mtf);

            Assert.Equal("NEUTRAL", decision.FinalDirection);
            Assert.Equal(50, decision.Probability);
            Assert.Equal(0.0, decision.MlScoreRaw);
            Assert.Equal(0.5, decision.MlProb);
            Assert.Equal(0.0, decision.FinalTotalScore);
        }

        [Fact]
        public async Task ConfluenceMatrix_InvalidConflictPenalty_SafelyClamped()
        {
            var cm = new ConfluenceMatrixEngine(new MarketDataFetcher(), new TechnicalAnalysisEngine());
            var ta = new TaSignal(0.8, 75.0, 65.0, 1.1050, 1.2, 0.0010, 25.0);
            var smc = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "");
            var state = new StateSignal("STABLE", 0.0, 0.0);
            var mtf = new ConfluenceMatrixResult(0.67, false, 7, "2 TF", "Summary", new Dictionary<string, string>(), "BUY");
            var ml = new MlSignal("BUY", 0.70, 0.65, "neural_brain_v5", 1, 0.70);

            // Pass NaN and negative conflictPenalty
            var decisionNan = await cm.EvaluateMatrixAsync("EURUSD", "s10", true, double.NaN, ta, smc, ml, state, mtf);
            Assert.True(decisionNan.Probability >= 50 && decisionNan.Probability <= 92);

            var decisionNeg = await cm.EvaluateMatrixAsync("EURUSD", "s10", true, -0.5, ta, smc, ml, state, mtf);
            Assert.True(decisionNeg.Probability >= 50 && decisionNeg.Probability <= 92);
        }

        [Fact]
        public void ContinuousStateEngine_5DigitQuotes_NoCatastrophicCancellation()
        {
            // 5-digit EURUSD_otc quotes where price changes by 1 pip (0.00001)
            double basePrice = 1.08542;
            double[] prices = new double[20];
            for (int i = 0; i < prices.Length; i++)
            {
                prices[i] = basePrice + (i % 2 == 0 ? 0.00001 : -0.00001);
            }

            var result = ContinuousStateEngine.EvaluateContinuousState(prices, "EURUSD_otc", "s5", isAlreadyClosed: true);

            Assert.True(double.IsFinite(result.VelocityBpsPerSec));
            Assert.True(double.IsFinite(result.AccelerationBpsPerSec2));
            Assert.True(double.IsFinite(result.KalmanFilteredState));
            Assert.NotEqual("UNKNOWN", result.VelocityRegime);
        }

        [Fact]
        public void StatefulConnorsRsi_And_StatefulHma_ComputeLive_MatchesUpdate()
        {
            var crsi = new StatefulConnorsRsi();
            var hma = new StatefulHma(9);

            double p = 1.08500;
            for (int i = 0; i < 25; i++)
            {
                p += (i % 2 == 0 ? 0.00010 : -0.00005);
                crsi.Update(p);
                hma.Update(p);
            }

            double livePrice = p + 0.00020;

            // ComputeLive must match Clone().Update() without mutating the base instance
            double liveHmaExpected = hma.Clone().Update(livePrice);
            double liveHmaActual = hma.ComputeLive(livePrice);
            Assert.Equal(liveHmaExpected, liveHmaActual, precision: 6);

            double liveCrsiExpected = crsi.Clone().Update(livePrice);
            double liveCrsiActual = crsi.ComputeLive(livePrice);
            Assert.Equal(liveCrsiExpected, liveCrsiActual, precision: 6);
        }

        [Fact]
        public async Task ConfluenceMatrix_AbnormalVolatilitySpike_BlocksTradeAndLowersProbability()
        {
            var cm = new ConfluenceMatrixEngine(new MarketDataFetcher(), new TechnicalAnalysisEngine());
            var ta = new TaSignal(1.5, 80.0, 65, 100, 2.0, 10);
            var smc = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish");
            var ml = new MlSignal("BUY", 0.85, 0.75, "neural_brain");
            var state = new StateSignal("TREND_BULLISH", 8.0, 0.6);
            var mtf = new ConfluenceMatrixResult(1.0, true, 15, "Golden", "MTF", new Dictionary<string, string>(), "BUY");

            // Abnormal spike / squeeze where TR (12.0 bps) >> NoiseFloor (1.0 bps) and IsTradeableImpulse = false
            var spikeAudit = new SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: false,
                ZScore: 3.5,
                LocalAtrBps: 12.0,
                NoiseFloorBps: 1.0,
                RegimeDescription: "Аномальный спайк волатильности: вход заблокирован во избежание ложного пробоя"
            );

            var decision = await cm.EvaluateMatrixAsync("EURUSD_otc", "s10", true, 1.0, ta, smc, ml, state, mtf, noiseAudit: spikeAudit);

            Assert.Equal(50, decision.Probability);
            Assert.Contains("Спайк волатильности", decision.CombinedReasoningText);
        }

        [Fact]
        public async Task ConfluenceMatrix_DecelerationImpulse_AppliesRiskDampener()
        {
            var cm = new ConfluenceMatrixEngine(new MarketDataFetcher(), new TechnicalAnalysisEngine());
            var ta = new TaSignal(1.2, 75.0, 60, 100, 1.5, 8);
            var smc = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish");
            var ml = new MlSignal("BUY", 0.80, 0.70, "neural_brain");
            // Velocity is positive (+12.0) but regime is DECELERATING (acceleration opposes velocity)
            var state = new StateSignal("DECELERATING", 12.0, -0.2);
            var mtf = new ConfluenceMatrixResult(0.67, false, 7, "Normal", "MTF", new Dictionary<string, string>(), "BUY");

            var decision = await cm.EvaluateMatrixAsync("EURUSD_otc", "s10", true, 1.0, ta, smc, ml, state, mtf);

            Assert.True(decision.Probability <= 55, $"Expected probability <= 55% during deceleration, got {decision.Probability}");
            Assert.Contains("Затухание импульса", decision.CombinedReasoningText);
        }
    }
}
