namespace ValutaBot.MiniApp;

using System;

public interface IMathEngine
{
    double ComputeRsi(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14);
    double ComputeConnorsRsi(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles);
    double ComputeHma(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9);
    double ComputeEma(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9);
    (double adx, double pdi, double mdi) ComputeTrueAdx(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14);
    double ComputeAtr(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14);
    
    ValutaBot.MiniApp.Indicators.StatefulSmc GetSmcState(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice);
}

public interface IMarketAnalyzer
{
    (double score, double confidence, double rsiVal, double hmaVal, double volStrengthVal, double atrVal) ScoreTimeframe(
        string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<double> volumes, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default,
        double? adxOverride = null, double? atrOverride = null, bool isForex = false,
        double? pdiOverride = null, double? mdiOverride = null);

    
    double CalculateVolatilityRatio(ReadOnlySpan<double> prices);
}

public interface IRiskGatekeeper
{
    TechnicalAnalysisEngine.GatekeeperResult ValidateMarketGatekeeper(string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default);
}

public interface ITechnicalAnalysisEngine : IMathEngine, IMarketAnalyzer, IRiskGatekeeper
{
}
