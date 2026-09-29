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
        if (Environment.GetEnvironmentVariable("AUTOSCANNER_DISABLED") == "true")
        {
            _logger.LogWarning("[AutoScanner] Disabled via AUTOSCANNER_DISABLED=true. Exiting.");
            return;
        }

        _logger.LogInformation("[AutoScanner] Service started. Will scan 6 pairs on s5/s10/s15/s30.");

        
        int currentPairIndex = 0;
        int currentTfIndex = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var dayOfWeek = DateTime.UtcNow.DayOfWeek;
            bool isWeekend = dayOfWeek == DayOfWeek.Saturday ||
                             (dayOfWeek == DayOfWeek.Sunday && DateTime.UtcNow.Hour < 21) ||
                             (dayOfWeek == DayOfWeek.Friday && DateTime.UtcNow.Hour >= 21);

            if (isWeekend)
            {
                _logger.LogDebug("[AutoScanner] Weekend - skipping scan.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
                var userSettings = await UserRepository.GetSettingsAsync(0);

                string pair = _targetPairs[currentPairIndex];
                string tf = _targetTimeframes[currentTfIndex];

                _logger.LogInformation($"[AutoScanner] Scanning {pair} on {tf}...");
                await orchestrator.ExecuteAnalysisAsync(pair, tf, userSettings);

                // Move to next TF/Pair
                currentTfIndex++;
                if (currentTfIndex >= _targetTimeframes.Length)
                {
                    currentTfIndex = 0;
                    currentPairIndex++;
                    if (currentPairIndex >= _targetPairs.Length)
                    {
                        currentPairIndex = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[AutoScanner] Exception during cycle: {ex.Message}");
            }

            // Drip-feed: 1 scan every 20 seconds. 
            // 3 requests a minute maximum, leaving 5 requests/minute for the user.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
    }
}
