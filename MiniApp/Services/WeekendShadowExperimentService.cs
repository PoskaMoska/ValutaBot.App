using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

namespace ValutaBot.MiniApp.Services;

/// <summary>
/// Passive weekend synthetic OTC archive and multi-hypothesis shadow experiment tracker (Fri 20:44Z - Sun 21:00Z).
/// Collects long-retention s5 candles in weekend_otc_candles and compares competing directional hypotheses
/// in weekend_otc_experiments without altering weekday models, live execution gates, or UI win rates.
/// </summary>
public static class WeekendShadowExperimentService
{
    private static readonly ConcurrentDictionary<string, (DateTime LastBarOpen, long RecordedAtSw)> _throttle =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _verifyLock = new(1, 1);

    private sealed record PendingExperimentRow(
        int Id,
        string Asset,
        string Timeframe,
        string OpenTime,
        string VerifyAt,
        double EntryPrice,
        string WeekdayModelDir,
        string MeanRevDir,
        string CrowdContraDir,
        string BasketDesyncDir
    );

    public readonly record struct HypothesisSnapshot(
        string WeekdayModelDir,
        double WeekdayModelProb,
        string MeanRevDir,
        int MeanRevStreak,
        string CrowdContraDir,
        double CrowdSentiment,
        string BasketDesyncDir,
        double DxyMomentumBps
    );

    /// <summary>
    /// Matches Python is_weekend_otc_unix: Friday 20:44 UTC through Sunday 21:00 UTC.
    /// </summary>
    public static bool IsWeekendOtcWindowUtc(DateTime timestamp)
    {
        DateTime utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        return utc.DayOfWeek switch
        {
            DayOfWeek.Friday => utc.TimeOfDay >= new TimeSpan(20, 44, 0),
            DayOfWeek.Saturday => true,
            DayOfWeek.Sunday => utc.TimeOfDay < new TimeSpan(21, 0, 0),
            _ => false
        };
    }

    internal static async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;

        try
        {
            await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                await conn.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS weekend_otc_candles (
                        id SERIAL PRIMARY KEY,
                        asset TEXT NOT NULL,
                        interval TEXT NOT NULL,
                        open_time TEXT NOT NULL,
                        open_price DOUBLE PRECISION NOT NULL,
                        high_price DOUBLE PRECISION NOT NULL,
                        low_price DOUBLE PRECISION NOT NULL,
                        close_price DOUBLE PRECISION NOT NULL,
                        volume DOUBLE PRECISION NOT NULL DEFAULT 0,
                        crowd_sentiment DOUBLE PRECISION NOT NULL DEFAULT 50.0,
                        UNIQUE(asset, interval, open_time)
                    );
                    DROP INDEX IF EXISTS idx_weekend_otc_candles_lookup;
                    CREATE INDEX IF NOT EXISTS idx_weekend_otc_candles_opentime
                        ON weekend_otc_candles(open_time DESC) WITH (fillfactor = 90);

                    CREATE TABLE IF NOT EXISTS weekend_otc_experiments (
                        id SERIAL PRIMARY KEY,
                        asset TEXT NOT NULL,
                        timeframe TEXT NOT NULL,
                        open_time TEXT NOT NULL,
                        verify_at TEXT NOT NULL,
                        entry_price DOUBLE PRECISION NOT NULL,
                        exit_price DOUBLE PRECISION NULL,
                        actual_dir TEXT NULL,
                        weekday_model_dir TEXT NOT NULL,
                        weekday_model_prob DOUBLE PRECISION NOT NULL DEFAULT 0.5,
                        weekday_win BOOLEAN NULL,
                        mean_rev_dir TEXT NOT NULL,
                        mean_rev_streak INTEGER NOT NULL DEFAULT 0,
                        mean_rev_win BOOLEAN NULL,
                        crowd_contra_dir TEXT NOT NULL,
                        crowd_sentiment DOUBLE PRECISION NOT NULL DEFAULT 50.0,
                        crowd_contra_win BOOLEAN NULL,
                        basket_desync_dir TEXT NOT NULL,
                        dxy_momentum_bps DOUBLE PRECISION NOT NULL DEFAULT 0.0,
                        basket_desync_win BOOLEAN NULL,
                        ml_model_version TEXT NOT NULL DEFAULT '',
                        created_at TEXT NOT NULL,
                        verified_at TEXT NULL,
                        UNIQUE(asset, timeframe, open_time)
                    );
                    CREATE INDEX IF NOT EXISTS idx_weekend_otc_exp_pending
                        ON weekend_otc_experiments(verify_at ASC) WHERE actual_dir IS NULL;
                    CREATE INDEX IF NOT EXISTS idx_weekend_otc_exp_verified
                        ON weekend_otc_experiments(timeframe, verified_at DESC) WHERE actual_dir IS NOT NULL;

                    DO $$
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM weekend_otc_candles LIMIT 1) THEN
                            INSERT INTO weekend_otc_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume, crowd_sentiment)
                            SELECT asset, interval, open_time, open_price, high_price, low_price, close_price, volume, COALESCE(crowd_sentiment, 50.0)
                            FROM subminute_candles
                            WHERE interval = 's5'
                              AND asset LIKE '%_PO'
                              AND open_time >= to_char((NOW() AT TIME ZONE 'UTC') - INTERVAL '8 days', 'YYYY-MM-DD') || 'T00:00:00Z'
                              AND (
                                  (EXTRACT(DOW FROM (open_time::timestamptz AT TIME ZONE 'UTC')) = 5 AND (open_time::timestamptz AT TIME ZONE 'UTC')::time >= '20:44:00'::time)
                               OR EXTRACT(DOW FROM (open_time::timestamptz AT TIME ZONE 'UTC')) = 6
                               OR (EXTRACT(DOW FROM (open_time::timestamptz AT TIME ZONE 'UTC')) = 0 AND (open_time::timestamptz AT TIME ZONE 'UTC')::time < '21:00:00'::time)
                              )
                            ON CONFLICT (asset, interval, open_time) DO NOTHING;
                        END IF;
                    END $$;
                ", commandTimeout: 120);
            });
            BotLogger.Info("[WeekendArchive] weekend_otc_candles & weekend_otc_experiments initialized.");
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[WeekendArchive] Initialization notice: {ex.Message}");
        }
    }

    internal static string EvaluateWeekdayModelDirection(double uncappedProbUp)
    {
        if (!double.IsFinite(uncappedProbUp)) return "NEUTRAL";
        if (uncappedProbUp >= 0.505) return "BUY";
        if (uncappedProbUp <= 0.495) return "PUT";
        return "NEUTRAL";
    }

    internal static (string Direction, int Streak) EvaluateMeanReversion(MiniAppController.OhlcCandle[] closedCandles)
    {
        if (closedCandles == null || closedCandles.Length < 2) return ("NEUTRAL", 0);

        int sign = 0;
        int streak = 0;
        for (int i = closedCandles.Length - 1; i >= 0; i--)
        {
            var c = closedCandles[i];
            double diff = c.Close - c.Open;
            if (Math.Abs(diff) <= 1e-12) break;

            int barSign = diff > 0 ? 1 : -1;
            if (sign == 0)
            {
                sign = barSign;
                streak = 1;
            }
            else if (barSign == sign)
            {
                streak++;
            }
            else
            {
                break;
            }
        }

        if (streak < 2 || sign == 0) return ("NEUTRAL", streak);
        return (sign > 0 ? "PUT" : "BUY", streak);
    }

    internal static string EvaluateCrowdContrarian(double crowdSentiment)
    {
        if (!double.IsFinite(crowdSentiment)) return "NEUTRAL";
        if (crowdSentiment >= 55.0) return "PUT";
        if (crowdSentiment <= 45.0) return "BUY";
        return "NEUTRAL";
    }

    internal static string EvaluateBasketDesync(
        string asset,
        MiniAppController.OhlcCandle[] closedCandles,
        int intervalSecs,
        DollarBasketMetrics dxy)
    {
        if (closedCandles == null || closedCandles.Length < 2) return "NEUTRAL";
        if (!double.IsFinite(dxy.BasketSyncScore) || !double.IsFinite(dxy.DxyMomentum1mBps)) return "NEUTRAL";
        if (dxy.BasketSyncScore < 0.67 || Math.Abs(dxy.DxyMomentum1mBps) < 0.8) return "NEUTRAL";

        int bars1m = Math.Max(1, (int)Math.Round(60.0 / Math.Max(1, intervalSecs)));
        int refIdx = Math.Max(0, closedCandles.Length - 1 - bars1m);
        double refClose = closedCandles[refIdx].Close;
        double lastClose = closedCandles[^1].Close;
        if (refClose <= 1e-8 || lastClose <= 1e-8) return "NEUTRAL";

        double pairMoveBps = (lastClose - refClose) / refClose * 10000.0;
        bool isUsdBase = AssetSanitizer.Sanitize(asset).StartsWith("USD", StringComparison.OrdinalIgnoreCase);
        double usdPairMoveBps = isUsdBase ? pairMoveBps : -pairMoveBps;

        int basketSign = Math.Sign(dxy.DxyMomentum1mBps);
        bool opposedOrLagged = Math.Sign(usdPairMoveBps) != basketSign
                               || Math.Abs(usdPairMoveBps) < Math.Abs(dxy.DxyMomentum1mBps) * 0.35;
        if (!opposedOrLagged) return "NEUTRAL";

        bool buyAsset = isUsdBase ? basketSign > 0 : basketSign < 0;
        return buyAsset ? "BUY" : "PUT";
    }

    internal static HypothesisSnapshot EvaluateHypotheses(
        string cleanAsset,
        int intervalSecs,
        MiniAppController.OhlcCandle[] closedCandles,
        MLPythonService.MLPythonPrediction? prediction,
        double crowdSentiment,
        DollarBasketMetrics dxy)
    {
        double uncappedUp = prediction?.RawUncappedProb
            ?? prediction?.RawConfidence
            ?? 0.5;
        uncappedUp = double.IsFinite(uncappedUp) ? Math.Clamp(uncappedUp, 0.0, 1.0) : 0.5;

        string weekdayDir = EvaluateWeekdayModelDirection(uncappedUp);
        var (meanRevDir, streak) = EvaluateMeanReversion(closedCandles);
        string crowdDir = EvaluateCrowdContrarian(crowdSentiment);
        string basketDir = EvaluateBasketDesync(cleanAsset, closedCandles, intervalSecs, dxy);

        return new HypothesisSnapshot(
            WeekdayModelDir: weekdayDir,
            WeekdayModelProb: Math.Round(uncappedUp, 4),
            MeanRevDir: meanRevDir,
            MeanRevStreak: streak,
            CrowdContraDir: crowdDir,
            CrowdSentiment: double.IsFinite(crowdSentiment) ? Math.Round(crowdSentiment, 2) : 50.0,
            BasketDesyncDir: basketDir,
            DxyMomentumBps: double.IsFinite(dxy.DxyMomentum1mBps) ? Math.Round(dxy.DxyMomentum1mBps, 2) : 0.0
        );
    }

    internal static (string ActualDir, bool? IsWin) ResolveOutcome(double entryPrice, double exitPrice, string predictedDir)
    {
        if (entryPrice <= 1e-8 || exitPrice <= 1e-8 || !double.IsFinite(entryPrice) || !double.IsFinite(exitPrice))
        {
            return ("TIE", null);
        }

        double rawDiff = (exitPrice - entryPrice) / entryPrice;
        string actualDir = Math.Abs(rawDiff) < 1e-8 ? "TIE" : (rawDiff > 0 ? "BUY" : "PUT");
        bool? win = (predictedDir is "BUY" or "PUT" && actualDir is "BUY" or "PUT")
            ? predictedDir == actualDir
            : null;
        return (actualDir, win);
    }

    public static async Task TryRecordWeekendExperimentAsync(
        string cleanAsset,
        string timeframe,
        int intervalSecs,
        int targetHorizon,
        DateTime barOpenUtc,
        double entryPrice,
        MiniAppController.OhlcCandle[] closedCandles,
        MLPythonService.MLPythonPrediction? prediction,
        double crowdSentiment,
        DollarBasketMetrics dxy)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        if (entryPrice <= 1e-8 || closedCandles == null || closedCandles.Length < 4 || intervalSecs <= 0) return;
        if (!PriceFeed.CoreUsdBasketPairs.Contains(cleanAsset, StringComparer.OrdinalIgnoreCase)) return;

        DateTime utcOpen = barOpenUtc.Kind == DateTimeKind.Utc ? barOpenUtc : barOpenUtc.ToUniversalTime();
        if (!IsWeekendOtcWindowUtc(utcOpen)) return;

        string normTf = TimeframeNormalizer.Normalize(timeframe).ToLowerInvariant();
        string throttleKey = $"{cleanAsset}:{normTf}";
        long nowSw = System.Diagnostics.Stopwatch.GetTimestamp();
        double minSpacingSec = intervalSecs == 5 ? 15.0 : Math.Max(1.0, intervalSecs - 1.0);

        if (_throttle.TryGetValue(throttleKey, out var prev))
        {
            if (prev.LastBarOpen >= utcOpen) return;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(prev.RecordedAtSw).TotalSeconds < minSpacingSec) return;
        }
        _throttle[throttleKey] = (utcOpen, nowSw);

        var snapshot = EvaluateHypotheses(cleanAsset, intervalSecs, closedCandles, prediction, crowdSentiment, dxy);
        int horizonBars = Math.Max(1, targetHorizon);
        DateTime verifyAtUtc = utcOpen.AddSeconds(horizonBars * intervalSecs);

        try
        {
            await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO weekend_otc_experiments (
                        asset, timeframe, open_time, verify_at, entry_price,
                        weekday_model_dir, weekday_model_prob,
                        mean_rev_dir, mean_rev_streak,
                        crowd_contra_dir, crowd_sentiment,
                        basket_desync_dir, dxy_momentum_bps,
                        ml_model_version, created_at
                    )
                    VALUES (
                        @Asset, @Timeframe, @OpenTime, @VerifyAt, @EntryPrice,
                        @WeekdayModelDir, @WeekdayModelProb,
                        @MeanRevDir, @MeanRevStreak,
                        @CrowdContraDir, @CrowdSentiment,
                        @BasketDesyncDir, @DxyMomentumBps,
                        @MlModelVersion, @CreatedAt
                    )
                    ON CONFLICT (asset, timeframe, open_time) DO NOTHING;",
                    new
                    {
                        Asset = cleanAsset,
                        Timeframe = normTf,
                        OpenTime = utcOpen.ToString("o"),
                        VerifyAt = verifyAtUtc.ToString("o"),
                        EntryPrice = entryPrice,
                        snapshot.WeekdayModelDir,
                        snapshot.WeekdayModelProb,
                        snapshot.MeanRevDir,
                        snapshot.MeanRevStreak,
                        snapshot.CrowdContraDir,
                        snapshot.CrowdSentiment,
                        snapshot.BasketDesyncDir,
                        snapshot.DxyMomentumBps,
                        MlModelVersion = prediction?.ModelVersion ?? "",
                        CreatedAt = DateTime.UtcNow.ToString("o")
                    }, commandTimeout: 6);
            });
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[WeekendExp] Failed to save snapshot for {cleanAsset}/{normTf}: {ex.Message}");
        }
    }

    public static async Task VerifyPendingWeekendExperimentsAsync(DateTime brokerNowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        if (!await _verifyLock.WaitAsync(0, ct)) return;

        try
        {
            DateTime cutoff = (brokerNowUtc.Kind == DateTimeKind.Utc ? brokerNowUtc : brokerNowUtc.ToUniversalTime()).AddSeconds(-4);

            var pending = await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                var rows = await conn.QueryAsync<PendingExperimentRow>(new CommandDefinition(@"
                    SELECT id as ""Id"",
                           asset as ""Asset"",
                           timeframe as ""Timeframe"",
                           open_time as ""OpenTime"",
                           verify_at as ""VerifyAt"",
                           entry_price as ""EntryPrice"",
                           weekday_model_dir as ""WeekdayModelDir"",
                           mean_rev_dir as ""MeanRevDir"",
                           crowd_contra_dir as ""CrowdContraDir"",
                           basket_desync_dir as ""BasketDesyncDir""
                    FROM weekend_otc_experiments
                    WHERE actual_dir IS NULL
                      AND verify_at::timestamptz <= @Cutoff
                    ORDER BY verify_at ASC
                    LIMIT 50;",
                    new { Cutoff = cutoff }, cancellationToken: ct, commandTimeout: 8));
                return rows.ToList();
            }, ct);

            if (pending.Count == 0) return;

            long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
            foreach (var row in pending)
            {
                if (ct.IsCancellationRequested) break;
                if (!DateTime.TryParse(row.VerifyAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var verifyUtc))
                {
                    continue;
                }

                DateTime targetGrid = new DateTime(verifyUtc.Ticks - (verifyUtc.Ticks % s5Ticks), DateTimeKind.Utc);
                string candleAsset = PriceFeed.CandleKey(row.Asset);
                double? exitPrice = null;

                try
                {
                    exitPrice = await DbConnectionFactory.WithRetryAsync(
                        conn => PendingTradeVerificationService.FindStrictS5ClosePriceAsync(
                            conn, candleAsset, targetGrid, includeWeekendArchiveFallback: true, ct),
                        ct);
                }
                catch { }

                if (exitPrice == null || exitPrice.Value <= 1e-8)
                {
                    var recent = await RealtimeTickCollector.GetRecentCandles(candleAsset, "s5", 10);
                    var match = recent?.FirstOrDefault(c => Math.Abs((c.Timestamp.AddSeconds(5) - targetGrid).TotalSeconds) <= 2.5);
                    if (match != null && match.Close > 1e-8)
                    {
                        exitPrice = match.Close;
                    }
                }

                if (exitPrice == null || exitPrice.Value <= 1e-8)
                {
                    double overdueSec = (DateTime.UtcNow - verifyUtc).TotalSeconds;
                    if (overdueSec >= 180)
                    {
                        await DbConnectionFactory.WithRetryAsync(async conn =>
                        {
                            await conn.ExecuteAsync(@"
                                UPDATE weekend_otc_experiments
                                SET actual_dir = 'GAP', verified_at = @VerifiedAt
                                WHERE id = @Id;",
                                new { Id = row.Id, VerifiedAt = DateTime.UtcNow.ToString("o") }, commandTimeout: 6);
                        }, ct);
                    }
                    continue;
                }

                var (actualDir, weekdayWin) = ResolveOutcome(row.EntryPrice, exitPrice.Value, row.WeekdayModelDir);
                var (_, meanRevWin) = ResolveOutcome(row.EntryPrice, exitPrice.Value, row.MeanRevDir);
                var (_, crowdWin) = ResolveOutcome(row.EntryPrice, exitPrice.Value, row.CrowdContraDir);
                var (_, basketWin) = ResolveOutcome(row.EntryPrice, exitPrice.Value, row.BasketDesyncDir);

                await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    await conn.ExecuteAsync(@"
                        UPDATE weekend_otc_experiments
                        SET exit_price = @ExitPrice,
                            actual_dir = @ActualDir,
                            weekday_win = @WeekdayWin,
                            mean_rev_win = @MeanRevWin,
                            crowd_contra_win = @CrowdWin,
                            basket_desync_win = @BasketWin,
                            verified_at = @VerifiedAt
                        WHERE id = @Id;",
                        new
                        {
                            Id = row.Id,
                            ExitPrice = exitPrice.Value,
                            ActualDir = actualDir,
                            WeekdayWin = weekdayWin,
                            MeanRevWin = meanRevWin,
                            CrowdWin = crowdWin,
                            BasketWin = basketWin,
                            VerifiedAt = DateTime.UtcNow.ToString("o")
                        }, commandTimeout: 6);
                }, ct);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[WeekendExpVerifier] Verification notice: {ex.Message}");
        }
        finally
        {
            _verifyLock.Release();
        }
    }
}
