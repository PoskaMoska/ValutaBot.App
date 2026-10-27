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
    private static readonly SemaphoreSlim _verificationSemaphore = new(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        BotLogger.Info("[PendingVerifier] Started. Sweeping pending trades every 5s for strict grid verification.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepExpiredTradesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Sweep error: {ex.Message}"); }
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private static async Task SweepExpiredTradesAsync(CancellationToken ct)
    {
        List<SignalTracker.PredictionRecord> expired;
        DateTime currentRefTime = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        // Wait 4s after VerifyAt so RealtimeTickCollector's 3s batch buffer has guaranteed flushed the expiry s5 candle to PostgreSQL.
        try { expired = await TradeRepository.GetPendingTradesToVerifyAsync(currentRefTime.AddSeconds(-4)); }
        catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Could not load pending trades: {ex.Message}"); return; }

        if (expired.Count == 0) return;

        var tasks = new List<Task>();

        foreach (var record in expired)
        {
            if (ct.IsCancellationRequested) break;
            
            tasks.Add(Task.Run(async () =>
            {
                await _verificationSemaphore.WaitAsync(ct).ConfigureAwait(false);
                try { await VerifyTradeAsync(record, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Failed to verify {record.Id}: {ex.Message}"); }
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
            return;
        }

        double? exitPrice = null;
        string verifyInterval = "s5"; 
        
        DateTime currentRefTime = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        double secondsOverdue = (currentRefTime - record.VerifyAt).TotalSeconds;

        try
        {
            // Settle on the SAME feed the signal was built from: "EURUSD_PO" in Pocket Option mode, "EURUSD" for Tiingo.
            string cleanAsset = PriceFeed.CandleKey(record.Asset);
            await using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
            await conn.OpenAsync(ct);
            
            // Physical duration expiry: verifyUtc is the exact moment the contract expires.
            DateTime createdUtc = record.CreatedAt.Kind == DateTimeKind.Utc ? record.CreatedAt : record.CreatedAt.ToUniversalTime();
            DateTime verifyUtc = record.VerifyAt.Kind == DateTimeKind.Utc ? record.VerifyAt : record.VerifyAt.ToUniversalTime();
            long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
            DateTime createdBucket = new DateTime(createdUtc.Ticks - (createdUtc.Ticks % s5Ticks), DateTimeKind.Utc);
            DateTime targetGrid = new DateTime(verifyUtc.Ticks - (verifyUtc.Ticks % s5Ticks), DateTimeKind.Utc);
            if (targetGrid <= createdBucket) targetGrid = createdBucket.AddSeconds(5);
            DateTime minAllowedSub = createdBucket > targetGrid.AddSeconds(-20) ? createdBucket : targetGrid.AddSeconds(-20);

            // Locate the enclosing candle [open_time, open_time + 5s) covering verifyUtc
            exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(new CommandDefinition(@"
                SELECT close_price
                FROM subminute_candles
                WHERE asset = @Asset AND interval = @Interval
                  AND open_time::timestamptz < @VerifyAt
                  AND open_time::timestamptz + INTERVAL '5 seconds' >= @VerifyAt
                ORDER BY open_time DESC
                LIMIT 1
            ", new { 
                Asset = cleanAsset, 
                Interval = verifyInterval, 
                VerifyAt = verifyUtc
            }, cancellationToken: ct));

            if (!exitPrice.HasValue || exitPrice.Value <= 1e-8 || double.IsNaN(exitPrice.Value) || double.IsInfinity(exitPrice.Value))
            {
                // Fallback: nearest candle on or before verifyUtc within allowed window
                exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(new CommandDefinition(@"
                    SELECT close_price
                    FROM subminute_candles
                    WHERE asset = @Asset AND interval = @Interval
                      AND open_time::timestamptz < @VerifyAt
                      AND open_time::timestamptz >= @MinAllowed
                    ORDER BY open_time DESC
                    LIMIT 1
                ", new { 
                    Asset = cleanAsset, 
                    Interval = verifyInterval, 
                    VerifyAt = verifyUtc,
                    MinAllowed = minAllowedSub
                }, cancellationToken: ct));
            }

            // If subminute is missing (e.g., scraper stopped), fallback to historical_candles (1m).
            // Not in PO mode: historical_candles holds Tiingo prices, which must never settle an OTC trade.
            if (!PriceFeed.UsePocketOption && (!exitPrice.HasValue || exitPrice.Value <= 1e-8 || double.IsNaN(exitPrice.Value) || double.IsInfinity(exitPrice.Value)))
            {
                DateTime minAllowedHist = verifyUtc.AddSeconds(-120);
                exitPrice = await conn.QueryFirstOrDefaultAsync<double?>(new CommandDefinition(@"
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
                }, cancellationToken: ct));
            }

            // Calculate excursion (MFE / MAE) during trade lifetime [createdBucket, targetGrid] using timestamptz comparison
            if (exitPrice.HasValue && exitPrice.Value > 1e-8 && !double.IsNaN(exitPrice.Value) && !double.IsInfinity(exitPrice.Value))
            {
                try
                {
                    var exc = await conn.QueryFirstOrDefaultAsync<(double? max_high, double? min_low)>(new CommandDefinition(@"
                        SELECT MAX(high_price) as max_high, MIN(low_price) as min_low
                        FROM subminute_candles
                        WHERE asset = @Asset
                          AND interval = @Interval
                          AND open_time::timestamptz >= @CreatedBucket::timestamptz
                          AND open_time::timestamptz <= @TargetGrid::timestamptz
                    ", new { 
                        Asset = cleanAsset, 
                        Interval = verifyInterval, 
                        CreatedBucket = createdBucket.ToString("o"), 
                        TargetGrid = targetGrid.ToString("o") 
                    }, cancellationToken: ct));

                    double exitVal = exitPrice.Value;
                    double maxH = Math.Max(exc.max_high ?? exitVal, Math.Max(exitVal, record.EntryPrice));
                    double minL = Math.Min(exc.min_low ?? exitVal, Math.Min(exitVal, record.EntryPrice));

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
        }
        catch (Exception ex)
        {
             BotLogger.Warn($"[PendingVerifier] DB fetch failed: {ex.Message}");
        }

        if (!exitPrice.HasValue || exitPrice.Value <= 1e-8 || double.IsNaN(exitPrice.Value) || double.IsInfinity(exitPrice.Value))
        {
            if (secondsOverdue >= 120)
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
            }
            return;
        }

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
        
        if (isExactDoji || record.Direction is "HOLD" or "SHADOW_HOLD") 
        {
            record.WasCorrect = null; // TIE or neutral HOLD sample
            record.PnlBps = 0.0;
        } 
        else 
        {
            bool isBuy = !string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("BUY");
            bool isPut = !string.IsNullOrEmpty(record.Direction) && record.Direction.EndsWith("PUT");

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

        string resultStr = record.WasCorrect.HasValue ? (record.WasCorrect.Value ? "WIN" : "LOSS") : "TIE";
        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {resultStr} @ {exitPrice.Value} (Target: {record.VerifyAt:HH:mm:ss}, PnL: {record.PnlBps:+#0.00;-#0.00;0.00} bps)");
    }
}
