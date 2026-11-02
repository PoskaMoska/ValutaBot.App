using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp.Services;
using Dapper;

namespace ValutaBot.MiniApp;

public class PendingTradeVerificationService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);
    // 5 concurrent verification tasks bounds pool consumption while handling 100+ trades/sec
    private static readonly SemaphoreSlim _verificationSemaphore = new(5);
    // Short-lived cache of trades awaiting broker candle flush to prevent Head-of-Line blocking
    private static readonly ConcurrentDictionary<string, DateTime> _awaitingCandleUntil = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        
        // Сброс зависших блокировок после холодного перезапуска контейнера
        await TradeRepository.ResetAllOrphanedClaimsAsync();
        BotLogger.Info("[PendingVerifier] Started. Sweeping pending trades every 5s for strict grid verification.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SweepExpiredTradesAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Sweep error: {ex.Message}"); }
                await Task.Delay(_checkInterval, stoppingToken);
            }
        }
        finally
        {
            try
            {
                await TradeRepository.ResetAllOrphanedClaimsAsync();
                BotLogger.Info("[PendingVerifier] Gracefully released all claimed_at locks on service shutdown.");
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[PendingVerifier] Failed to release claims on shutdown: {ex.Message}");
            }
            _awaitingCandleUntil.Clear();
        }
    }

    private static async Task SweepExpiredTradesAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var kvp in _awaitingCandleUntil)
        {
            if (kvp.Value < nowUtc) _awaitingCandleUntil.TryRemove(kvp.Key, out _);
        }
        if (_awaitingCandleUntil.Count > 1000) _awaitingCandleUntil.Clear();

        List<SignalTracker.PredictionRecord> expired;
        DateTime currentRefTime = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        // Передаем активные ключи _awaitingCandleUntil, чтобы база НЕ клеймила ожидающие свечей сделки
        var activeAwaitingIds = _awaitingCandleUntil.Where(kvp => kvp.Value > nowUtc).Select(kvp => kvp.Key).ToList();
        try 
        { 
            expired = await TradeRepository.GetPendingTradesToVerifyAsync(
                currentRefTime.AddSeconds(-4), 
                limit: 30, 
                excludeIds: activeAwaitingIds); 
        }
        catch (Exception ex) 
        { 
            BotLogger.Warn($"[PendingVerifier] Could not load pending trades: {ex.Message}"); 
            return; 
        }

        if (expired.Count == 0) return;

        var tasks = new List<Task>();

        foreach (var record in expired)
        {
            if (ct.IsCancellationRequested) break;

            tasks.Add(Task.Run(async () =>
            {
                await _verificationSemaphore.WaitAsync(ct).ConfigureAwait(false);
                try { await VerifyTradeAsync(record, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await TradeRepository.ReleasePendingTradeClaimAsync(record.Id);
                }
                catch (Exception ex)
                {
                    BotLogger.Warn($"[PendingVerifier] Failed to verify {record.Id}: {ex.Message}");
                    await TradeRepository.ReleasePendingTradeClaimAsync(record.Id);
                }
                finally { _verificationSemaphore.Release(); }
            }, ct));
        }
        
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private static async Task VerifyTradeAsync(SignalTracker.PredictionRecord record, CancellationToken ct = default)
    {
        // 1. ПЕРВООЧЕРЕДНАЯ ВАЛИДАЦИЯ EntryPrice (исключает деление на 0 в MFE/MAE и PnL)
        if (record.EntryPrice <= 1e-8 || double.IsNaN(record.EntryPrice) || double.IsInfinity(record.EntryPrice))
        {
            BotLogger.Warn($"[PendingVerifier] Invalid EntryPrice {record.EntryPrice} for trade {record.Id} — discarding.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            _awaitingCandleUntil.TryRemove(record.Id, out _);
            return;
        }

        double? exitPrice = null;
        bool dbFailed = false;
        string verifyInterval = "s5"; 
        
        DateTime currentRefTime = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        double physicalSecondsOverdue = (DateTime.UtcNow - record.VerifyAt.ToUniversalTime()).TotalSeconds;

        DateTime createdUtc = record.CreatedAt.Kind == DateTimeKind.Utc ? record.CreatedAt : record.CreatedAt.ToUniversalTime();
        DateTime verifyUtc = record.VerifyAt.Kind == DateTimeKind.Utc ? record.VerifyAt : record.VerifyAt.ToUniversalTime();
        long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
        DateTime createdBucket = new DateTime(createdUtc.Ticks - (createdUtc.Ticks % s5Ticks), DateTimeKind.Utc);
        DateTime targetGrid = new DateTime(verifyUtc.Ticks - (verifyUtc.Ticks % s5Ticks), DateTimeKind.Utc);
        if (targetGrid <= createdBucket) targetGrid = createdBucket.AddSeconds(5);

        // Settle on Pocket Option OTC candle stream ("{ASSET}_PO").
        string cleanAsset = PriceFeed.CandleKey(record.Asset);

        try
        {
            await using var conn = await ValutaBot.App.MiniApp.Data.DbConnectionFactory.OpenConnectionAsync(ct);

            string minIsoPrimary = targetGrid.AddSeconds(-15).ToString("yyyy-MM-ddTHH:mm:ss");
            string maxIsoPrimary = targetGrid.AddSeconds(5).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

            // Locate the exact s5 candle that closed at the contract expiry boundary targetGrid.
            // Strict fidelity: The candle's close (open_time + 5s) MUST match targetGrid within [-1.5s, +3.0s].
            // Old candles (e.g. from 5s-20s prior) are STRICTLY REJECTED to prevent settling on pre-expiry ticks!
            exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(new CommandDefinition(@"
                SELECT close_price
                FROM (
                    SELECT close_price, open_time
                    FROM subminute_candles
                    WHERE asset = @Asset AND interval = @Interval
                      AND open_time >= @MinIso
                      AND open_time <= @MaxIso
                    ORDER BY open_time DESC
                    LIMIT 5
                ) sub
                WHERE open_time::timestamptz + INTERVAL '5 seconds' >= @TargetGrid - INTERVAL '1500 milliseconds'
                  AND open_time::timestamptz + INTERVAL '5 seconds' <= @TargetGrid + INTERVAL '3000 milliseconds'
                ORDER BY ABS(EXTRACT(EPOCH FROM (open_time::timestamptz + INTERVAL '5 seconds' - @TargetGrid))) ASC
                LIMIT 1
            ", new { 
                Asset = cleanAsset, 
                Interval = verifyInterval, 
                MinIso = minIsoPrimary,
                MaxIso = maxIsoPrimary,
                TargetGrid = targetGrid
            }, cancellationToken: ct));

            // Fast-path In-Memory fallback: if DB writer is buffering ticks (micro-batching lag 250-400ms),
            // inspect the real-time in-memory ring buffer in RealtimeTickCollector directly.
            if (!exitPrice.HasValue || exitPrice.Value <= 1e-8 || double.IsNaN(exitPrice.Value) || double.IsInfinity(exitPrice.Value))
            {
                try
                {
                    var recentCandles = await RealtimeTickCollector.GetRecentCandles(cleanAsset, verifyInterval, 10);
                    if (recentCandles != null && recentCandles.Length > 0)
                    {
                        var match = recentCandles.FirstOrDefault(c =>
                            Math.Abs((c.Timestamp.AddSeconds(5) - targetGrid).TotalSeconds) <= 2.5);
                        if (match != null && match.Close > 1e-8 && !double.IsNaN(match.Close) && !double.IsInfinity(match.Close))
                        {
                            exitPrice = match.Close;
                        }
                    }
                }
                catch (Exception exCollector)
                {
                    BotLogger.Warn($"[PendingVerifier] In-memory RealtimeTickCollector lookup error: {exCollector.Message}");
                }
            }

            // Calculate excursion (MFE / MAE) during trade lifetime [createdBucket, targetGrid] via Index Seek on UNIQUE(asset, interval, open_time)
            if (exitPrice.HasValue && exitPrice.Value > 1e-8 && !double.IsNaN(exitPrice.Value) && !double.IsInfinity(exitPrice.Value))
            {
                try
                {
                    var exc = await conn.QueryFirstOrDefaultAsync<(double? max_high, double? min_low)>(new CommandDefinition(@"
                        SELECT MAX(high_price) as max_high, MIN(low_price) as min_low
                        FROM subminute_candles
                        WHERE asset = @Asset
                          AND interval = @Interval
                          AND open_time >= @CreatedIso
                          AND open_time <= @TargetIso
                    ", new { 
                        Asset = cleanAsset, 
                        Interval = verifyInterval, 
                        CreatedIso = createdBucket.ToString("yyyy-MM-ddTHH:mm:ss"), 
                        TargetIso = targetGrid.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") 
                    }, cancellationToken: ct));

                    double exitVal = exitPrice.Value;
                    double excMax = exc.max_high.HasValue && !double.IsNaN(exc.max_high.Value) && !double.IsInfinity(exc.max_high.Value) 
                        ? exc.max_high.Value 
                        : exitVal;
                    double excMin = exc.min_low.HasValue && !double.IsNaN(exc.min_low.Value) && !double.IsInfinity(exc.min_low.Value) 
                        ? exc.min_low.Value 
                        : exitVal;
                    double maxH = Math.Max(excMax, Math.Max(exitVal, record.EntryPrice));
                    double minL = Math.Min(excMin, Math.Min(exitVal, record.EntryPrice));

                    if (!string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("BUY"))
                    {
                        record.MaxFavorableBps = Math.Round((maxH - record.EntryPrice) / record.EntryPrice * 10000.0, 2);
                        record.MaxAdverseBps = Math.Round((record.EntryPrice - minL) / record.EntryPrice * 10000.0, 2);
                    }
                    else if (!string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("PUT"))
                    {
                        record.MaxFavorableBps = Math.Round((record.EntryPrice - minL) / record.EntryPrice * 10000.0, 2);
                        record.MaxAdverseBps = Math.Round((maxH - record.EntryPrice) / record.EntryPrice * 10000.0, 2);
                    }
                }
                catch (Exception exMfe)
                {
                    BotLogger.Warn($"[PendingVerifier] MFE/MAE calculation warning: {exMfe.Message}");
                }
            }
            dbFailed = false;
        }
        catch (Exception ex)
        {
            dbFailed = true;
            BotLogger.Warn($"[PendingVerifier] DB fetch failed: {ex.Message}");
        }

        if (!exitPrice.HasValue || exitPrice.Value <= 1e-8 || double.IsNaN(exitPrice.Value) || double.IsInfinity(exitPrice.Value))
        {
            if (dbFailed)
            {
                // Освобождаем claimed_at, чтобы сделка сразу повторилась на следующем тике с новым коннектом
                await TradeRepository.ReleasePendingTradeClaimAsync(record.Id);
                _awaitingCandleUntil.TryRemove(record.Id, out _);
                return;
            }

            if (physicalSecondsOverdue >= 120)
            {
                // SRE-FIX: Do NOT silently delete the trade! Archive into trade_outcomes with cancelled status
                // to maintain audit trail and dataset integrity across container restarts/gaps.
                BotLogger.Warn($"[PendingVerifier] REBOOT/GAP TIMEOUT: trade {record.Id} ({record.Asset}/{record.Timeframe}) lacked s5 candles during downtime. Archiving outcome.");

                var staleOutcome = new TradeOutcomeRecord
                {
                    Id = record.Id,
                    Asset = record.Asset,
                    Timeframe = record.Timeframe,
                    Direction = record.Direction,
                    Probability = record.Probability,
                    EntryPrice = record.EntryPrice,
                    ExitPrice = record.EntryPrice,
                    PnlBps = 0.0,
                    WasWin = null, // Cancelled / unresolved due to downtime
                    ReasoningText = "CANCELLED_DUE_TO_CONTAINER_REBOOT_OR_FEED_DOWNTIME",
                    CreatedAt = record.CreatedAt.ToString("o"),
                    VerifiedAt = DateTime.UtcNow.ToString("o"),
                    TaDirection = record.SourceDirections.GetValueOrDefault("TechAnalysis", "NEUTRAL"),
                    MlDirection = record.SourceDirections.GetValueOrDefault("NeuralBrain", record.SourceDirections.GetValueOrDefault("LIGHTGBM", "NEUTRAL")),
                    SmcDirection = record.SourceDirections.GetValueOrDefault("SMC", "NEUTRAL"),
                    DynamicHorizon = record.DynamicHorizon,
                    MarketRegime = record.MarketRegime,
                    VelocityRegime = record.VelocityRegime,
                    AtrAtSignal = record.AtrAtSignal,
                    AdxAtSignal = record.AdxAtSignal,
                    RsiAtSignal = record.RsiAtSignal,
                    HigherTfAligned = record.HigherTfAligned,
                    MinutesToNews = record.MinutesToNews,
                    FeaturesJson = record.FeaturesJson,
                    SmcBosDir = record.SmcBosDir,
                    SmcHasOb = record.SmcHasOb,
                    SmcHasFvg = record.SmcHasFvg,
                    OfDeltaRatio = record.OfDeltaRatio,
                    OfState = record.OfState,
                    TaTelemetry = record.TaTelemetry,
                    MlTelemetry = record.MlTelemetry,
                    SmcTelemetry = record.SmcTelemetry
                };

                await TradeRepository.SaveTradeOutcomeAsync(staleOutcome, record.Id);
                _awaitingCandleUntil.TryRemove(record.Id, out _);
            }
            else
            {
                // Broker candle is in transit (WebSocket latency or DB flush delay).
                // Protect fidelity: wait for the correct expiry candle rather than picking an older bar!
                _awaitingCandleUntil[record.Id] = DateTime.UtcNow.AddSeconds(3);
                await TradeRepository.ReleasePendingTradeClaimAsync(record.Id);
            }
            return;
        }

        _awaitingCandleUntil.TryRemove(record.Id, out _);

        // Guard: NEUTRAL / SHADOW_NEUTRAL сигнал не несёт обучающей информации
        if (record.Direction is "NEUTRAL" or "SHADOW_NEUTRAL")
        {
            BotLogger.Warn($"[PendingVerifier] {record.Direction} direction for {record.Id} — skipping, deleting.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            return;
        }

        double rawPriceDiff = (exitPrice.Value - record.EntryPrice) / record.EntryPrice;
        bool isExactDoji = Math.Abs(rawPriceDiff) < 1e-8; // entry == exit
        
        record.ExitPrice = exitPrice.Value;
        
        bool isBuy = !string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("BUY");
        bool isPut = !string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("PUT");

        if (record.Direction is "HOLD" or "SHADOW_HOLD") 
        {
            record.WasCorrect = null; // Neutral HOLD sample
            record.PnlBps = 0.0;
        } 
        else if (isExactDoji)
        {
            // POCKET OPTION AT-THE-MONEY (ATM) TIE RULE:
            // In binary options on Pocket Option, an exact tie (Entry == Exit) is a refund, NOT a win.
            // WasWin MUST be false for BUY and PUT contracts, with PnL = 0 bps.
            record.WasCorrect = false;
            record.PnlBps = 0.0;
        }
        else 
        {
            record.WasCorrect = (isBuy && exitPrice.Value > record.EntryPrice)
                             || (isPut && exitPrice.Value < record.EntryPrice);

            // Financial PnL Invariant:
            // BUY: Profit when Exit > Entry
            // PUT: Profit when Exit < Entry (rawPriceDiff is negative, so pnl = -rawPriceDiff * 10000)
            double effectivePnlDiff = isPut ? -rawPriceDiff : rawPriceDiff;
            record.PnlBps = Math.Round(effectivePnlDiff * 10000.0, 2);
        }

        await TradeOutcomeTracker.OnTradeVerifiedAsync(record);
        // Note: SaveTradeOutcomeAsync already deletes pending trade by ID atomically in the same SQL batch

        string resultStr = record.WasCorrect.HasValue 
            ? (record.WasCorrect.Value ? "WIN" : (isExactDoji ? "TIE(LOSS)" : "LOSS")) 
            : "HOLD";
        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {resultStr} @ {exitPrice.Value} (Target: {record.VerifyAt:HH:mm:ss}, PnL: {record.PnlBps:+#0.00;-#0.00;0.00} bps)");
    }
}
