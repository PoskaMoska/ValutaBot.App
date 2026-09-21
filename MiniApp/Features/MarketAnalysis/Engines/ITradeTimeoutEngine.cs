namespace ValutaBot.MiniApp;

public interface ITradeTimeoutEngine
{
    TradeTimeoutEngine.TimeoutResult CalculateTimeout(
        string asset,
        string timeframe,
        double atr,
        double volRatio,
        SmcEngine.SmcAnalysisResult smc,
        double currentPrice,
        ContinuousStateResult state,
        bool isForex = false);
}

