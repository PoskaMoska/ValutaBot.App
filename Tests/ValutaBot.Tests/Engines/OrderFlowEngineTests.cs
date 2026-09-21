using System;
using Xunit;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

namespace ValutaBot.Tests.Engines
{
    public class OrderFlowEngineTests
    {
        [Fact]
        public void AnalyzeOrderFlow_SpoofingTrap_DetectsTrap()
        {
            double[] spoofPrices = new double[10];
            double[] spoofVolumes = new double[10];
            for (int i = 0; i < 10; i++) { spoofPrices[i] = 100.0; spoofVolumes[i] = 100.0; }
            spoofPrices[8] = 99.999;
            spoofPrices[9] = 100.0; // Small up-tick to force volume into Buy side
            spoofVolumes[9] = 5000.0; // Massive volume, but priceDelta from 5 periods ago is 0
            
            var spoofCandles = new MiniAppController.OhlcCandle[10];
            for (int i = 0; i < 10; i++)
            {
                double p = spoofPrices[i];
                double prev = i > 0 ? spoofPrices[i - 1] : p;
                spoofCandles[i] = new MiniAppController.OhlcCandle(prev, Math.Max(p, prev), Math.Min(p, prev), p, spoofVolumes[i], DateTime.UtcNow.AddMinutes(i - 10));
            }
            
            var orderFlowRes = OrderFlowEngine.AnalyzeOrderFlow("TEST", "1m", spoofCandles, spoofPrices[9]);
            
            Assert.Contains("Спуфинг", orderFlowRes.OrderFlowState);
        }

        [Fact]
        public void AnalyzeOrderFlow_BearishAbsorption_DetectsAbsorption()
        {
            double[] bearishAbsPrices = { 100, 100, 100, 100, 100, 100.001, 100.002, 100.003, 100.004, 99.5 };
            double[] bearishAbsVols = { 100, 100, 100, 100, 100, 2000, 2000, 2000, 2000, 50 };
            
            var bearishAbsCandles = new MiniAppController.OhlcCandle[10];
            for (int i = 0; i < 10; i++)
            {
                double p = bearishAbsPrices[i];
                double prev = i > 0 ? bearishAbsPrices[i - 1] : p;
                bearishAbsCandles[i] = new MiniAppController.OhlcCandle(prev, Math.Max(p, prev), Math.Min(p, prev), p, bearishAbsVols[i], DateTime.UtcNow.AddMinutes(i - 10));
            }
            
            var absRes = OrderFlowEngine.AnalyzeOrderFlow("TEST2", "1m", bearishAbsCandles, bearishAbsPrices[9]);
            
            Assert.Equal("BEARISH_ABSORPTION", absRes.OrderFlowState);
        }
    }
}
