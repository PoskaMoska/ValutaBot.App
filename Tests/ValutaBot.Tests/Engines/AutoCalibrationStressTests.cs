using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class AutoCalibrationStressTests
    {
        [Fact]
        public async Task RecordSourceOutcome_ThreadSafety_DoesNotCrash()
        {
            var tasks = new Task[100];
            var stressAutoCalib = new AutoCalibrationEngine();
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = Task.Run(() => 
                {
                    for (int j = 0; j < 10; j++)
                    {
                        stressAutoCalib.RecordSourceOutcome("LIGHTGBM", "TEST_ASSET", "m1", true);
                    }
                });
            }
            
            await Task.WhenAll(tasks);
            
            var weight = stressAutoCalib.GetCalibratedRegimeWeight("LIGHTGBM", "TEST_ASSET", "m1", AutoCalibrationEngine.MarketRegime.TrendingImpulse);
            Assert.True(weight > 0.0);
        }

        [Fact]
        public void ForgettingFactor_AppliesWithoutCrashing()
        {
            var testAutoCalib = new AutoCalibrationEngine();
            for (int i = 0; i < 60; i++)
            {
                testAutoCalib.RecordSourceOutcome("LIGHTGBM", "TEST_ASSET2", "m1", true);
            }
            
            var lgbmWeight = testAutoCalib.GetCalibratedRegimeWeight("LIGHTGBM", "TEST_ASSET2", "m1", AutoCalibrationEngine.MarketRegime.RangingFlat);
            Assert.True(lgbmWeight > 0.0);
        }
    }
}
