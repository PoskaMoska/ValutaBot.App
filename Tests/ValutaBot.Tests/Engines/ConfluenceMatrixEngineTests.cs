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
            _engine = new ConfluenceMatrixEngine(null, null, null);
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
            Assert.True(decision.Probability >= 70, $"Expected probability >= 70, got {decision.Probability}");
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
            Assert.True(decision.Probability >= 70, $"Expected probability >= 70, got {decision.Probability}");
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
            var decision = await _engine.EvaluateMatrixAsync("BTCUSDT", "m5", false, 1.0, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            // Assert
            Assert.Equal("NEUTRAL", decision.FinalDirection);
        }
    }
}

