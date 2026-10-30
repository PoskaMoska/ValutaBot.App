using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Background service that actively scans Pocket Option OTC pairs across s5..m1 horizons
/// on candle-close events emitted by RealtimeTickCollector.
/// </summary>
public class AutoTradingScannerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoTradingScannerService> _logger;
    private readonly ICircuitBreakerService _circuitBreaker;
    private readonly TradingBotSettings _settings;
    private static readonly string[] _basePairs = { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF" };

    // Candle keys the scanner listens to ("EURUSD_PO").
    private static readonly string[] _targetPairs = _basePairs.Select(PriceFeed.CandleKey).ToArray();

    // All 5 target horizons (5s, 10s, 15s, 30s, 1m) come from the PO tick feed, event-driven.
    private static readonly string[] _subminuteTfs = { "s5", "s10", "s15", "s30", "m1" };

    private static readonly ConcurrentDictionary<string, DateTime> _lastScannedCandleTimes = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> _minCandlesPerTf =
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

    private static int TfToSeconds(string tf) => tf.ToLowerInvariant() switch
    {
        "s5"  => 5,
        "s10" => 10,
        "s15" => 15,
        "s30" => 30,
        _     => 60
    };

    /// <summary>
    /// Admission window (seconds from bar open) before inference starts.
    /// Leaves a 250-400ms budget for ML inference before Orchestrator ExpiryGuard (1.3s/2.2s/2.8s/3.8s/5.0s).
    /// </summary>
    private static double GetAdmitWindowSec(int tfSecs) => tfSecs switch
    {
        5  => 1.0,
        10 => 1.8,
        15 => 2.3,
        30 => 3.2,
        _  => 4.5
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

        _logger.LogInformation("[AutoScanner] Event-driven Pocket Option scanner started (s5..m1).");
        await RunSubminuteScannerAsync(stoppingToken);
    }

    private async Task RunSubminuteScannerAsync(CancellationToken stoppingToken)
    {
        // D2 fix: monotonic Stopwatch timestamp instead of DateTime.UtcNow for queue wait calculation
        var channel = Channel.CreateUnbounded<(string pair, string tf, long queuedSw)>();
        var queuedKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var inFlightKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        Action<string, string> candleClosedHandler = (pair, tf) =>
        {
            if (_targetPairs.Contains(pair, StringComparer.OrdinalIgnoreCase) && _subminuteTfs.Contains(tf, StringComparer.OrdinalIgnoreCase))
            {
                string key = $"{pair}:{tf}";
                if (queuedKeys.TryAdd(key, 1))
                {
                    if (!channel.Writer.TryWrite((pair, tf, Stopwatch.GetTimestamp())))
                    {
                        queuedKeys.TryRemove(key, out _);
                    }
                }
            }
        };

        RealtimeTickCollector.OnCandleClosed += candleClosedHandler;

        var workers = new List<Task>();
        for (int i = 0; i < 6; i++) // 6 concurrent workers matching all 6 monitored OTC currency pairs
        {
            workers.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in channel.Reader.ReadAllAsync(stoppingToken))
                    {
                        string key = $"{item.pair}:{item.tf}";
                        queuedKeys.TryRemove(key, out _);
                        if (stoppingToken.IsCancellationRequested) break;

                        // D2 & D10 fix: drop stale events using monotonic Stopwatch before any scope/DB allocation
                        int tfSecs = TfToSeconds(item.tf);
                        double maxQueueWaitSec = GetAdmitWindowSec(tfSecs);
                        if (Stopwatch.GetElapsedTime(item.queuedSw).TotalSeconds > maxQueueWaitSec)
                        {
                            continue;
                        }
                        if (!inFlightKeys.TryAdd(key, 1))
                        {
                            continue;
                        }
                        try
                        {
                            await ProcessSubminuteScanAsync(item.pair, item.tf, item.queuedSw, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[AutoScanner] Worker encountered unexpected error on {Pair}/{Tf}: {Message}", item.pair, item.tf, ex.Message);
                        }
                        finally
                        {
                            inFlightKeys.TryRemove(key, out _);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "[AutoScanner] Fatal error in scanner worker: {Message}", ex.Message);
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

    private async Task ProcessSubminuteScanAsync(string pair, string tf, long queuedSw, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        // D10 fix: fast non-allocating guards (Dead Man's Switch & entry window) BEFORE CreateScope / DB queries
        if (!PocketOptionWebSocketStream.IsSymbolFeedAlive(pair, maxIdleSeconds: 2.0))
        {
            _logger.LogWarning($"[AutoScanner-Fast] Skipped {pair}/{tf}: symbol tick feed is stale (>2.0s idle).");
            return;
        }

        int tfSecs = TfToSeconds(tf);
        DateTime refTime = PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;

        long bucketTicks = TimeSpan.FromSeconds(tfSecs).Ticks;
        DateTime currentCandleOpen = new DateTime(refTime.Ticks - (refTime.Ticks % bucketTicks), DateTimeKind.Utc);

        // D3 defense-in-depth: elapsed time since bar open cannot be smaller than monotonic time spent in queue
        double elapsedFromOpen = Math.Max(
            (refTime - currentCandleOpen).TotalSeconds,
            Stopwatch.GetElapsedTime(queuedSw).TotalSeconds);

        double maxOpenElapsed = GetAdmitWindowSec(tfSecs);
        if (elapsedFromOpen > maxOpenElapsed)
        {
            _logger.LogWarning($"[AutoScanner] Skipped {pair}/{tf}: open window elapsed (+{elapsedFromOpen:F2}s > {maxOpenElapsed:F1}s).");
            return;
        }

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
            stoppingToken.ThrowIfCancellationRequested();
            int minCandles = _minCandlesPerTf.TryGetValue(tf, out int mc) ? mc : 80;
            var recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 161);

            if (recentCandles.Length >= minCandles)
            {
                var lastCandleTime = recentCandles[^1].Timestamp;

                // Atomic deduplication barrier against scanning the same candle
                string scanKey = $"{pair}:{tf}";
                if (_lastScannedCandleTimes.TryGetValue(scanKey, out var prevScannedTime) && prevScannedTime >= lastCandleTime)
                {
                    _logger.LogDebug($"[AutoScanner] Skipped {scanKey}: candle {lastCandleTime:HH:mm:ss.fff} already analyzed.");
                    return;
                }

                // CAS claim on the candle timestamp before starting inference
                if (!_lastScannedCandleTimes.TryAdd(scanKey, lastCandleTime) &&
                    !_lastScannedCandleTimes.TryUpdate(scanKey, lastCandleTime, prevScannedTime))
                {
                    _logger.LogDebug($"[AutoScanner] Skipped {scanKey}: candle {lastCandleTime:HH:mm:ss.fff} claimed by another worker.");
                    return;
                }

                // D9 fix: measure staleness from candle CLOSE (lastCandleTime + tfSecs), not candle open
                DateTime lastCandleClose = lastCandleTime.AddSeconds(tfSecs);
                double candleAgeFromCloseSec = (refTime - lastCandleClose).TotalSeconds;
                double maxCloseAgeSec = tfSecs switch
                {
                    5  => 2.5,
                    10 => 4.0,
                    15 => 6.0,
                    30 => 10.0,
                    _  => 15.0
                };

                if (candleAgeFromCloseSec <= maxCloseAgeSec)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
                    var userSettings = await UserRepository.GetSettingsAsync(0);

                    _logger.LogInformation($"[AutoScanner-Fast] Scanning {pair} on {tf} ({recentCandles.Length}/{minCandles} candles, +{elapsedFromOpen:F2}s into bar)...");
                    await orchestrator.ExecuteAnalysisAsync(AnalysisAsset(pair), tf, userSettings, recentCandles);
                }
                else
                {
                    _logger.LogWarning($"[AutoScanner-Fast] Stale candles for {pair}/{tf}: close={lastCandleClose:HH:mm:ss}, refTime={refTime:HH:mm:ss} (age {candleAgeFromCloseSec:F1}s > limit {maxCloseAgeSec:F1}s). Skipping.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
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
}
