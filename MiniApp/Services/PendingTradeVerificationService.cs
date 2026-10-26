using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data.Repositories;
using Dapper;

namespace ValutaBot.MiniApp;

public class PendingTradeVerificationService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        BotLogger.Info("[PendingVerifier] Started. Sweeping pending trades every 5s for strict grid verification.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepExpiredTradesAsync(stoppingToken); }
            catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Sweep error: {ex.Message}"); }
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private static async Task SweepExpiredTradesAsync(CancellationToken ct)
    {
        List<SignalTracker.PredictionRecord> expired;
        // Wait 4s after VerifyAt so RealtimeTickCollector's 3s batch buffer has guaranteed flushed the expiry s5 candle to PostgreSQL.
        try { expired = await TradeRepository.GetPendingTradesToVerifyAsync(DateTime.UtcNow.AddSeconds(-4)); }
        catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Could not load pending trades: {ex.Message}"); return; }

        if (expired.Count == 0) return;

        var semaphore = new SemaphoreSlim(20);
        var tasks = new List<Task>();

        foreach (var record in expired)
        {
            if (ct.IsCancellationRequested) break;
            
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try { await VerifyTradeAsync(record); }
                catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Failed to verify {record.Id}: {ex.Message}"); }
                finally { semaphore.Release(); }
            }, ct));
        }
        
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private static async Task VerifyTradeAsync(SignalTracker.PredictionRecord record)
    {
        double? exitPrice = null;
        string verifyInterval = "s5"; 
        
        double secondsOverdue = (DateTime.UtcNow - record.VerifyAt).TotalSeconds;

        try
        {
            // Settle on the SAME feed the signal was built from: "EURUSD_PO" in Pocket Option mode, "EURUSD" for Tiingo.
            string cleanAsset = PriceFeed.CandleKey(record.Asset);
            using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            
            // Physical duration expiry: verifyUtc is the exact moment the contract expires.
            DateTime createdUtc = record.CreatedAt.Kind == DateTimeKind.Utc ? record.CreatedAt : record.CreatedAt.ToUniversalTime();
            DateTime verifyUtc = record.VerifyAt.Kind == DateTimeKind.Utc ? record.VerifyAt : record.VerifyAt.ToUniversalTime();
            long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
            DateTime createdBucket = new DateTime(createdUtc.Ticks - (createdUtc.Ticks % s5Ticks), DateTimeKind.Utc);
            DateTime targetGrid = new DateTime(verifyUtc.Ticks - (verifyUtc.Ticks % s5Ticks), DateTimeKind.Utc);
            if (targetGrid <= createdBucket) targetGrid = createdBucket.AddSeconds(5);
            DateTime minAllowedSub = createdBucket > targetGrid.AddSeconds(-20) ? createdBucket : targetGrid.AddSeconds(-20);

            // Locate the enclosing candle [open_time, open_time + 5s) covering verifyUtc
            exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(@"
                SELECT 
                    CASE 
                        WHEN @VerifyAt - open_time::timestamptz <= INTERVAL '2.5 seconds' THEN open_price 
                        ELSE close_price 
                    END
                FROM subminute_candles
                WHERE asset = @Asset AND interval = @Interval
                  AND open_time::timestamptz <= @VerifyAt
                  AND open_time::timestamptz + INTERVAL '5 seconds' >= @VerifyAt
                ORDER BY open_time DESC
                LIMIT 1
            ", new { 
                Asset = cleanAsset, 
                Interval = verifyInterval, 
                VerifyAt = verifyUtc
            });

            if (!exitPrice.HasValue || exitPrice.Value <= 0)
            {
                // Fallback: nearest candle on or before verifyUtc within allowed window
                exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(@"
                    SELECT close_price
                    FROM subminute_candles
                    WHERE asset = @Asset AND interval = @Interval
                      AND open_time::timestamptz <= @VerifyAt
                      AND open_time::timestamptz >= @MinAllowed
                    ORDER BY open_time DESC
                    LIMIT 1
                ", new { 
                    Asset = cleanAsset, 
                    Interval = verifyInterval, 
                    VerifyAt = verifyUtc,
                    MinAllowed = minAllowedSub
                });
            }

            // If subminute is missing (e.g., scraper stopped), fallback to historical_candles (1m).
            // Not in PO mode: historical_candles holds Tiingo prices, which must never settle an OTC trade.
            if (!PriceFeed.UsePocketOption && (!exitPrice.HasValue || exitPrice.Value <= 0))
            {
                DateTime minAllowedHist = verifyUtc.AddSeconds(-120);
                exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(@"
                    SELECT 
                        CASE 
                            WHEN open_time = @VerifyAt THEN ""open"" 
                            ELSE ""close"" 
                        END
                    FROM historical_candles
                    WHERE asset = @Asset
                      AND open_time <= @VerifyAt
                      AND open_time >= @MinAllowedTime
                    ORDER BY open_time DESC LIMIT 1
                ", new { 
                    Asset = cleanAsset, 
                    VerifyAt = verifyUtc.ToString("O"),
                    MinAllowedTime = minAllowedHist.ToString("O")
                });
            }

            // Calculate excursion (MFE / MAE) during trade lifetime [createdBucket, targetGrid) using indexed s5 candles
            if (exitPrice.HasValue && exitPrice.Value > 0)
            {
                try
                {
                    var exc = await conn.QueryFirstOrDefaultAsync<(double? max_high, double? min_low)>(@"
                        SELECT MAX(high_price) as max_high, MIN(low_price) as min_low
                        FROM subminute_candles
                        WHERE asset = @Asset
                          AND interval = @Interval
                          AND open_time >= @CreatedBucket
                          AND open_time <  @TargetGrid
                    ", new { 
                        Asset = cleanAsset, 
                        Interval = verifyInterval,
                        CreatedBucket = createdBucket.ToString("O"), 
                        TargetGrid = targetGrid.ToString("O") 
                    });

                    double exitVal = exitPrice.Value;
                    double maxH = exc.max_high ?? exitVal;
                    double minL = exc.min_low ?? exitVal;

                    if (maxH < exitVal) maxH = exitVal;
                    if (minL > exitVal) minL = exitVal;
                    if (maxH < record.EntryPrice) maxH = record.EntryPrice;
                    if (minL > record.EntryPrice) minL = record.EntryPrice;

                    if (!string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("BUY"))
                    {
                        record.MaxFavorableBps = Math.Round((maxH - record.EntryPrice) / record.EntryPrice * 10000, 2);
                        record.MaxAdverseBps = Math.Round((record.EntryPrice - minL) / record.EntryPrice * 10000, 2);
                    }
                    else if (!string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("PUT"))
                    {
                        record.MaxFavorableBps = Math.Round((record.EntryPrice - minL) / record.EntryPrice * 10000, 2);
                        record.MaxAdverseBps = Math.Round((maxH - record.EntryPrice) / record.EntryPrice * 10000, 2);
                    }
                }
                catch (Exception exMfe)
                {
                    BotLogger.Warn($"[PendingVerifier] MFE/MAE calculation warning: {exMfe.Message}");
                }
            }
        }
        catch (Exception ex)
        {
             BotLogger.Warn($"[PendingVerifier] DB fetch failed: {ex.Message}");
        }

        if (!exitPrice.HasValue || exitPrice.Value <= 0)
        {
            // FIX D-1: Removed duplicate null-check block that was dead code.
            // Previous logic: first block handled >120 (discard) and <120 (wait) but left
            // ==120 unhandled, falling through to a second block with a wrong >60 threshold.
            // New logic: wait up to 120 seconds, then discard.
            if (secondsOverdue >= 120)
            {
                BotLogger.Warn($"[PendingVerifier] STALE trade {record.Id} ({record.Asset}/{record.Timeframe}): no DB candle after {secondsOverdue:F0}s, discarding.");
                await TradeRepository.DeletePendingTradeAsync(record.Id);
            }
            // else: still within 120s window — wait for next sweep cycle
            return;
        }

        double priceDiff = (exitPrice.Value - record.EntryPrice) / record.EntryPrice;

        // Guard: NEUTRAL / SHADOW_NEUTRAL сигнал не несёт обучающей информации
        if (record.Direction is "NEUTRAL" or "SHADOW_NEUTRAL")
        {
            BotLogger.Warn($"[PendingVerifier] {record.Direction} direction for {record.Id} — skipping, deleting.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            return;
        }

        bool isExactDoji = Math.Abs(priceDiff) < 1e-8; // entry == exit
        
        record.ExitPrice = exitPrice.Value;
        record.PnlBps = Math.Round(priceDiff * 10000, 2);
        
        if (isExactDoji || record.Direction is "HOLD" or "SHADOW_HOLD") {
            record.WasCorrect = null; // TIE or neutral HOLD sample
        } else {
            record.WasCorrect = (record.Direction.EndsWith("BUY") && exitPrice.Value > record.EntryPrice)
                             || (record.Direction.EndsWith("PUT") && exitPrice.Value < record.EntryPrice);
        }

        await TradeOutcomeTracker.OnTradeVerifiedAsync(record);
        await TradeRepository.DeletePendingTradeAsync(record.Id);

        string resultStr = record.WasCorrect.HasValue ? (record.WasCorrect.Value ? "WIN" : "LOSS") : "TIE";
        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {resultStr} @ {exitPrice.Value} (Target: {record.VerifyAt:HH:mm:ss})");
    }
}
