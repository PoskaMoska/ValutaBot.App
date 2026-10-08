using System;
using Xunit;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

namespace ValutaBot.Tests.Engines
{
    public class OnlineMetaLearnerTests
    {
        [Fact]
        public void Predict_ValidInputs_ReturnsReasonableProbability()
        {
            var learner = new OnlineMetaLearner();
            
            // Bullish confluence
            double pBull = learner.Predict("EURUSD", "s5", ta: 0.6, of: 0.0, smc: 0.5, ml: 0.7, tfConflict: false);
            Assert.True(pBull > 0.55, $"Expected bullish prob > 0.55, got {pBull}");

            // Bearish confluence
            double pBear = learner.Predict("EURUSD", "s5", ta: -0.6, of: 0.0, smc: -0.5, ml: -0.7, tfConflict: false);
            Assert.True(pBear < 0.45, $"Expected bearish prob < 0.45, got {pBear}");
        }

        [Fact]
        public void PartialFit_SymmetricLosses_DoesNotCollapseWeightsToFloor()
        {
            var learner = new OnlineMetaLearner();
            string testAsset = "TEST_" + Guid.NewGuid().ToString("N")[..6];
            string testTf = "s5";

            // Simulate 10 consecutive losses (both BUY and PUT)
            for (int i = 0; i < 10; i++)
            {
                string dir = (i % 2 == 0) ? "BUY" : "PUT";
                double taVal = dir == "BUY" ? 0.4 : -0.4;
                double mlVal = dir == "BUY" ? 0.5 : -0.5;
                learner.PartialFit(testAsset, testTf, ta: taVal, of: 0.0, smc: 0.0, ml: mlVal, wasWin: false, direction: dir);
            }

            var weights = learner.GetWeights(testAsset, testTf);
            Assert.Equal(5, weights.Length);

            // Verify weights did NOT collapse to near-zero 0.05
            Assert.True(weights[1] >= 0.20, $"TA weight {weights[1]} collapsed too much");
            Assert.True(weights[4] >= 0.20, $"ML weight {weights[4]} collapsed too much");
            // Bias should remain bounded
            Assert.True(Math.Abs(weights[0]) <= 1.0, $"Bias {weights[0]} drifted too far");
        }
    }
}
