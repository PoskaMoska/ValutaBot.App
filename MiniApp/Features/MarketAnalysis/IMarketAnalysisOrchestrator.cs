using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Models;

namespace ValutaBot.MiniApp.Features.MarketAnalysis;

public interface IMarketAnalysisOrchestrator
{
    Task<AnalysisResponseDto> ExecuteAnalysisAsync(
        string asset,
        string timeframe,
        ValutaBot.App.MiniApp.Data.Repositories.UserSettings? userSettings = null,
        MiniAppController.OhlcCandle[]? preloadedCandles = null,
        CancellationToken ct = default);
}
