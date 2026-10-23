using System;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp
{
    public interface IMLPythonService
    {
        void Init(string? baseUrl);
        Task<MLPythonService.MLPythonPrediction?> PredictAsync(string symbol, string interval, MLPythonService.MarketDataColumnar candles, MLPythonService.MarketDataColumnar? mtfCandles = null, bool isForex = false, double entryPrice = 0);
    }
}
