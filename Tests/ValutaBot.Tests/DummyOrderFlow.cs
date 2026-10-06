using System;
using ValutaBot.MiniApp;
using ValutaBot.App;

namespace ValutaBot.MiniApp
{
    public class OrderFlowEngine
    {
        public static OrderflowSignal AnalyzeOrderFlow(string asset, string timeframe, MiniAppController.OhlcCandle[] candles, double currentPrice)
        {
            return new OrderflowSignal(0.0, "dummy");
        }
    }

    public class StatefulOrderFlow
    {
        public double DeltaRatio { get; set; } = 1.0;
        public bool HasInstitutionalBlockTrade { get; set; } = false;
        
        public void Update(ReadOnlySpan<MiniAppController.OhlcCandle> candles) {}
        
        public OrderflowSignal Evaluate(string asset, string timeframe, double[] prices, double[] volumes)
        {
            return new OrderflowSignal(0.0, "dummy");
        }
    }
}
