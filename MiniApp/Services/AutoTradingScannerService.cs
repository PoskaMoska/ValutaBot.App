using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp.Services;

/// <summary>
/// Background service that actively scans all 6 machine-learning pairs 
/// on the 's5' timeframe every 5 seconds.
/// This allows the bot to continuously accumulate live trades in PostgreSQL 
/// (via MarketAnalysisOrchestrator -> SignalTracker) even when the frontend is closed,
/// speeding up the collection of out-of-sample data.
/// </summary>
public class AutoTradingScannerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoTradingScannerService> _logger;
    private static readonly string[] _targetPairs = { "EUR/USD", "GBP/USD", "AUD/USD", "USD/CAD", "USD/CHF", "USD/JPY" };
    private static readonly string[] _targetTimeframes = { "s5", "s10", "s15", "s30" };

    public AutoTradingScannerService(IServiceProvider serviceProvider, ILogger<AutoTradingScannerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AutoScanner] Service started. Will scan 6 pairs on s5/s10/s15/s30.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var loopStart = DateTime.UtcNow;

            // Skip weekends — forex is closed, OTC data is static/synthetic.
            // Signals on weekend historical data are meaningless and pollute MetaLearner feedback.
            var dayOfWeek = DateTime.UtcNow.DayOfWeek;
            bool isWeekend = dayOfWeek == DayOfWeek.Saturday ||
                             (dayOfWeek == DayOfWeek.Sunday && DateTime.UtcNow.Hour < 21) ||
                             (dayOfWeek == DayOfWeek.Friday && DateTime.UtcNow.Hour >= 21);

            if (isWeekend)
            {
                _logger.LogDebug("[AutoScanner] Weekend — skipping scan to avoid OTC garbage trades.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
                var userSettings = await UserRepository.GetSettingsAsync(0);

                var tasks = new List<Task>();
                foreach (var pair in _targetPairs)
                {
                    foreach (var tf in _targetTimeframes)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 160);
                                if (recentCandles.Length < 160) return;
                                
                                var lastCandleTime = recentCandles[^1].Timestamp;
                                if ((DateTime.UtcNow - lastCandleTime).TotalSeconds > 30) return;

                                await orchestrator.ExecuteAnalysisAsync(pair, tf, userSettings);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"[AutoScanner] Error analyzing {pair} {tf}: {ex.Message}");
                            }
                        }, stoppingToken));
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AutoScanner] Fatal error in scanner loop.");
            }

            // Target loop time is 5 seconds for s5
            var elapsed = DateTime.UtcNow - loopStart;
            var delay = TimeSpan.FromSeconds(5) - elapsed;
            
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}
