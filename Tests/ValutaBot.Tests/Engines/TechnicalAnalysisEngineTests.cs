using System;
using System.Linq;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class TechnicalAnalysisEngineTests
    {
        [Fact]
        public void ScoreTimeframe_DynamismCheck_UptrendAndDowntrendScores()
        {
            double[] upTrend = new double[50];
            double[] downTrend = new double[50];
            double[] mockVols = new double[50];
            for (int i = 0; i < 50; i++)
            {
                upTrend[i] = 100.0 + i * 0.5; // strongly rising
                downTrend[i] = 100.0 - i * 0.5; // strongly falling
                mockVols[i] = 100.0;
            }

            var taEngine = new TechnicalAnalysisEngine();
            
            var upCandles = upTrend.Select((p, i) => new MiniAppController.OhlcCandle(
                i > 0 ? upTrend[i-1] : p, p + 0.1, p - 0.1, p, 100, DateTime.UtcNow.AddSeconds(i-50))).ToArray();
            var downCandles = downTrend.Select((p, i) => new MiniAppController.OhlcCandle(
                i > 0 ? downTrend[i-1] : p, p + 0.1, p - 0.1, p, 100, DateTime.UtcNow.AddSeconds(i-50))).ToArray();
            
            var upRes = taEngine.ScoreTimeframe("TEST_UP", "m1", upTrend, mockVols, upCandles, 45.0, 0.001, false, 35.0, 10.0);
            var downRes = taEngine.ScoreTimeframe("TEST_DOWN", "m1", downTrend, mockVols, downCandles, 45.0, 0.001, false, 10.0, 35.0);
            
            Assert.True(upRes.score > 0, $"Expected positive score, got {upRes.score:F3}");
            Assert.True(downRes.score < 0, $"Expected negative score, got {downRes.score:F3}");
            Assert.True(upRes.score > downRes.score, "Uptrend should be > downtrend");
        }

        [Fact]
        public void ValidateMarketGatekeeper_FlatMarket_ReturnsUntradeable()
        {
            double[] flatPrices = new double[20];
            var flatCandles = new MiniAppController.OhlcCandle[20];
            for (int i = 0; i < 20; i++) 
            { 
                flatPrices[i] = 1.0500; 
                flatCandles[i] = new MiniAppController.OhlcCandle(1.0500, 1.0500, 1.0500, 1.0500, 100);
            }
            
            var gatekeeperRes = (new TechnicalAnalysisEngine()).ValidateMarketGatekeeper("TEST", "m1", flatPrices, flatCandles);
            
            Assert.False(gatekeeperRes.IsTradeable);
            Assert.Contains("засто", gatekeeperRes.Reason);
        }

        [Fact]
        public void DataResiliency_EmptyArrays_HandledSafely()
        {
            MiniAppController.OhlcCandle[] emptyArr = Array.Empty<MiniAppController.OhlcCandle>();
            var taEngine = new TechnicalAnalysisEngine();
            
            var hmaRes = taEngine.ComputeHma("TEST", "m1", emptyArr);
            var rsiRes = taEngine.ComputeConnorsRsi("TEST", "m1", emptyArr);
            
            Assert.Equal(0.0, hmaRes);
            Assert.Equal(50.0, rsiRes);
        }
    }
}
