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
        try { expired = await TradeRepository.GetPendingTradesToVerifyAsync(DateTime.UtcNow); }
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
            string cleanAsset = record.Asset.ToUpper().Replace("/", "").Replace("-", "").Replace(" OTC", "").Replace("_OTC", "");
            using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            
            // First try to find a subminute candle (s5, s10, etc.) for high precision
            // To avoid horizon skew, we must get the exact price at VerifyAt.
            // If the candle's open_time == VerifyAt, the exact price is its open_price.
            // If the returned candle is the preceding one, the exact price is its close_price.
            exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(@"
                SELECT 
                    CASE 
                        WHEN open_time = @VerifyAt THEN open_price 
                        ELSE close_price 
                    END
                FROM subminute_candles
                WHERE asset = @Asset AND interval = @Interval
                  AND open_time <= @VerifyAt
                ORDER BY open_time DESC LIMIT 1
            ", new { 
                Asset = cleanAsset, 
                Interval = verifyInterval, 
                VerifyAt = record.VerifyAt.ToString("O")
            });

            // If subminute is missing (e.g., scraper stopped), fallback to historical_candles (1m)
            if (!exitPrice.HasValue || exitPrice.Value <= 0)
            {
                exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(@"
                    SELECT 
                        CASE 
                            WHEN open_time = @VerifyAt THEN ""open"" 
                            ELSE ""close"" 
                        END
                    FROM historical_candles
                    WHERE asset = @Asset
                      AND open_time <= @VerifyAt
                    ORDER BY open_time DESC LIMIT 1
                ", new { 
                    Asset = cleanAsset, 
                    VerifyAt = record.VerifyAt.ToString("O")
                });
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

        // Guard: NEUTRAL сигнал не несёт обучающей информации
        if (record.Direction == "NEUTRAL")
        {
            BotLogger.Warn($"[PendingVerifier] NEUTRAL direction for {record.Id} — skipping, deleting.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            return;
        }

        bool isExactDoji = Math.Abs(priceDiff) < 1e-8; // entry == exit
        
        record.ExitPrice = exitPrice.Value;
        record.PnlBps = Math.Round(priceDiff * 10000, 2);
        
        if (isExactDoji) {
            record.WasCorrect = null; // TIE
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
