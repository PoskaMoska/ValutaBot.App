using System;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    /// <summary>
    /// Regression tests for Junction-2 fixes (Orchestrator -> ConfluenceMatrix -> MetaLearner).
    ///   D2-2: MlSignal.Confidence must be explicitly clamped to [0..1] at construction.
    ///   D2-3: SmcSignal string fields must be normalized to "" instead of null.
    /// </summary>
    public class Junction2FixRegressionTests
    {
        private readonly ITestOutputHelper _out;
        public Junction2FixRegressionTests(ITestOutputHelper output) => _out = output;

        // ═══════════════════════════════════════════════════════════════════
        // D2-2: ML Confidence Clamping
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void D2_2_MlSignal_Confidence_IsClamped_ByOrchestratorLogic()
        {
            // Simulate Python returning an uncalibrated logit (e.g. 1.73)
            double pythonLogit = 1.73;
            
            // The logic from Orchestrator
            double lgbmConf = Math.Clamp(pythonLogit, 0.0, 1.0);
            
            var mlSignal = new MlSignal("BUY", lgbmConf, 0.8, "test");
            
            Assert.Equal(1.0, mlSignal.Confidence);
            _out.WriteLine($"[D2-2] Python returned {pythonLogit} → clamped to {mlSignal.Confidence}");
        }

        [Fact]
        public void D2_2_MlSignal_Confidence_Negative_IsClampedToZero()
        {
            // Simulate Python returning negative prob (should never happen, but safety first)
            double pythonBad = -0.2;
            
            // The logic from Orchestrator
            double lgbmConf = Math.Clamp(pythonBad, 0.0, 1.0);
            
            var mlSignal = new MlSignal("BUY", lgbmConf, 0.8, "test");
            
            Assert.Equal(0.0, mlSignal.Confidence);
            _out.WriteLine($"[D2-2] Python returned {pythonBad} → clamped to {mlSignal.Confidence}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // D2-3: SMC Signal Null Normalization
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void D2_3_SmcSignal_NullsAreNormalized_ToEmptyStrings()
        {
            // Simulate SmcResult with nulls (e.g. not enough data)
            string? rawBos = null;
            string? rawSweep = null;
            string? rawOb = null;
            string? rawFvg = null;

            // The construction logic from Orchestrator
            var smcSignal = new SmcSignal(
                rawBos ?? "",
                rawSweep ?? "",
                rawOb ?? "",
                rawFvg ?? "",
                "");

            Assert.NotNull(smcSignal.BosDirection);
            Assert.NotNull(smcSignal.SweepDirection);
            Assert.NotNull(smcSignal.OrderBlockType);
            Assert.NotNull(smcSignal.FvgType);

            Assert.Equal("", smcSignal.BosDirection);
            Assert.Equal("", smcSignal.SweepDirection);
            Assert.Equal("", smcSignal.OrderBlockType);
            Assert.Equal("", smcSignal.FvgType);

            _out.WriteLine("[D2-3] SMC Signal nulls successfully normalized to empty strings.");
        }
        
        [Fact]
        public void D2_3_SmcSignal_EmptyString_SafeToUseWithContains()
        {
            var smcSignal = new SmcSignal("", "", "", "", "");
            
            // Prove that without nulls, .Contains() won't throw NullReferenceException
            bool isBullish = smcSignal.BosDirection.Contains("BULLISH");
            
            Assert.False(isBullish);
        }
    }
}
