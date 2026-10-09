using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp.Services;

/// <summary>
/// Background service that actively scans machine-learning pairs.
/// Uses a dual-engine architecture:
/// 1. Fast, free subminute scanner (s5-s30) relying on local WS ticks.
/// 2. Slow, throttled minute scanner (m1) using REST API to preserve 800/day limits.
/// </summary>
public class AutoTradingScannerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoTradingScannerService> _logger;
    private readonly ICircuitBreakerService _circuitBreaker;
    private readonly TradingBotSettings _settings;
    private static readonly string[] _targetPairs = { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF" };
    private static readonly string[] _subminuteTfs = { "s5", "s10", "s15", "s30" };
    private static readonly string[] _minuteTfs = { "m1" };

    // Minimum candles per timeframe to trigger a scan — capped by physical limits:
    // s5:  12 candles/min × 30 min = 360 max → require 160
    // s10:  6 candles/min × 30 min = 180 max → require 120
    // s15:  4 candles/min × 30 min = 120 max → require  80
    // s30:  2 candles/min × 30 min =  60 max → require  40
    // FIX: Old hardcoded 160 physically blocked s15 and s30 from ever scanning.
    private static readonly System.Collections.Generic.Dictionary<string, int> _minCandlesPerTf =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["s5"]  = 160,
            ["s10"] = 120,
            ["s15"] = 80,
            ["s30"] = 40,
        };


    public AutoTradingScannerService(IServiceProvider serviceProvider, ILogger<AutoTradingScannerService> logger, ICircuitBreakerService circuitBreaker, IOptions<TradingBotSettings> settings)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _circuitBreaker = circuitBreaker;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("AUTOSCANNER_DISABLED") == "true")
        {
            _logger.LogWarning("[AutoScanner] Disabled via AUTOSCANNER_DISABLED=true. Exiting.");
            return;
        }

        _logger.LogInformation("[AutoScanner] Dual-Engine Service started. Starting Subminute (Free) and Minute (Paid) streams.");

        var subminuteTask = RunSubminuteScannerAsync(stoppingToken);
        var minuteTask = RunMinuteScannerAsync(stoppingToken);

        await Task.WhenAll(subminuteTask, minuteTask);
    }

    private async Task RunSubminuteScannerAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateUnbounded<(string pair, string tf)>();

        RealtimeTickCollector.OnCandleClosed += (pair, tf) =>
        {
            if (_targetPairs.Contains(pair, StringComparer.OrdinalIgnoreCase) && _subminuteTfs.Contains(tf, StringComparer.OrdinalIgnoreCase))
            {
                channel.Writer.TryWrite((pair, tf));
            }
        };

        var workers = new List<Task>();
        for (int i = 0; i < 4; i++) // 4 concurrent workers
        {
            workers.Add(Task.Run(async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync(stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessSubminuteScanAsync(item.pair, item.tf, stoppingToken);
                }
            }, stoppingToken));
        }

        await Task.WhenAll(workers);
    }

    private async Task ProcessSubminuteScanAsync(string pair, string tf, CancellationToken stoppingToken)
    {
        if (IsWeekendPause()) return;

        if (_settings.DatasetCollectionMode)
        {
            // Allowed
        }
        else if (_circuitBreaker.IsHalted())
        {
            _logger.LogWarning($"[AutoScanner] Circuit Breaker Active. Pausing scan...");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
            var userSettings = await UserRepository.GetSettingsAsync(0);

            int minCandles = _minCandlesPerTf.TryGetValue(tf, out int mc) ? mc : 80;
            var recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 160);
            
            if (recentCandles.Length >= minCandles)
            {
                var lastCandleTime = recentCandles[^1].Timestamp;
                if ((DateTime.UtcNow - lastCandleTime).TotalSeconds <= 30)
                {
                    _logger.LogInformation($"[AutoScanner-Fast] Scanning {pair} on {tf} ({recentCandles.Length}/{minCandles} candles)...");
                    await orchestrator.ExecuteAnalysisAsync(pair, tf, userSettings);
                }
                else
                {
                    _logger.LogWarning($"[AutoScanner-Fast] Stale candles for {pair}/{tf}: last={lastCandleTime:HH:mm:ss}. Skipping.");
                }
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("���������")) _logger.LogWarning($"[AutoScanner-Fast] Blocked: {ex.Message}"); else _logger.LogError($"[AutoScanner-Fast] Exception: {ex.Message}");
        }
    }

    private async Task RunMinuteScannerAsync(CancellationToken stoppingToken)
    {
        int currentPairIndex = 0;
        int currentTfIndex = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsWeekendPause())
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            if (_settings.DatasetCollectionMode)
            {
                _logger.LogDebug("[AutoScanner] DatasetCollectionMode=true — Circuit Breaker bypassed for continuous data collection.");
            }
            else if (_circuitBreaker.IsHalted())
            {
                _logger.LogWarning($"[AutoScanner] Circuit Breaker Active: {_circuitBreaker.GetHaltedReason()}. Pausing scan...");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
                var userSettings = await UserRepository.GetSettingsAsync(0);

                string pair = _targetPairs[currentPairIndex];
                string tf = _minuteTfs[currentTfIndex];

                _logger.LogInformation($"[AutoScanner-Slow] Scanning {pair} on {tf} (Consumes API Limits)...");
                await orchestrator.ExecuteAnalysisAsync(pair, tf, userSettings);

                MoveToNextCycle(ref currentTfIndex, ref currentPairIndex, _minuteTfs.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[AutoScanner-Slow] Exception: {ex.Message}");
            }

            // Slow loop: 4 minutes between REST API calls. 
            // 15 requests per hour * 2 (base+mtf) = 30 req/hour = 720/day.
            await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken);
        }
    }

    private void MoveToNextCycle(ref int currentTfIndex, ref int currentPairIndex, int tfLength)
    {
        currentTfIndex++;
        if (currentTfIndex >= tfLength)
        {
            currentTfIndex = 0;
            currentPairIndex++;
            if (currentPairIndex >= _targetPairs.Length)
            {
                currentPairIndex = 0;
            }
        }
    }

    private bool IsWeekendPause()
    {
        var dayOfWeek = DateTime.UtcNow.DayOfWeek;
        return dayOfWeek == DayOfWeek.Saturday ||
               (dayOfWeek == DayOfWeek.Sunday && DateTime.UtcNow.Hour < 21) ||
               (dayOfWeek == DayOfWeek.Friday && DateTime.UtcNow.Hour >= 21);
    }
}
