using System;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class ContinuousStateEngineDiagnosticTests
    {
        [Fact]
        public void EvaluateContinuousState_TrendingData_ProducesNonStableRegime()
        {
            double[] trendPrices = new double[60];
            var randTrend = new Random(100);
            double lastChange = 0;
            trendPrices[0] = 10.0;
            for (int i = 1; i < 60; i++)
            {
                double currentChange = (randTrend.NextDouble() - 0.5) * 0.1 + lastChange * 0.75 + 0.02;
                trendPrices[i] = trendPrices[i - 1] + currentChange;
                lastChange = currentChange;
            }

            var hurstTrendResult = ContinuousStateEngine.EvaluateContinuousState(trendPrices, "TEST_HURST", "m1");
            Assert.True(hurstTrendResult.VelocityRegime != "STABLE" || hurstTrendResult.VelocityBpsPerSec > 0.5, 
                $"Expected trending regime, got {hurstTrendResult.VelocityRegime} vel={hurstTrendResult.VelocityBpsPerSec:F2}");
        }

        [Fact]
        public void EvaluateContinuousState_RangeData_ProducesLowMomentum()
        {
            // Trend setup
            double[] trendPrices = new double[60];
            var randTrend = new Random(100);
            double lastChange = 0;
            trendPrices[0] = 10.0;
            for (int i = 1; i < 60; i++)
            {
                double currentChange = (randTrend.NextDouble() - 0.5) * 0.1 + lastChange * 0.75 + 0.02;
                trendPrices[i] = trendPrices[i - 1] + currentChange;
                lastChange = currentChange;
            }
            var hurstTrendResult = ContinuousStateEngine.EvaluateContinuousState(trendPrices, "TEST_HURST", "m1");

            // Range setup
            double[] rangePrices = new double[60];
            var randRange = new Random(42);
            rangePrices[0] = 10.0;
            for (int i = 1; i < 60; i++)
                rangePrices[i] = rangePrices[i - 1] + (randRange.NextDouble() - 0.5) * 0.01;
            
            var hurstRangeResult = ContinuousStateEngine.EvaluateContinuousState(rangePrices, "TEST_HURST_RANGE", "m1");
            
            Assert.True(Math.Abs(hurstRangeResult.MomentumContribution) <= Math.Abs(hurstTrendResult.MomentumContribution), 
                $"Range momentum ({hurstRangeResult.MomentumContribution:F2}) should be <= trending ({hurstTrendResult.MomentumContribution:F2})");
        }

        [Fact]
        public void EvaluateContinuousState_FlashCrash_DetectsHyperAcceleratingDown()
        {
            double[] flashPrices = { 100, 100, 100, 100, 100, 99, 97, 94, 90, 85, 75, 60 };
            var flashRes = ContinuousStateEngine.EvaluateContinuousState(flashPrices, "TEST", "m1");
            
            Assert.Equal("HYPER_ACCELERATING_DOWN", flashRes.VelocityRegime);
            Assert.True(flashRes.VelocityBpsPerSec < -3.0);
        }
    }
}
