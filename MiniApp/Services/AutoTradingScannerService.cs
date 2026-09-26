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

    public AutoTradingScannerService(IServiceProvider serviceProvider, ILogger<AutoTradingScannerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AutoScanner] Service started. Will scan 6 pairs on s5 every 5 seconds.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var loopStart = DateTime.UtcNow;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
                
                // Get default user settings (userId = 0 is system default)
                var userSettings = await UserRepository.GetSettingsAsync(0);

                var tasks = new List<Task>();
                foreach (var pair in _targetPairs)
                {
                    // Fire and forget analysis for each pair concurrently
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // ExecuteAnalysisAsync triggers SignalTracker.RecordPredictionAsync internally if valid
                            await orchestrator.ExecuteAnalysisAsync(pair, "s5", userSettings);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"[AutoScanner] Error analyzing {pair}: {ex.Message}");
                        }
                    }, stoppingToken));
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
