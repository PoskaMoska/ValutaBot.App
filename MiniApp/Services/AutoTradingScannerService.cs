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
    public static void ResetLastScannedTimes() => _lastScannedCandleTimes.Clear();

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

    /// <summary>
    /// Returns true when the close of a closed 5s bar sits on an off-grid intermediate 5s phase
    /// for <paramref name="targetTfSecs"/> (e.g. :05, :15 for s10; :05, :10, :20, :25 for s15),
    /// so rolling s10/s15 scans trigger every 5 seconds without double-firing on native grid closes.
    /// </summary>
    internal static bool ShouldEnqueueRollingStepFromS5(DateTime closedS5OpenUtc, int targetTfSecs)
    {
        if (targetTfSecs <= 5 || targetTfSecs % 5 != 0)
            return false;

        var utcOpen = closedS5OpenUtc.Kind == DateTimeKind.Utc ? closedS5OpenUtc : closedS5OpenUtc.ToUniversalTime();
        long closeUnixSec = new DateTimeOffset(utcOpen.AddSeconds(5)).ToUnixTimeSeconds();
        long mod = ((closeUnixSec % targetTfSecs) + targetTfSecs) % targetTfSecs;
        return mod != 0;
    }

    /// <summary>
    /// Aggregates closed 5s OHLC candles into rolling <paramref name="tfSecs"/>-second bars ending cleanly
    /// at <paramref name="anchorCloseUtc"/> (matching Python's phase-shifted resample_s5_grid logic).
    /// Only complete buckets containing all <c>tfSecs / 5</c> consecutive s5 bars are emitted.
    /// </summary>
    internal static MiniAppController.OhlcCandle[] BuildRollingCandlesFromS5(
        ReadOnlySpan<MiniAppController.OhlcCandle> s5Candles,
        int tfSecs,
        DateTime anchorCloseUtc,
        int maxCount = 160)
    {
        if (s5Candles.IsEmpty || tfSecs <= 5 || tfSecs % 5 != 0 || maxCount <= 0)
            return Array.Empty<MiniAppController.OhlcCandle>();

        int barsNeeded = tfSecs / 5;
        DateTime anchorUtc = anchorCloseUtc.Kind == DateTimeKind.Utc ? anchorCloseUtc : anchorCloseUtc.ToUniversalTime();
        long anchorUnixSec = new DateTimeOffset(anchorUtc).ToUnixTimeSeconds();
        long cutoffUnixMs = anchorUnixSec * 1000L + 250L;
        long phaseSec = ((anchorUnixSec % tfSecs) + tfSecs) % tfSecs;

        // Deduplicate & keep strictly closed s5 bars ascending by open timestamp
        var closedBySec = new SortedDictionary<long, MiniAppController.OhlcCandle>();
        for (int i = 0; i < s5Candles.Length; i++)
        {
            var c = s5Candles[i];
            if (!double.IsFinite(c.Open) || !double.IsFinite(c.High) || !double.IsFinite(c.Low) || !double.IsFinite(c.Close) || c.Close <= 0)
                continue;
            var tsUtc = c.Timestamp.Kind == DateTimeKind.Utc ? c.Timestamp : c.Timestamp.ToUniversalTime();
            long closeMs = new DateTimeOffset(tsUtc.AddSeconds(5)).ToUnixTimeMilliseconds();
            if (closeMs > cutoffUnixMs)
                continue;
            long openSec = new DateTimeOffset(tsUtc).ToUnixTimeSeconds();
            closedBySec[openSec] = tsUtc == c.Timestamp ? c : c with { Timestamp = tsUtc };
        }

        if (closedBySec.Count < barsNeeded)
            return Array.Empty<MiniAppController.OhlcCandle>();

        static long FloorDiv(long a, long d) => a >= 0 ? a / d : ((a - d + 1) / d);

        var rolling = new List<MiniAppController.OhlcCandle>(closedBySec.Count / barsNeeded + 1);
        long activeBucket = long.MinValue;
        double bOpen = 0, bHigh = 0, bLow = 0, bClose = 0, bVol = 0, bCrowd = 50.0;
        long prevBarSec = 0;
        int countInBucket = 0;

        void FlushIfComplete()
        {
            if (countInBucket == barsNeeded && activeBucket != long.MinValue && activeBucket + tfSecs <= anchorUnixSec)
            {
                var bucketOpenUtc = DateTimeOffset.FromUnixTimeSeconds(activeBucket).UtcDateTime;
                rolling.Add(new MiniAppController.OhlcCandle(bOpen, bHigh, bLow, bClose, bVol, bucketOpenUtc, bCrowd));
            }
        }

        foreach (var kv in closedBySec)
        {
            long openSec = kv.Key;
            var bar = kv.Value;
            long bucket = FloorDiv(openSec - phaseSec, tfSecs) * tfSecs + phaseSec;

            if (bucket != activeBucket)
            {
                FlushIfComplete();
                activeBucket = bucket;
                bOpen = bar.Open;
                bHigh = bar.High;
                bLow = bar.Low;
                bClose = bar.Close;
                bVol = bar.Volume;
                bCrowd = bar.CrowdSentiment;
                prevBarSec = openSec;
                countInBucket = 1;
            }
            else if (openSec - prevBarSec == 5)
            {
                if (bar.High > bHigh) bHigh = bar.High;
                if (bar.Low < bLow) bLow = bar.Low;
                bClose = bar.Close;
                bVol += bar.Volume;
                bCrowd = bar.CrowdSentiment;
                prevBarSec = openSec;
                countInBucket++;
            }
            else
            {
                // Gap inside bucket invalidates partial aggregation; reset state for next bucket
                countInBucket = -1;
            }
        }
        FlushIfComplete();

        if (rolling.Count <= maxCount)
            return rolling.ToArray();

        return rolling.GetRange(rolling.Count - maxCount, maxCount).ToArray();
    }

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

        _logger.LogInformation("[AutoScanner] Event-driven Pocket Option scanner supervisor initialized (s5..m1).");
        ResetLastScannedTimes(); // Clean start: clear any stale in-memory deduplication keys
        int consecutiveFailures = 0;
        long lastCrashAlertUtcTicks = 0;
        try
        {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSubminuteScannerAsync(stoppingToken);
                consecutiveFailures = 0; // Сброс при чистом завершении (если канал был пересоздан)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                ResetLastScannedTimes(); // Clear deduplication cache so resurrected loop rescans fresh candles
                int delaySec = Math.Min(consecutiveFailures * 2, 30);
                _logger.LogError(ex, "[AutoScanner] Scanner loop crashed (failure #{Count}). Resuscitating scanner in {Delay}s...", consecutiveFailures, delaySec);
                long nowUtcTicks = DateTime.UtcNow.Ticks;
                if (nowUtcTicks - lastCrashAlertUtcTicks >= TimeSpan.FromMinutes(5).Ticks)
                {
                    lastCrashAlertUtcTicks = nowUtcTicks;
                    BotLogger.NotifyAdmin($"[AutoScanner] Аварийный перезапуск цикла сканера (сбой #{consecutiveFailures}, рестарт через {delaySec}с): {ex.Message}");
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        }
        finally
        {
            ResetLastScannedTimes();
            _logger.LogInformation("[AutoScanner] Scanner service stopped cleanly.");
        }
    }

    private async Task RunSubminuteScannerAsync(CancellationToken stoppingToken)
    {
        // D2 fix: monotonic Stopwatch timestamp instead of DateTime.UtcNow for queue wait calculation
        var channel = Channel.CreateUnbounded<(string pair, string tf, long queuedSw)>();
        var queuedKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var inFlightKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        void EnqueueScan(string pair, string targetTf, long ts)
        {
            string key = $"{pair}:{targetTf}";
            if (queuedKeys.TryAdd(key, 1))
            {
                if (!channel.Writer.TryWrite((pair, targetTf, ts)))
                {
                    queuedKeys.TryRemove(key, out _);
                }
            }
        }

        Action<string, string> candleClosedHandler = (pair, tf) =>
        {
            if (_targetPairs.Contains(pair, StringComparer.OrdinalIgnoreCase) && _subminuteTfs.Contains(tf, StringComparer.OrdinalIgnoreCase))
            {
                long ts = Stopwatch.GetTimestamp();
                EnqueueScan(pair, tf, ts);

                if (tf.Equals("s5", StringComparison.OrdinalIgnoreCase)
                    && RealtimeTickCollector.TryGetLatestClosedCandleTimeUtc(pair, "s5", out var closedS5OpenUtc))
                {
                    if (ShouldEnqueueRollingStepFromS5(closedS5OpenUtc, 10))
                        EnqueueScan(pair, "s10", ts);
                    if (ShouldEnqueueRollingStepFromS5(closedS5OpenUtc, 15))
                        EnqueueScan(pair, "s15", ts);
                }
            }
        };

        RealtimeTickCollector.OnCandleClosed += candleClosedHandler;
        try
        {
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
                                await Task.Delay(15, stoppingToken);
                                if (Stopwatch.GetElapsedTime(item.queuedSw).TotalSeconds <= maxQueueWaitSec && queuedKeys.TryAdd(key, 1))
                                {
                                    if (!channel.Writer.TryWrite(item)) queuedKeys.TryRemove(key, out _);
                                }
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
                        channel.Writer.TryComplete(ex);
                        throw;
                    }
                }, stoppingToken));
            }

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

        bool isRollingWindowTf = tfSecs is 10 or 15;
        long bucketTicks = isRollingWindowTf
            ? TimeSpan.FromSeconds(5).Ticks
            : TimeSpan.FromSeconds(tfSecs).Ticks;
        DateTime currentCandleOpen = new DateTime(refTime.Ticks - (refTime.Ticks % bucketTicks), DateTimeKind.Utc);
        bool isShiftedRollingPhase = isRollingWindowTf
            && (currentCandleOpen.Ticks % TimeSpan.FromSeconds(tfSecs).Ticks) != 0;

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
            MiniAppController.OhlcCandle[] recentCandles;
            if (isShiftedRollingPhase)
            {
                int s5Limit = tfSecs == 10 ? 322 : 362;
                var s5Raw = await RealtimeTickCollector.GetRecentCandles(pair, "s5", s5Limit);
                recentCandles = BuildRollingCandlesFromS5(s5Raw, tfSecs, currentCandleOpen, 160);
            }
            else
            {
                recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 161);
            }

            if (recentCandles.Length >= minCandles)
            {
                // GetRecentCandles appends the newly-opened 1-tick live candle at [^1] right after OnCandleClosed.
                // Select the actual just-closed candle for deduplication and close-age calculation:
                bool isLastClosed = recentCandles[^1].Timestamp.AddSeconds(tfSecs) <= refTime.AddMilliseconds(250);
                var targetClosedCandle = isLastClosed
                    ? recentCandles[^1]
                    : (recentCandles.Length > 1 ? recentCandles[^2] : recentCandles[^1]);

                var lastCandleTime = targetClosedCandle.Timestamp;

                // Atomic deduplication barrier against scanning the same closed candle
                string scanKey = $"{pair}:{tf}";
                if (_lastScannedCandleTimes.TryGetValue(scanKey, out var prevScannedTime) && prevScannedTime >= lastCandleTime && (prevScannedTime - lastCandleTime) <= TimeSpan.FromMinutes(5))
                {
                    _logger.LogDebug($"[AutoScanner] Skipped {scanKey}: closed candle {lastCandleTime:HH:mm:ss.fff} already analyzed.");
                    return;
                }

                // CAS claim on the closed candle timestamp before starting inference
                if (!_lastScannedCandleTimes.TryAdd(scanKey, lastCandleTime) &&
                    !_lastScannedCandleTimes.TryUpdate(scanKey, lastCandleTime, prevScannedTime))
                {
                    _logger.LogDebug($"[AutoScanner] Skipped {scanKey}: closed candle {lastCandleTime:HH:mm:ss.fff} claimed by another worker.");
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

                if (candleAgeFromCloseSec <= maxCloseAgeSec && candleAgeFromCloseSec >= -0.5)
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
