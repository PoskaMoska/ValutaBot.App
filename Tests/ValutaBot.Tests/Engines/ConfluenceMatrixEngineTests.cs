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
    }
}


