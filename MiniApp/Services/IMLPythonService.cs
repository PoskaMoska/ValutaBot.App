using System;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp
{
    public interface IMLPythonService
    {
        void Init(string? baseUrl);
        Task<MLPythonService.MLPythonPrediction?> PredictAsync(string symbol, string interval, MLPythonService.MarketDataColumnar candles, MLPythonService.MarketDataColumnar? mtfCandles = null, bool isForex = false, double entryPrice = 0);
        Task SendFeedbackAsync(string asset, string timeframe, bool wasWin, string direction, double entryPrice, double exitPrice, double probLgbm, DateTime timestamp, bool isForex);
        Task<bool> ForceTrainGlobalAsync(string asset, string timeframe, bool isForex = false, int limit = 2000);
    }
}
