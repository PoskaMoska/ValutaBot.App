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
    private static readonly string[] _basePairs = { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF" };

    // Candle keys the scanner listens to: "EURUSD_PO" (Pocket Option OTC) or "EURUSD" (Tiingo legacy).
    private static readonly string[] _targetPairs = _basePairs.Select(PriceFeed.CandleKey).ToArray();

    // PO mode: all 5 target horizons (5s, 10s, 15s, 30s, 1m) come from the PO tick feed, event-driven.
    private static readonly string[] _subminuteTfs = PriceFeed.UsePocketOption
        ? new[] { "s5", "s10", "s15", "s30", "m1" }
        : new[] { "s5", "s10", "s15", "s30" };
    private static readonly string[] _minuteTfs = { "m1" };

    // Minimum candles per timeframe to trigger a scan — capped by physical limits:
    // s5:  12 candles/min × 30 min = 360 max → require 160
    // s10:  6 candles/min × 30 min = 180 max → require 120
    // s15:  4 candles/min × 30 min = 120 max → require  80
    // s30:  2 candles/min × 30 min =  60 max → require  40
    // m1:   1 candle/min            → require 40 (PO mode)
    // FIX: Old hardcoded 160 physically blocked s15 and s30 from ever scanning.
    private static readonly System.Collections.Generic.Dictionary<string, int> _minCandlesPerTf =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["s5"]  = 160,
            ["s10"] = 120,
            ["s15"] = 80,
            ["s30"] = 40,
            ["m1"]  = 40,
        };

    /// <summary>Name passed to the analysis pipeline: "EURUSD_PO" -> "EURUSD_OTC" (sanitizer maps it to EURUSD).</summary>
    private static string AnalysisAsset(string candleKey)
        => PriceFeed.IsPoKey(candleKey) ? PriceFeed.BaseAsset(candleKey) + "_OTC" : candleKey;


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
        var channel = Channel.CreateUnbounded<(string pair, string tf, DateTime queuedAtUtc)>();
        var queuedKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var inFlightKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        Action<string, string> candleClosedHandler = (pair, tf) =>
        {
            if (_targetPairs.Contains(pair, StringComparer.OrdinalIgnoreCase) && _subminuteTfs.Contains(tf, StringComparer.OrdinalIgnoreCase))
            {
                string key = $"{pair}:{tf}";
                if (queuedKeys.TryAdd(key, 1))
                {
                    if (!channel.Writer.TryWrite((pair, tf, DateTime.UtcNow)))
                    {
                        queuedKeys.TryRemove(key, out _);
                    }
                }
            }
        };

        RealtimeTickCollector.OnCandleClosed += candleClosedHandler;

        var workers = new List<Task>();
        for (int i = 0; i < 4; i++) // 4 concurrent workers
        {
            workers.Add(Task.Run(async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync(stoppingToken))
                {
                    string key = $"{item.pair}:{item.tf}";
                    queuedKeys.TryRemove(key, out _);

                    if (stoppingToken.IsCancellationRequested) break;

                    // Drop stale events that waited too long in queue relative to their timeframe (prevents s5/s10 entry skew)
                    double maxQueueWaitSec = item.tf.Equals("s5", StringComparison.OrdinalIgnoreCase) ? 1.5
                        : item.tf.Equals("s10", StringComparison.OrdinalIgnoreCase) ? 2.2
                        : 3.5;
                    if ((DateTime.UtcNow - item.queuedAtUtc).TotalSeconds > maxQueueWaitSec)
                    {
                        continue;
                    }

                    if (!inFlightKeys.TryAdd(key, 1))
                    {
                        continue;
                    }

                    try
                    {
                        await ProcessSubminuteScanAsync(item.pair, item.tf, stoppingToken);
                    }
                    finally
                    {
                        inFlightKeys.TryRemove(key, out _);
                    }
                }
            }, stoppingToken));
        }

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            RealtimeTickCollector.OnCandleClosed -= candleClosedHandler;
            channel.Writer.TryComplete();
        }
    }

    private long _lastCircuitBreakerWarnTicks = 0;

    private async Task ProcessSubminuteScanAsync(string pair, string tf, CancellationToken stoppingToken)
    {
        if (IsWeekendPause(pair)) return;

        if (!_settings.DatasetCollectionMode && _circuitBreaker.IsHalted())
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long prevTicks = Interlocked.Read(ref _lastCircuitBreakerWarnTicks);
            if (nowTicks - prevTicks >= TimeSpan.FromSeconds(60).Ticks &&
                Interlocked.CompareExchange(ref _lastCircuitBreakerWarnTicks, nowTicks, prevTicks) == prevTicks)
            {
                _logger.LogWarning($"[AutoScanner] Circuit Breaker Active ({_circuitBreaker.GetHaltedReason()}). Routing scans to SHADOW mode for continuous dataset collection.");
            }
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
            var userSettings = await UserRepository.GetSettingsAsync(0);

            int minCandles = _minCandlesPerTf.TryGetValue(tf, out int mc) ? mc : 80;
            var recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 161);
            
            if (recentCandles.Length >= minCandles)
            {
                var lastCandleTime = recentCandles[^1].Timestamp;
                if ((DateTime.UtcNow - lastCandleTime).TotalSeconds <= 30)
                {
                    _logger.LogInformation($"[AutoScanner-Fast] Scanning {pair} on {tf} ({recentCandles.Length}/{minCandles} candles)...");
                    await orchestrator.ExecuteAnalysisAsync(AnalysisAsset(pair), tf, userSettings);
                }
                else
                {
                    _logger.LogWarning($"[AutoScanner-Fast] Stale candles for {pair}/{tf}: last={lastCandleTime:HH:mm:ss}. Skipping.");
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is Npgsql.NpgsqlException || ex is System.IO.IOException || ex is System.Net.Sockets.SocketException || ex is TimeoutException || ex.Message.Contains("reading from stream", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"[AutoScanner-Fast] Transient DB/network jitter: {ex.Message}");
            }
            else if (ex.Message.Contains("Заблокировано", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Blocked", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"[AutoScanner-Fast] Blocked: {ex.Message}");
            }
            else
            {
                _logger.LogError(ex, $"[AutoScanner-Fast] Exception: {ex.Message}");
            }
        }
    }

    private async Task RunMinuteScannerAsync(CancellationToken stoppingToken)
    {
        if (PriceFeed.UsePocketOption)
        {
            _logger.LogInformation("[AutoScanner] PO mode: 1m is scanned from the Pocket Option feed; Tiingo REST minute loop disabled.");
            return;
        }

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
                if (ex is Npgsql.NpgsqlException || ex is System.IO.IOException || ex is System.Net.Sockets.SocketException || ex is TimeoutException || ex.Message.Contains("reading from stream", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"[AutoScanner-Slow] Transient DB/network jitter: {ex.Message}");
                }
                else
                {
                    _logger.LogError(ex, $"[AutoScanner-Slow] Exception: {ex.Message}");
                }
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

    private bool IsWeekendPause(string? pair = null)
    {
        // OTC pairs and Pocket Option streams operate 24/7 without weekend pause
        if (PriceFeed.UsePocketOption || (pair != null && pair.Contains("OTC", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var dayOfWeek = DateTime.UtcNow.DayOfWeek;
        return dayOfWeek == DayOfWeek.Saturday ||
               (dayOfWeek == DayOfWeek.Sunday && DateTime.UtcNow.Hour < 21) ||
               (dayOfWeek == DayOfWeek.Friday && DateTime.UtcNow.Hour >= 21);
    }
}
