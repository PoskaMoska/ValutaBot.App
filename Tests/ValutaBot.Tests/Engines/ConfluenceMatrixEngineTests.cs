using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class ConfluenceMatrixEngineTests
    {
        private readonly IConfluenceMatrixEngine _engine;

        public ConfluenceMatrixEngineTests()
        {
            _engine = new ConfluenceMatrixEngine(null!, null!);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_AllSignalsBullish_ReturnsBuyWithHighProbability()
        {
            // Arrange
            var taSignal = new TaSignal(2.0, 80.0, 65, 100, 2.5, 10);
            var smcSignal = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "BULLISH_OB", "BULLISH_FVG", "Strong bullish structure");
            var ofSignal = new OrderflowSignal(1.5, "Strong buying pressure");
            var mlSignal = new MlSignal("BUY", 0.9, 0.85, "test_model");
            var stateSignal = new StateSignal("TREND_BULLISH", 15.0, 1.0);
            var mtfResult = new ConfluenceMatrixResult(0.9, true, 5, "High", "MTF Golden", new Dictionary<string, string>(), "BUY");

            // Act
            var decision = await _engine.EvaluateMatrixAsync("BTCUSDT", "m5", false, 1.0, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            // Assert
            Assert.Equal("BUY", decision.FinalDirection);
            Assert.True(decision.Probability >= 60, $"Expected probability >= 60, got {decision.Probability}");
        }

        [Fact]
        public async Task EvaluateMatrixAsync_AllSignalsBearish_ReturnsPutWithHighProbability()
        {
            // Arrange
            var taSignal = new TaSignal(-2.0, 80.0, 35, 100, 2.5, 10);
            var smcSignal = new SmcSignal("BEARISH_BOS", "BEARISH_SWEEP", "BEARISH_OB", "BEARISH_FVG", "Strong bearish structure");
            var ofSignal = new OrderflowSignal(-1.5, "Strong selling pressure");
            var mlSignal = new MlSignal("PUT", 0.9, 0.85, "test_model");
            var stateSignal = new StateSignal("TREND_BEARISH", -15.0, -1.0);
            var mtfResult = new ConfluenceMatrixResult(0.9, true, 5, "High", "MTF Golden", new Dictionary<string, string>(), "PUT");

            // Act
            var decision = await _engine.EvaluateMatrixAsync("BTCUSDT", "m5", false, 1.0, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            // Assert
            Assert.Equal("PUT", decision.FinalDirection);
            Assert.True(decision.Probability >= 60, $"Expected probability >= 60, got {decision.Probability}");
        }

        [Fact]
        public async Task EvaluateMatrixAsync_EmptySignals_ReturnsNeutral()
        {
            // Arrange
            var taSignal = new TaSignal(0, 0, 50, 100, 0, 10);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var ofSignal = new OrderflowSignal(0, "None");
            var mlSignal = new MlSignal("NEUTRAL", 0, null, "none");
            var stateSignal = new StateSignal("FLAT", 0, 0);
            var mtfResult = new ConfluenceMatrixResult(0, false, 0, "None", "None", new Dictionary<string, string>(), "NEUTRAL");

            // Act
            // Set asset to EURUSD to bypass the real crypto Fear and Greed API which could shift the score from 0.0 to negative/positive
            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "m5", false, 1.0, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            // Assert
            Assert.Equal("NEUTRAL", decision.FinalDirection);
            Assert.Equal(0.0, decision.FinalTotalScore);
            Assert.Equal(50, decision.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_ProbabilityClampedToRealisticBoundaries()
        {
            // Extreme Bullish Inputs (Neural Brain Mode)
            var taBull = new TaSignal(10.0, 100.0, 90, 100, 10.0, 100);
            var smcBull = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "BULLISH_OB", "BULLISH_FVG", "Hyper Bullish");
            var ofBull = new OrderflowSignal(10.0, "Hyper buying");
            var mlBull = new MlSignal("BUY", 1.0, 0.99, "patch_tst_v3");
            var stateBull = new StateSignal("TREND_BULLISH", 50.0, 5.0);
            var mtfBull = new ConfluenceMatrixResult(1.0, true, 5, "Ultra", "MTF", new Dictionary<string, string>(), "BUY");

            var decisionBull = await _engine.EvaluateMatrixAsync("EURUSD", "1m", true, 1.0, taBull, smcBull, ofBull, mlBull, stateBull, mtfBull);

            // Assert: Clamped strictly to [50%, 99%] in Direct Neural Brain Mode
            Assert.InRange(decisionBull.Probability, 50, 99);
            Assert.Equal("BUY", decisionBull.FinalDirection);
            Assert.True(decisionBull.MlProb > 0.5, $"Expected MlProb > 0.5 for BUY, got {decisionBull.MlProb}");
            Assert.True(decisionBull.MlScoreRaw > 0.0, $"Expected MlScoreRaw > 0 for BUY, got {decisionBull.MlScoreRaw}");

            // Extreme Bearish Inputs (Neural Brain Mode)
            var taBear = new TaSignal(-10.0, 100.0, 10, 100, 10.0, 100);
            var smcBear = new SmcSignal("BEARISH_BOS", "BEARISH_SWEEP", "BEARISH_OB", "BEARISH_FVG", "Hyper Bearish");
            var ofBear = new OrderflowSignal(-10.0, "Hyper selling");
            var mlBear = new MlSignal("PUT", 1.0, 0.99, "patch_tst_v3");
            var stateBear = new StateSignal("TREND_BEARISH", -50.0, -5.0);
            var mtfBear = new ConfluenceMatrixResult(1.0, true, 5, "Ultra", "MTF", new Dictionary<string, string>(), "PUT");

            var decisionBear = await _engine.EvaluateMatrixAsync("EURUSD", "1m", true, 1.0, taBear, smcBear, ofBear, mlBear, stateBear, mtfBear);

            // Assert: Clamped strictly to [50%, 99%] and properly signed (< 0.5 MlProb, < 0 MlScoreRaw) for PUT
            Assert.InRange(decisionBear.Probability, 50, 99);
            Assert.Equal("PUT", decisionBear.FinalDirection);
            Assert.True(decisionBear.MlProb < 0.5, $"Expected MlProb < 0.5 for PUT, got {decisionBear.MlProb}");
            Assert.True(decisionBear.MlScoreRaw < 0.0, $"Expected MlScoreRaw < 0 for PUT, got {decisionBear.MlScoreRaw}");
        }

        [Fact]
        public async Task EvaluateMatrixAsync_NeuralBrainExplicitHold_DoesNotFallbackToLegacyTa()
        {
            var taSignal = new TaSignal(2.0, 90.0, 70, 100, 2.5, 10);
            var smcSignal = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "BULLISH_OB", "BULLISH_FVG", "Strong bullish");
            var ofSignal = new OrderflowSignal(1.5, "Strong buying");
            var mlSignal = new MlSignal("HOLD", 0.50, 0.625, "patch_tst_v4", 1, 0.50);
            var stateSignal = new StateSignal("TREND_BULLISH", 10.0, 0.8);
            var mtfResult = new ConfluenceMatrixResult(1.0, true, 15, "Golden", "MTF", new Dictionary<string, string>(), "BUY");

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s15", true, 1.0, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            Assert.Equal("NEUTRAL", decision.FinalDirection);
            Assert.Equal(50, decision.Probability);
            Assert.Equal(0.0, decision.FinalTotalScore);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_FlatMarketBelowNoiseFloor_CappedAt52Percent()
        {
            var taSignal = new TaSignal(1.5, 80.0, 60, 100, 1.0, 5);
            var smcSignal = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish BOS");
            var mlSignal = new MlSignal("BUY", 0.75, 0.70, "neural_brain");
            var stateSignal = new StateSignal("FLAT", 0.01, 0.0);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Normal", "MTF", new Dictionary<string, string>(), "BUY");

            // Subminute noise audit indicating dead flat market below 1.0 bps noise floor
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: false,
                ZScore: 0.2,
                LocalAtrBps: 0.3,
                NoiseFloorBps: 1.0,
                RegimeDescription: "Шумовой флэт"
            );

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s10", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.Equal(52, decision.Probability);
            Assert.Contains("Шумовой флэт", decision.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_BullishWickRejection_DoesNotInflateProbability()
        {
            var taSignal = new TaSignal(1.0, 70.0, 55, 100, 2.0, 10);
            var smcSignal = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish BOS");
            var mlSignal = new MlSignal("BUY", 0.60, 0.65, "neural_brain");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.5);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Normal", "MTF", new Dictionary<string, string>(), "BUY");

            // Subminute noise audit with tradeable impulse and Bullish Wick rejection
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: true,
                ZScore: 1.2,
                LocalAtrBps: 2.5,
                NoiseFloorBps: 1.0,
                RegimeDescription: "Импульс + Бычий отскок тени",
                HasBullishWick: true
            );

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s10", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.Equal(60, decision.Probability);
            Assert.Contains("Wick Rejection", decision.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_MacroTrendCompass_ResolvesBuyWhenTaScoreFlat()
        {
            // Flat TA (score = 0.05 <= 0.15), ML offline
            var taSignal = new TaSignal(0.05, 50.0, 52, 100, 1.0, 5);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var mlSignal = new MlSignal("NEUTRAL", 0.5, null, "offline");
            var stateSignal = new StateSignal("FLAT", 0.0, 0.0);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Macro M1 BUY", "MTF", new Dictionary<string, string> { ["M1"] = "BUY" }, "BUY");

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s15", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.Equal(52, decision.Probability);
            Assert.Contains("Компас Макро-Тренда (BUY)", decision.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_RiskDampener_PreservesCircuitBreakerPenaltyDuringWickRejection()
        {
            var taSignal = new TaSignal(1.0, 70.0, 55, 100, 2.0, 10);
            var smcSignal = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish BOS");
            var mlSignal = new MlSignal("BUY", 0.60, 0.65, "neural_brain");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.5);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Normal", "MTF", new Dictionary<string, string>(), "BUY");

            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: true,
                ZScore: 1.2,
                LocalAtrBps: 2.5,
                NoiseFloorBps: 1.0,
                RegimeDescription: "Импульс + Бычий отскок тени",
                HasBullishWick: true
            );

            // 3 consecutive losses: circuit breaker penalty (-6%) must NOT be erased by Wick Rejection
            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s10", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, consecutiveLosses: 3, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            // Probability should be capped at 60% due to active risk dampener (not 65-68%)
            Assert.True(decision.Probability <= 60, $"Expected probability <= 60% when circuit breaker is active, got {decision.Probability}");
            Assert.Contains("демпфировано", decision.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_S5StrongSignal_ClearsExecutionThresholdWithinCeiling()
        {
            var taSignal = new TaSignal(1.5, 70.0, 58, 100, 2.0, 10);
            var smcSignal = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish BOS");
            var mlSignal = new MlSignal("BUY", 0.64, 0.72, "dual_stream_v5");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.5);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Normal", "MTF", new Dictionary<string, string>(), "BUY");
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: true,
                ZScore: 1.0,
                LocalAtrBps: 2.0,
                NoiseFloorBps: 1.0,
                RegimeDescription: "Здоровый импульс"
            );

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s5", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.InRange(decision.Probability, 61, 65);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_ExtremeOrModerateCrowd_DoesNotInflateProbability()
        {
            var taSignal = new TaSignal(1.0, 70.0, 55.0, 100, 1.0, 5);
            var smcSignal = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish BOS");
            var mlSignal = new MlSignal("BUY", 0.56, 0.60, "patch_tst_v3");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.5);
            var mtfResult = new ConfluenceMatrixResult(0.60, false, 6, "Normal", "MTF", new Dictionary<string, string>(), "BUY");

            var baseline = await _engine.EvaluateMatrixAsync("EURUSD", "m1", false, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, crowdSentiment: 50.0);
            var extremeFade = await _engine.EvaluateMatrixAsync("EURUSD", "m1", false, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, crowdSentiment: 10.0);
            var moderateTrend = await _engine.EvaluateMatrixAsync("EURUSD", "m1", false, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, crowdSentiment: 65.0);

            Assert.Equal(baseline.Probability, extremeFade.Probability);
            Assert.Equal(baseline.Probability, moderateTrend.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_OverheatedFomoCrowdOnHighRsi_CapsBuyProbability()
        {
            var taSignal = new TaSignal(1.2, 70.0, 72.0, 100, 2.0, 10);
            var smcSignal = new SmcSignal("BULLISH_BOS", "NONE", "NONE", "NONE", "Bullish BOS");
            var mlSignal = new MlSignal("BUY", 0.62, 0.68, "patch_tst_v3");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.5);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "Normal", "MTF", new Dictionary<string, string>(), "BUY");

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "m1", false, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, crowdSentiment: 88.0);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.True(decision.Probability <= 54, $"Expected FOMO crowd cap <= 54%, got {decision.Probability}");
            Assert.Contains("Перегрев толпы", decision.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_QuantSubminute_PreservesCalibratedProbThroughLowZScoreAndDeceleration()
        {
            var taSignal = new TaSignal(-1.2, 70.0, 80.0, 100, 2.0, 10);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var mlSignal = new MlSignal("BUY", 0.62, 0.59, "neural_quant_v6");
            var stateSignal = new StateSignal("DECELERATING", 10.0, -0.2);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "PUT");
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: false,
                ZScore: 0.4,
                LocalAtrBps: 0.9,
                NoiseFloorBps: 0.20,
                RegimeDescription: "Недостаточный импульс (0.4σ)"
            );

            var decision = await _engine.EvaluateMatrixAsync("EURUSD", "s5", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.Equal(62, decision.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_QuantSubminute_PreservesQuietFlatWhileCappingVolatilitySpike()
        {
            var taSignal = new TaSignal(1.0, 70.0, 55.0, 100, 2.0, 10);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var mlSignal = new MlSignal("BUY", 0.63, 0.59, "neural_quant_v8");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.3);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "BUY");

            var flatNoiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: false,
                ZScore: 0.2,
                LocalAtrBps: 0.12,
                NoiseFloorBps: 0.20,
                RegimeDescription: "Рыночный штиль (размах ниже шума)"
            );
            var spikeAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: false,
                ZScore: 3.4,
                LocalAtrBps: 10.0,
                NoiseFloorBps: 0.20,
                RegimeDescription: "Аномальный спайк волатильности"
            );

            var flatDecision = await _engine.EvaluateMatrixAsync("EURUSD", "s5", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, noiseAudit: flatNoiseAudit);
            var spikeDecision = await _engine.EvaluateMatrixAsync("EURUSD", "s5", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, noiseAudit: spikeAudit);

            Assert.Equal(63, flatDecision.Probability);
            Assert.Equal(50, spikeDecision.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_QuantSubminute_DoesNotDeadlockOnConsecutiveLosses()
        {
            var taSignal = new TaSignal(1.0, 70.0, 55.0, 100, 2.0, 10);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var mlSignal = new MlSignal("BUY", 0.64, 0.62, "neural_quant_v7");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.3);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "BUY");
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: true,
                ZScore: 1.1,
                LocalAtrBps: 0.90,
                NoiseFloorBps: 0.20,
                RegimeDescription: "Импульс подтвержден"
            );

            var decision = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal, mlSignal, stateSignal, mtfResult,
                consecutiveLosses: 3, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.Equal(64, decision.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_QuantSubminute_PreservesHighConvictionUpTo75_WhileLegacyCapsAt65()
        {
            var taSignal = new TaSignal(1.0, 70.0, 55.0, 100, 2.0, 10);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.3);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "BUY");
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: true,
                ZScore: 1.0,
                LocalAtrBps: 0.90,
                NoiseFloorBps: 0.20,
                RegimeDescription: "Импульс подтвержден"
            );

            var quantDecision = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal,
                new MlSignal("BUY", 0.72, 0.65, "neural_quant_v8"),
                stateSignal, mtfResult, noiseAudit: noiseAudit);
            var cappedQuantDecision = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal,
                new MlSignal("BUY", 0.78, 0.68, "neural_quant_v8"),
                stateSignal, mtfResult, noiseAudit: noiseAudit);
            var legacyDecision = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal,
                new MlSignal("BUY", 0.72, 0.65, "legacy_v4"),
                stateSignal, mtfResult, noiseAudit: noiseAudit);

            Assert.Equal(72, quantDecision.Probability);
            Assert.Equal(75, cappedQuantDecision.Probability);
            Assert.Equal(65, legacyDecision.Probability);
        }

        [Theory]
        [InlineData("s30")]
        [InlineData("m1")]
        public async Task EvaluateMatrixAsync_S30AndM1_DoesNotInflateMlProbabilityWithHeuristicBoost(string timeframe)
        {
            var taSignal = new TaSignal(0.85, 70.0, 55.0, 100, 2.0, 10);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.45);
            var mtfResult = new ConfluenceMatrixResult(1.0, true, 12, "MTF", "MTF", new Dictionary<string, string>(), "BUY");
            var noiseAudit = new ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult(
                IsTradeableImpulse: true,
                ZScore: 1.0,
                LocalAtrBps: 1.20,
                NoiseFloorBps: 0.20,
                RegimeDescription: "Импульс подтвержден"
            );

            var decision = await _engine.EvaluateMatrixAsync(
                "EURUSD", timeframe, true, 1.0, taSignal, smcSignal,
                new MlSignal("BUY", 0.56, 0.56, "neural_quant_v8"),
                stateSignal, mtfResult, noiseAudit: noiseAudit);

            Assert.Equal("BUY", decision.FinalDirection);
            Assert.Equal(56, decision.Probability);
            Assert.Equal(0.45, decision.OfScore, precision: 4);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_SubsecondChase_SuppressesOverconfidentS5Entry()
        {
            var taSignal = new TaSignal(0.1, 55.0, 52.0, 1.1, 0.0, 0.0001, 22.0);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var stateSignal = new StateSignal("STABLE", 0.1, 0.0);
            var mtfResult = new ConfluenceMatrixResult(0.50, false, 5, "MTF", "MTF", new Dictionary<string, string>(), "NEUTRAL");

            var chaseSubsec = new RealtimeTickCollector.SubsecondTickMetrics(
                Ticks1_5s: 3, Ticks5s: 6, Ticks15s: 12,
                AccelRatio1_5s: 1.5, Ret1_5sBps: 0.22, Ret5sBps: 0.30, DirPurity5s: 0.8, LastTickAgeMs: 120.0);
            var daytimeChopFree = new MatrixMicroContext(chaseSubsec, PriceEntropy: 0.65, UtcNow: new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc));

            var chased = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal,
                new MlSignal("BUY", 0.64, 0.60, "neural_quant_v8"),
                stateSignal, mtfResult, 0, 1.0, null, 50.0, daytimeChopFree);

            Assert.Equal("BUY", chased.FinalDirection);
            Assert.True(chased.Probability <= 52, $"Expected chase guard cap <= 52%, got {chased.Probability}%");
            Assert.Contains("Микро-чейз", chased.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_SubsecondDoublePullback_LiftsStrongSetupWithoutChop()
        {
            var taSignal = new TaSignal(0.3, 58.0, 48.0, 1.1, 0.0, 0.0001, 22.0);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var stateSignal = new StateSignal("STABLE", -0.2, 0.0);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "BUY");

            var pullbackSubsec = new RealtimeTickCollector.SubsecondTickMetrics(
                Ticks1_5s: 3, Ticks5s: 7, Ticks15s: 14,
                AccelRatio1_5s: 1.3, Ret1_5sBps: -0.28, Ret5sBps: -0.20, DirPurity5s: -0.7, LastTickAgeMs: 150.0);
            var cleanCtx = new MatrixMicroContext(pullbackSubsec, PriceEntropy: 0.65, UtcNow: new DateTime(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc));

            var lifted = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal,
                new MlSignal("BUY", 0.58, 0.60, "neural_quant_v8"),
                stateSignal, mtfResult, 0, 1.0, null, 50.0, cleanCtx);

            Assert.Equal("BUY", lifted.FinalDirection);
            Assert.Equal(62, lifted.Probability);
            Assert.Contains("Снайперский микровход", lifted.CombinedReasoningText);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_DaytimeDispersionChop_CapsCalibratedProbWhileNightPreservesIt()
        {
            var taSignal = new TaSignal(0.2, 56.0, 68.0, 1.1, 0.0, 0.0001, 34.0);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var stateSignal = new StateSignal("STABLE", 0.2, 0.1);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "BUY");
            var ml = new MlSignal("BUY", 0.64, 0.60, "neural_quant_v8");

            var dayChop = new MatrixMicroContext(null, PriceEntropy: 1.05, UtcNow: new DateTime(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc));
            var nightHighEnt = new MatrixMicroContext(null, PriceEntropy: 1.05, UtcNow: new DateTime(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc));

            var dayDecision = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal, ml, stateSignal, mtfResult, 0, 1.0, null, 50.0, dayChop);
            var nightDecision = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s5", true, 1.0, taSignal, smcSignal, ml, stateSignal, mtfResult, 0, 1.0, null, 50.0, nightHighEnt);

            Assert.True(dayDecision.Probability <= 54, $"Expected daytime chop cap <= 54%, got {dayDecision.Probability}%");
            Assert.Equal(64, nightDecision.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_ChoppyTickStorm_CapsOverconfidentEntryWhileCalmFlowPreservesIt()
        {
            var taSignal = new TaSignal(0.2, 55.0, 50.0, 155.0, 0.0, 0.01, 20.0);
            var smcSignal = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var stateSignal = new StateSignal("STABLE", 0.0, 0.0);
            var mtfResult = new ConfluenceMatrixResult(0.67, false, 7, "MTF", "MTF", new Dictionary<string, string>(), "BUY");
            var ml = new MlSignal("BUY", 0.64, 0.60, "neural_quant_v8");

            var stormSubsec = new RealtimeTickCollector.SubsecondTickMetrics(
                Ticks1_5s: 5, Ticks5s: 15, Ticks15s: 32,
                AccelRatio1_5s: 1.1, Ret1_5sBps: -0.05, Ret5sBps: 0.02, DirPurity5s: 0.22, LastTickAgeMs: 120.0);
            var calmSubsec = new RealtimeTickCollector.SubsecondTickMetrics(
                Ticks1_5s: 2, Ticks5s: 6, Ticks15s: 15,
                AccelRatio1_5s: 1.0, Ret1_5sBps: -0.05, Ret5sBps: -0.02, DirPurity5s: -0.30, LastTickAgeMs: 120.0);

            var stormCtx = new MatrixMicroContext(stormSubsec, PriceEntropy: 0.60, UtcNow: new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc));
            var calmCtx = new MatrixMicroContext(calmSubsec, PriceEntropy: 0.60, UtcNow: new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc));

            var stormDecision = await _engine.EvaluateMatrixAsync(
                "USDJPY", "s5", true, 1.0, taSignal, smcSignal, ml, stateSignal, mtfResult, 0, 1.0, null, 50.0, stormCtx);
            var calmDecision = await _engine.EvaluateMatrixAsync(
                "USDJPY", "s5", true, 1.0, taSignal, smcSignal, ml, stateSignal, mtfResult, 0, 1.0, null, 50.0, calmCtx);

            Assert.True(stormDecision.Probability <= 56, $"Expected choppy storm cap <= 56%, got {stormDecision.Probability}%");
            Assert.Contains("Шторм тиков", stormDecision.CombinedReasoningText);
            Assert.Equal(64, calmDecision.Probability);
        }

        [Fact]
        public async Task EvaluateMatrixAsync_CounterBosAndCoherentDxy_DampensConflictingEntryAcrossBothDirectAndInverseUsdPairs()
        {
            var taSignal = new TaSignal(0.1, 55.0, 52.0, 1.1, 0.0, 0.0001, 20.0);
            var stateSignal = new StateSignal("STABLE", 0.0, 0.0);
            var mtfResult = new ConfluenceMatrixResult(0.50, false, 5, "MTF", "MTF", new Dictionary<string, string>(), "NEUTRAL");
            var mlBuy = new MlSignal("BUY", 0.63, 0.60, "neural_quant_v8");

            // EURUSD BUY against BEARISH_BOS (-3%) AND falling EUR/rising USD DXY (-0.60 -> pairDxy = +0.60 or +0.60 * -1 = -0.60 -> counter BUY -2%)
            var conflictCtx = new MatrixMicroContext(
                Subsecond: null,
                PriceEntropy: 0.60,
                UtcNow: new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc),
                DxyMomentum1mBps: 0.60,
                BasketSync5sScore: 0.80);
            var bearBos = new SmcSignal("BEARISH_BOS", "NONE", "NONE", "NONE", "Bearish BOS");

            var conflictEur = await _engine.EvaluateMatrixAsync(
                "EURUSD", "s15", true, 1.0, taSignal, bearBos, mlBuy, stateSignal, mtfResult, 0, 1.0, null, 50.0, conflictCtx);
            // USDJPY BUY with positive DXY (+0.60 -> +1.0 * +0.60 = +0.60) and no BOS conflict preserves full conviction
            var cleanJpy = await _engine.EvaluateMatrixAsync(
                "USDJPY", "s15", true, 1.0, taSignal, new SmcSignal("NONE", "NONE", "NONE", "NONE", ""), mlBuy, stateSignal, mtfResult, 0, 1.0, null, 50.0, conflictCtx);

            Assert.Equal(58, conflictEur.Probability);
            Assert.Contains("пробоя структуры", conflictEur.CombinedReasoningText);
            Assert.Contains("корзины USD", conflictEur.CombinedReasoningText);
            Assert.Equal(63, cleanJpy.Probability);
        }
    }
}
