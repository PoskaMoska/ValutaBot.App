using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Data.Repositories
{
    public class TradeOutcomeRecord
    {
        public string Id { get; set; } = "";
        public string Asset { get; set; } = "";
        public string Timeframe { get; set; } = "";
        public string Direction { get; set; } = "";
        public double EntryPrice { get; set; }
        public double ExitPrice { get; set; }
        public double PnlBps { get; set; }
        public bool? WasWin { get; set; }
        public int Probability { get; set; }
        public double TaScore { get; set; }
        public double OfScore { get; set; }
        public double SmcScore { get; set; }
        public double MlProb { get; set; }
        public double MlScore { get; set; }
        public string FeaturesJson { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string VerifiedAt { get; set; } = "";

        // === Rich Features for LightGBM ===
        public string SmcBosDir { get; set; } = "NONE";   // BULLISH_BOS | BEARISH_BOS | NONE
        public bool SmcHasOb { get; set; }                // Order Block detected
        public bool SmcHasFvg { get; set; }               // Fair Value Gap detected
        public double OfDeltaRatio { get; set; }           // buy/sell volume ratio (e.g. 2.3 = 2.3x buyers)
        public string OfState { get; set; } = "NEUTRAL";  // STRONG_BULLISH_FLOW | BEARISH_ABSORPTION | etc.
        public int DynamicHorizon { get; set; }            // candles until expiry (1-4)

        // === Market Context for Analytics & ML ===
        public string MarketRegime    { get; set; } = "UNKNOWN"; // TREND | FLAT | CHAOS (from Python GMM)
        public string VelocityRegime  { get; set; } = "UNKNOWN"; // SLOW | MODERATE | FAST | EXPLOSIVE
        public double AtrAtSignal     { get; set; }              // ATR in price units at signal time
        public double AdxAtSignal     { get; set; }              // ADX 0-100 at signal time
        public double RsiAtSignal     { get; set; } = 50.0;      // RSI 0-100 at signal time
        public bool   HigherTfAligned { get; set; }              // higher TF candle direction matches signal
        public int    MinutesToNews   { get; set; } = -1;        // minutes to next high-impact news; -1 = none

        // === Time Context (computed at verification time from CreatedAt) ===
        public string Session    { get; set; } = "UNKNOWN"; // TOKYO | LONDON | NY | OVERLAP | QUIET
        public int    DayOfWeek  { get; set; }               // 1=Mon … 7=Sun
        public int    HourUtc    { get; set; }               // 0-23 UTC

        // === Phase 1: Complete Decision Snapshot ===
        public string TaDirection  { get; set; } = "NEUTRAL";
        public string MlDirection  { get; set; } = "NEUTRAL";
        public string SmcDirection { get; set; } = "NEUTRAL";
        public string OfDirection  { get; set; } = "NEUTRAL";
        public int    ConflictCount { get; set; }
        public string ConfidenceBucket { get; set; } = "50-60";
        public bool   WasCloseCall { get; set; }
        public int    ConsecutiveLossesBefore { get; set; }
        public int    SecondsSinceLastTrade { get; set; } = -1;
        public string ReasoningText { get; set; } = "";
        public string MlModelVersion { get; set; } = "";
        public double MlModelAccuracy { get; set; }
        public double MaxFavorableBps { get; set; }
        public double MaxAdverseBps { get; set; }
        
        // === Phase 3: Entropy and Context ===
        public double PriceEntropy { get; set; }
        public int    TrendMaturity { get; set; }
        public double PricePositionPct { get; set; }
        public bool   BbSqueeze { get; set; }

        // === Raw per-source telemetry (stored as jsonb) ===
        public string? TaTelemetry  { get; set; }
        public string? MlTelemetry  { get; set; }
        public string? SmcTelemetry { get; set; }
    }

    public class EvolutionDumpDto
    {
        public int TotalTrades { get; set; }
        public int NewTrades48h { get; set; }
        public double OldMlWinRate { get; set; }
        public double NewMlWinRate { get; set; }
        public int MlSavedTrades { get; set; }
    }

    public static class TradeRepository
    {
        private const string PoEraCutoff = "2026-10-15 21:55:00+00";
        private static readonly System.Collections.Concurrent.ConcurrentQueue<TradeOutcomeRecord> _emergencyOutcomesQueue = new();
        private const int MaxEmergencyOutcomesCapacity = 10_000;
        private static int _isDrainingOutcomes = 0;

        public static int EmergencyOutcomesBufferCount => _emergencyOutcomesQueue.Count;

        public static string NormalizeDirection(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return "NEUTRAL";
            dir = dir.Trim().ToUpperInvariant();
            return dir switch
            {
                "CALL" or "UP" => "BUY",
                "SELL" or "DOWN" => "PUT",
                "SHADOW_CALL" or "SHADOW_UP" => "SHADOW_BUY",
                "SHADOW_SELL" or "SHADOW_DOWN" => "SHADOW_PUT",
                _ => dir
            };
        }

        private static double Sanitize(double val, double fallback = 0.0)
        {
            if (!double.IsFinite(val)) return fallback;
            // Prevent IEEE 754 negative zero (-0.0) and machine epsilon below double precision float floor
            if (Math.Abs(val) < 1e-12) return 0.0;
            return val;
        }

        private static string? SanitizeTelemetry(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var trimmed = json.Trim();
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            {
                return trimmed;
            }
            return null;
        }

        private static string NormalizeIsoDate(string? dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return DateTime.UtcNow.ToString("o");
            if (DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt.ToUniversalTime().ToString("o");
            }
            return dateStr.Trim().Replace(' ', 'T');
        }

        private static void SanitizeOutcome(TradeOutcomeRecord o)
        {
            o.Direction = NormalizeDirection(o.Direction);
            o.EntryPrice = Sanitize(o.EntryPrice);
            o.ExitPrice = Sanitize(o.ExitPrice);

            // Financial PnL machine epsilon boundary guard:
            // Prevent machine epsilon drift (e.g. 1e-15) from converting a refund/tie (Doji) into a micro-loss.
            if (o.WasWin == null || Math.Abs(o.EntryPrice - o.ExitPrice) < 1e-7 || Math.Abs(o.PnlBps) < 1e-4)
            {
                o.PnlBps = 0.0;
                if (Math.Abs(o.EntryPrice - o.ExitPrice) < 1e-7 && (o.Direction == "BUY" || o.Direction == "PUT"))
                {
                    o.WasWin = null;
                }
            }
            else
            {
                o.PnlBps = Sanitize(Math.Round(o.PnlBps, 2));
            }

            o.TaScore = Sanitize(o.TaScore);
            o.OfScore = Sanitize(o.OfScore);
            o.SmcScore = Sanitize(o.SmcScore);
            o.MlProb = Sanitize(o.MlProb, 0.5);
            o.MlScore = Sanitize(o.MlScore);
            o.OfDeltaRatio = Sanitize(o.OfDeltaRatio, 1.0);
            o.AtrAtSignal = Sanitize(o.AtrAtSignal);
            o.AdxAtSignal = Sanitize(o.AdxAtSignal);
            o.RsiAtSignal = Sanitize(o.RsiAtSignal, 50.0);
            o.MlModelAccuracy = Sanitize(o.MlModelAccuracy);
            o.MaxFavorableBps = Sanitize(o.MaxFavorableBps);
            o.MaxAdverseBps = Sanitize(o.MaxAdverseBps);
            o.PriceEntropy = Sanitize(o.PriceEntropy);
            o.PricePositionPct = Sanitize(o.PricePositionPct, 0.5);

            o.CreatedAt = NormalizeIsoDate(o.CreatedAt);
            o.VerifiedAt = NormalizeIsoDate(o.VerifiedAt);

            o.TaTelemetry = SanitizeTelemetry(o.TaTelemetry);
            o.MlTelemetry = SanitizeTelemetry(o.MlTelemetry);
            o.SmcTelemetry = SanitizeTelemetry(o.SmcTelemetry);
        }

        private static SignalTracker.PredictionRecord SanitizePrediction(SignalTracker.PredictionRecord r)
        {
            r.Direction = NormalizeDirection(r.Direction);
            r.EntryPrice = Sanitize(r.EntryPrice);
            r.TaScore = Sanitize(r.TaScore);
            r.OfScore = Sanitize(r.OfScore);
            r.SmcScore = Sanitize(r.SmcScore);
            r.MlProb = Sanitize(r.MlProb, 0.5);
            r.MlScore = Sanitize(r.MlScore);
            r.OfDeltaRatio = Sanitize(r.OfDeltaRatio, 1.0);
            r.AtrAtSignal = Sanitize(r.AtrAtSignal);
            r.AdxAtSignal = Sanitize(r.AdxAtSignal);
            r.RsiAtSignal = Sanitize(r.RsiAtSignal, 50.0);
            r.MlModelAccuracy = Sanitize(r.MlModelAccuracy);
            r.PriceEntropy = Sanitize(r.PriceEntropy);
            r.PricePositionPct = Sanitize(r.PricePositionPct, 0.5);
            r.TaTelemetry = SanitizeTelemetry(r.TaTelemetry);
            r.MlTelemetry = SanitizeTelemetry(r.MlTelemetry);
            r.SmcTelemetry = SanitizeTelemetry(r.SmcTelemetry);
            return r;
        }

        private static Dictionary<string, string> SafeDeserializeDirections(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize(json, ValutaBotJsonContext.Default.DictionaryStringString) ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        public static async Task<EvolutionDumpDto?> GetEvolutionDumpAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return null;
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    var dto = await conn.QueryFirstOrDefaultAsync<EvolutionDumpDto>($@"
WITH outcome_data AS (
    SELECT 
        id,
        created_at::timestamptz as dt,
        CASE 
            WHEN direction = 'BUY' AND was_win = true THEN 'BUY'
            WHEN direction = 'BUY' AND was_win = false THEN 'PUT'
            WHEN direction = 'PUT' AND was_win = true THEN 'PUT'
            WHEN direction = 'PUT' AND was_win = false THEN 'BUY'
            ELSE 'NEUTRAL'
        END as win_dir,
        COALESCE(
            NULLIF(ml_direction, ''),
            CASE 
                WHEN ml_score > 0 THEN 'BUY'
                WHEN ml_score < 0 THEN 'PUT'
                ELSE 'NEUTRAL'
            END
        ) as ml_dir,
        CASE 
            WHEN ta_score > 0 THEN 'BUY'
            WHEN ta_score < 0 THEN 'PUT'
            ELSE 'NEUTRAL'
        END as ta_dir
    FROM trade_outcomes
    WHERE direction IN ('BUY', 'PUT')
      AND was_win IS NOT NULL
      {eraFilter}
)
SELECT 
    COUNT(id) as TotalTrades,
    COUNT(id) FILTER (WHERE dt >= NOW() - INTERVAL '48 hours') as NewTrades48h,
    
    COALESCE(
        COUNT(id) FILTER (WHERE dt < NOW() - INTERVAL '48 hours' AND ml_dir = win_dir AND ml_dir != 'NEUTRAL') * 100.0 / 
        NULLIF(COUNT(id) FILTER (WHERE dt < NOW() - INTERVAL '48 hours' AND ml_dir != 'NEUTRAL'), 0), 
        50.0
    ) as OldMlWinRate,
    
    COALESCE(
        COUNT(id) FILTER (WHERE dt >= NOW() - INTERVAL '48 hours' AND ml_dir = win_dir AND ml_dir != 'NEUTRAL') * 100.0 / 
        NULLIF(COUNT(id) FILTER (WHERE dt >= NOW() - INTERVAL '48 hours' AND ml_dir != 'NEUTRAL'), 0), 
        50.0
    ) as NewMlWinRate,

    COUNT(id) FILTER (WHERE dt >= NOW() - INTERVAL '48 hours' AND ml_dir = win_dir AND ta_dir != win_dir AND ta_dir != 'NEUTRAL' AND ml_dir != 'NEUTRAL') as MlSavedTrades

FROM outcome_data;", commandTimeout: 10);
                    return dto;
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetEvolutionDump notice: {ex.Message}");
                return null;
            }
        }

        public static async Task SaveTradeOutcomesBatchAsync(IEnumerable<TradeOutcomeRecord> outcomes)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            var list = outcomes.Select(o => { SanitizeOutcome(o); return o; }).ToList();
            if (list.Count == 0) return;
            var ids = list.Select(o => o.Id).ToArray();
            try
            {
                await DbConnectionFactory.WithRetryAsync(async (conn, tx) =>
                {
                    await conn.ExecuteAsync(@"
                        DELETE FROM pending_trades WHERE id = ANY(@ids);",
                        new { ids }, transaction: tx, commandTimeout: 10);

                    await conn.ExecuteAsync(@"
                        INSERT INTO trade_outcomes 
                        (id, asset, timeframe, direction, probability, entry_price, exit_price, pnl_bps, was_win, ta_score, of_score, smc_score, ml_prob, ml_score, features_json, created_at, verified_at,
                         smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon,
                         market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news,
                         session, day_of_week, hour_utc,
                         ta_direction, ml_direction, smc_direction, of_direction, conflict_count,
                         confidence_bucket, was_close_call, consecutive_losses_before, seconds_since_last_trade,
                         reasoning_text, ml_model_version, ml_model_accuracy, max_favorable_bps, max_adverse_bps,
                         price_entropy, trend_maturity, price_position_pct, bb_squeeze,
                         ta_telemetry, ml_telemetry, smc_telemetry)
                        VALUES (@Id, @Asset, @Timeframe, @Direction, @Probability, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson, @CreatedAt, @VerifiedAt,
                                @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon,
                                @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews,
                                @Session, @DayOfWeek, @HourUtc,
                                @TaDirection, @MlDirection, @SmcDirection, @OfDirection, @ConflictCount,
                                @ConfidenceBucket, @WasCloseCall, @ConsecutiveLossesBefore, @SecondsSinceLastTrade,
                                @ReasoningText, @MlModelVersion, @MlModelAccuracy, @MaxFavorableBps, @MaxAdverseBps,
                                @PriceEntropy, @TrendMaturity, @PricePositionPct, @BbSqueeze,
                                NULLIF(BTRIM(@TaTelemetry), '')::jsonb, NULLIF(BTRIM(@MlTelemetry), '')::jsonb, NULLIF(BTRIM(@SmcTelemetry), '')::jsonb)
                        ON CONFLICT (id) DO NOTHING", list, transaction: tx, commandTimeout: 10);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Error($"[PostgreSQL DB] Failed to save batch trade outcome records | Details: {ex.Message}");
                throw;
            }
        }

        public static async Task<bool> SaveTradeOutcomeAsync(TradeOutcomeRecord outcome, string? pendingTradeIdToDelete = null)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return false;
            SanitizeOutcome(outcome);
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async (conn, tx) =>
                {
                    string? pendingId = !string.IsNullOrEmpty(pendingTradeIdToDelete) ? pendingTradeIdToDelete : null;

                    // Atomic CTE: delete pending trade and insert outcome only if pending trade existed (or direct outcome with no pending ID).
                    // Prevents duplicate outcome records and concurrent duplicate processing across multiple workers.
                    const string sql = @"
                    WITH del AS (
                        DELETE FROM pending_trades 
                        WHERE id = @PendingTradeId AND @PendingTradeId IS NOT NULL AND @PendingTradeId != ''
                        RETURNING id
                    )
                    INSERT INTO trade_outcomes 
                    (id, asset, timeframe, direction, probability, entry_price, exit_price, pnl_bps, was_win, ta_score, of_score, smc_score, ml_prob, ml_score, features_json, created_at, verified_at,
                     smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon,
                     market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news,
                     session, day_of_week, hour_utc,
                     ta_direction, ml_direction, smc_direction, of_direction, conflict_count,
                     confidence_bucket, was_close_call, consecutive_losses_before, seconds_since_last_trade,
                     reasoning_text, ml_model_version, ml_model_accuracy, max_favorable_bps, max_adverse_bps,
                     price_entropy, trend_maturity, price_position_pct, bb_squeeze, ta_telemetry, ml_telemetry, smc_telemetry)
                    SELECT @Id, @Asset, @Timeframe, @Direction, @Probability, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson, @CreatedAt, @VerifiedAt,
                           @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon,
                           @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews,
                           @Session, @DayOfWeek, @HourUtc,
                           @TaDirection, @MlDirection, @SmcDirection, @OfDirection, @ConflictCount,
                           @ConfidenceBucket, @WasCloseCall, @ConsecutiveLossesBefore, @SecondsSinceLastTrade,
                           @ReasoningText, @MlModelVersion, @MlModelAccuracy, @MaxFavorableBps, @MaxAdverseBps,
                           @PriceEntropy, @TrendMaturity, @PricePositionPct, @BbSqueeze,
                           NULLIF(BTRIM(@TaTelemetry), '')::jsonb, NULLIF(BTRIM(@MlTelemetry), '')::jsonb, NULLIF(BTRIM(@SmcTelemetry), '')::jsonb
                    WHERE (@PendingTradeId IS NOT NULL AND @PendingTradeId != '' AND EXISTS (SELECT 1 FROM del))
                       OR (@PendingTradeId IS NULL OR @PendingTradeId = '')
                    ON CONFLICT (id) DO NOTHING
                    RETURNING id;";

                    var insertedId = await conn.ExecuteScalarAsync<string>(sql, new
                    {
                        PendingTradeId = pendingId,
                        outcome.Id,
                        outcome.Asset,
                        outcome.Timeframe,
                        outcome.Direction,
                        outcome.Probability,
                        outcome.EntryPrice,
                        outcome.ExitPrice,
                        outcome.PnlBps,
                        outcome.WasWin,
                        outcome.TaScore,
                        outcome.OfScore,
                        outcome.SmcScore,
                        outcome.MlProb,
                        outcome.MlScore,
                        outcome.FeaturesJson,
                        outcome.CreatedAt,
                        outcome.VerifiedAt,
                        outcome.SmcBosDir,
                        outcome.SmcHasOb,
                        outcome.SmcHasFvg,
                        outcome.OfDeltaRatio,
                        outcome.OfState,
                        outcome.DynamicHorizon,
                        outcome.MarketRegime,
                        outcome.VelocityRegime,
                        outcome.AtrAtSignal,
                        outcome.AdxAtSignal,
                        outcome.RsiAtSignal,
                        outcome.HigherTfAligned,
                        outcome.MinutesToNews,
                        outcome.Session,
                        outcome.DayOfWeek,
                        outcome.HourUtc,
                        outcome.TaDirection,
                        outcome.MlDirection,
                        outcome.SmcDirection,
                        outcome.OfDirection,
                        outcome.ConflictCount,
                        outcome.ConfidenceBucket,
                        outcome.WasCloseCall,
                        outcome.ConsecutiveLossesBefore,
                        outcome.SecondsSinceLastTrade,
                        outcome.ReasoningText,
                        outcome.MlModelVersion,
                        outcome.MlModelAccuracy,
                        outcome.MaxFavorableBps,
                        outcome.MaxAdverseBps,
                        outcome.PriceEntropy,
                        outcome.TrendMaturity,
                        outcome.PricePositionPct,
                        outcome.BbSqueeze,
                        outcome.TaTelemetry,
                        outcome.MlTelemetry,
                        outcome.SmcTelemetry
                    }, transaction: tx, commandTimeout: 10);
                    bool success = !string.IsNullOrEmpty(insertedId);
                    if (success && !_emergencyOutcomesQueue.IsEmpty)
                    {
                        _ = Task.Run(DrainEmergencyOutcomesAsync);
                    }
                    return success;
                });
            }
            catch (Exception ex)
            {
                BotLogger.Error($"[PostgreSQL DB] Failed to save trade outcome record {outcome.Id} ({ex.Message}). Buffering in emergency in-memory queue.", ex);
                while (_emergencyOutcomesQueue.Count >= MaxEmergencyOutcomesCapacity && _emergencyOutcomesQueue.TryDequeue(out var evicted))
                {
                    BotLogger.Warn($"[TradeRepository] Emergency buffer capacity reached ({MaxEmergencyOutcomesCapacity}). Evicting oldest outcome {evicted.Id} to prioritize recent trade {outcome.Id}.");
                }
                _emergencyOutcomesQueue.Enqueue(outcome);
                return false;
            }
        }

        public static async Task<int> DrainEmergencyOutcomesAsync()
        {
            if (_emergencyOutcomesQueue.IsEmpty || string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
                return 0;

            if (Interlocked.CompareExchange(ref _isDrainingOutcomes, 1, 0) != 0)
                return 0; // Concurrent drain already in progress

            int totalDrained = 0;
            try
            {
                while (!_emergencyOutcomesQueue.IsEmpty)
                {
                    var batch = new List<TradeOutcomeRecord>();
                    while (_emergencyOutcomesQueue.TryDequeue(out var item) && batch.Count < 200)
                    {
                        batch.Add(item);
                    }

                    if (batch.Count == 0) break;

                    try
                    {
                        await SaveTradeOutcomesBatchAsync(batch);
                        totalDrained += batch.Count;
                        BotLogger.Info($"[TradeRepository] Drained and saved {batch.Count} emergency buffered trade outcomes (total: {totalDrained}, remaining: {_emergencyOutcomesQueue.Count}).");
                    }
                    catch (Exception ex)
                    {
                        // Restore batch back into emergency queue so data is NOT lost
                        foreach (var item in batch)
                        {
                            while (_emergencyOutcomesQueue.Count >= MaxEmergencyOutcomesCapacity && _emergencyOutcomesQueue.TryDequeue(out _)) { }
                            _emergencyOutcomesQueue.Enqueue(item);
                        }
                        BotLogger.Warn($"[TradeRepository] Drain emergency outcomes postponed: {ex.Message}");
                        break;
                    }
                }
                return totalDrained;
            }
            finally
            {
                Interlocked.Exchange(ref _isDrainingOutcomes, 0);
            }
        }

        private static string TimestampToIso(object? value) => value switch
        {
            null => "",
            DateTime dt => dt.ToString("o"),
            DateTimeOffset dto => dto.ToString("o"),
            _ => value.ToString() ?? ""
        };

        public static async Task<List<TradeOutcomeRecord>> LoadTradeOutcomesAsync(int limit = 1000)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<TradeOutcomeRecord>();
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    var rows = await conn.QueryAsync($@"
                        SELECT id as ""Id"", asset as ""Asset"", timeframe as ""Timeframe"", direction as ""Direction"",
                               entry_price as ""EntryPrice"", exit_price as ""ExitPrice"", pnl_bps as ""PnlBps"",
                               was_win as ""WasWin"", ta_score as ""TaScore"", of_score as ""OfScore"", smc_score as ""SmcScore"",
                               ml_prob as ""MlProb"", ml_score as ""MlScore"", created_at as ""CreatedAt"", verified_at as ""VerifiedAt""
                        FROM trade_outcomes
                        WHERE direction IN ('BUY', 'PUT')
                          AND was_win IS NOT NULL
                          {eraFilter}
                        ORDER BY verified_at DESC
                        LIMIT @limit
                    ", new { limit }, commandTimeout: 8);

                    return rows.Select(r => new TradeOutcomeRecord
                    {
                        Id = r.Id,
                        Asset = r.Asset,
                        Timeframe = r.Timeframe,
                        Direction = r.Direction,
                        EntryPrice = r.EntryPrice != null ? Convert.ToDouble(r.EntryPrice) : 0.0,
                        ExitPrice = r.ExitPrice != null ? Convert.ToDouble(r.ExitPrice) : 0.0,
                        PnlBps = r.PnlBps != null ? Convert.ToDouble(r.PnlBps) : 0.0,
                        WasWin = r.WasWin != null ? Convert.ToBoolean(r.WasWin) : null,
                        TaScore = r.TaScore != null ? Convert.ToDouble(r.TaScore) : 0.0,
                        OfScore = r.OfScore != null ? Convert.ToDouble(r.OfScore) : 0.0,
                        SmcScore = r.SmcScore != null ? Convert.ToDouble(r.SmcScore) : 0.0,
                        MlProb = r.MlProb != null ? Convert.ToDouble(r.MlProb) : 0.0,
                        MlScore = r.MlScore != null ? Convert.ToDouble(r.MlScore) : 0.0,
                        CreatedAt = TimestampToIso((object?)r.CreatedAt),
                        VerifiedAt = TimestampToIso((object?)r.VerifiedAt)
                    }).ToList();
                });
            }
            catch (Exception ex)
            {
                BotLogger.Error("[PostgreSQL DB] Failed to load trade outcomes", ex);
                return new List<TradeOutcomeRecord>();
            }
        }

        public static async Task SavePendingTradeAsync(SignalTracker.PredictionRecord record)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    SanitizePrediction(record);
                    await conn.ExecuteAsync(@"
                    INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, probability, ta_score, of_score, smc_score, ml_prob, ml_score, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon, market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news, reasoning_text, ml_model_version, ml_model_accuracy, features_json, price_entropy, trend_maturity, price_position_pct, bb_squeeze, ta_telemetry, ml_telemetry, smc_telemetry)
                    VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews, @ReasoningText, @MlModelVersion, @MlModelAccuracy, @FeaturesJson, @PriceEntropy, @TrendMaturity, @PricePositionPct, @BbSqueeze, NULLIF(BTRIM(@TaTelemetry), '')::jsonb, NULLIF(BTRIM(@MlTelemetry), '')::jsonb, NULLIF(BTRIM(@SmcTelemetry), '')::jsonb)
                    ON CONFLICT (id) DO NOTHING", 
                    new {
                        record.Id,
                        record.Direction,
                        record.Asset,
                        record.Timeframe,
                        record.BrokerSymbol,
                        record.EntryPrice,
                        CreatedAtStr = record.CreatedAt.ToUniversalTime().ToString("o"),
                        VerifyAtStr = record.VerifyAt.ToUniversalTime().ToString("o"),
                        record.IsForex,
                        record.Probability,
                        SourceDirectionsStr = System.Text.Json.JsonSerializer.Serialize(record.SourceDirections, ValutaBotJsonContext.Default.DictionaryStringString),
                        record.TaScore,
                        record.OfScore,
                        record.SmcScore,
                        record.MlProb,
                        record.MlScore,
                        record.SmcBosDir,
                        record.SmcHasOb,
                        record.SmcHasFvg,
                        record.OfDeltaRatio,
                        record.OfState,
                        record.DynamicHorizon,
                        record.MarketRegime,
                        record.VelocityRegime,
                        record.AtrAtSignal,
                        record.AdxAtSignal,
                        record.RsiAtSignal,
                        record.HigherTfAligned,
                        record.MinutesToNews,
                        record.ReasoningText,
                        record.MlModelVersion,
                        record.MlModelAccuracy,
                        record.FeaturesJson,
                        record.PriceEntropy,
                        record.TrendMaturity,
                        record.PricePositionPct,
                        record.BbSqueeze,
                        record.TaTelemetry,
                        record.MlTelemetry,
                        record.SmcTelemetry
                    }, commandTimeout: 8);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Error($"[TradeRepository] Failed to save pending trade {record.Id}: {ex.Message}");
            }
        }

        public static async Task<List<SignalTracker.PredictionRecord>> GetPendingTradesToVerifyAsync(
            DateTime upTo, 
            int limit = 100, 
            IEnumerable<string>? excludeIds = null)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) 
                return new List<SignalTracker.PredictionRecord>();
            string upToIso = upTo.ToUniversalTime().ToString("o");
            var excludedArray = excludeIds?.ToArray() ?? Array.Empty<string>();
            return await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                var rows = await conn.QueryAsync(@"
                    WITH targets AS (
                        SELECT id
                        FROM pending_trades
                        WHERE verify_at <= @UpToIso
                          AND (claimed_at IS NULL OR claimed_at < NOW() - INTERVAL '60 seconds')
                          AND (cardinality(@ExcludedArray) = 0 OR id != ALL(@ExcludedArray))
                        ORDER BY verify_at ASC
                        LIMIT @Limit
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE pending_trades p
                    SET claimed_at = NOW()
                    FROM targets t
                    WHERE p.id = t.id
                    RETURNING p.id as ""Id"", p.direction as ""Direction"", p.asset as ""Asset"", p.timeframe as ""Timeframe"", 
                              p.binance_symbol as ""BinanceSymbol"", p.entry_price as ""EntryPrice"", 
                              p.created_at as ""CreatedAtStr"", p.verify_at as ""VerifyAtStr"", 
                              p.is_forex as ""IsForex"", p.source_directions as ""SourceDirectionsStr"",
                              p.probability as ""Probability"", p.features_json as ""FeaturesJson"", 
                              p.ta_score as ""TaScore"", p.of_score as ""OfScore"", p.smc_score as ""SmcScore"", 
                              p.ml_prob as ""MlProb"", p.ml_score as ""MlScore"",
                              p.smc_bos_dir as ""SmcBosDir"", p.smc_has_ob as ""SmcHasOb"", p.smc_has_fvg as ""SmcHasFvg"", 
                              p.of_delta_ratio as ""OfDeltaRatio"", p.of_state as ""OfState"", p.dynamic_horizon as ""DynamicHorizon"",
                              p.market_regime as ""MarketRegime"", velocity_regime as ""VelocityRegime"", 
                              atr_at_signal as ""AtrAtSignal"", adx_at_signal as ""AdxAtSignal"",
                              p.rsi_at_signal as ""RsiAtSignal"", higher_tf_aligned as ""HigherTfAligned"", 
                              p.minutes_to_news as ""MinutesToNews"",
                              p.reasoning_text as ""ReasoningText"", p.ml_model_version as ""MlModelVersion"", 
                              p.ml_model_accuracy as ""MlModelAccuracy"",
                              p.price_entropy as ""PriceEntropy"", p.trend_maturity as ""TrendMaturity"", 
                              p.price_position_pct as ""PricePositionPct"", p.bb_squeeze as ""BbSqueeze"",
                              p.ta_telemetry::text as ""TaTelemetry"", p.ml_telemetry::text as ""MlTelemetry"", 
                              p.smc_telemetry::text as ""SmcTelemetry"";", 
                    new { UpToIso = upToIso, Limit = limit, ExcludedArray = excludedArray }, commandTimeout: 8);

            return rows.Select(r => new SignalTracker.PredictionRecord
            {
                Id = r.Id,
                Direction = r.Direction ?? "",
                Asset = r.Asset ?? "",
                Timeframe = r.Timeframe ?? "",
                BrokerSymbol = r.BinanceSymbol ?? "",
                EntryPrice = r.EntryPrice != null ? Convert.ToDouble(r.EntryPrice) : 0.0,
                CreatedAt = string.IsNullOrEmpty(r.CreatedAtStr) ? DateTime.MinValue : DateTime.Parse(r.CreatedAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                VerifyAt = string.IsNullOrEmpty(r.VerifyAtStr) ? DateTime.MinValue : DateTime.Parse(r.VerifyAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                IsForex = r.IsForex != null ? Convert.ToBoolean(r.IsForex) : false,
                SourceDirections = SafeDeserializeDirections((string?)r.SourceDirectionsStr),

                Probability = r.Probability != null ? Convert.ToInt32(r.Probability) : 0,
                FeaturesJson = r.FeaturesJson ?? "",
                TaScore = r.TaScore != null ? Convert.ToDouble(r.TaScore) : 0.0,
                OfScore = r.OfScore != null ? Convert.ToDouble(r.OfScore) : 0.0,
                SmcScore = r.SmcScore != null ? Convert.ToDouble(r.SmcScore) : 0.0,
                MlProb = r.MlProb != null ? Convert.ToDouble(r.MlProb) : 0.0,
                MlScore = r.MlScore != null ? Convert.ToDouble(r.MlScore) : 0.0,
                SmcBosDir = r.SmcBosDir ?? "NONE",
                SmcHasOb = r.SmcHasOb != null ? Convert.ToBoolean(r.SmcHasOb) : false,
                SmcHasFvg = r.SmcHasFvg != null ? Convert.ToBoolean(r.SmcHasFvg) : false,
                OfDeltaRatio = r.OfDeltaRatio != null ? Convert.ToDouble(r.OfDeltaRatio) : 1.0,
                OfState = r.OfState ?? "NEUTRAL",
                DynamicHorizon = r.DynamicHorizon != null ? Convert.ToInt32(r.DynamicHorizon) : 3,
                MarketRegime = r.MarketRegime ?? "UNKNOWN",
                VelocityRegime = r.VelocityRegime ?? "UNKNOWN",
                AtrAtSignal = r.AtrAtSignal != null ? Convert.ToDouble(r.AtrAtSignal) : 0.0,
                AdxAtSignal = r.AdxAtSignal != null ? Convert.ToDouble(r.AdxAtSignal) : 0.0,
                RsiAtSignal = r.RsiAtSignal != null ? Convert.ToDouble(r.RsiAtSignal) : 50.0,
                HigherTfAligned = r.HigherTfAligned != null ? Convert.ToBoolean(r.HigherTfAligned) : false,
                MinutesToNews = r.MinutesToNews != null ? Convert.ToInt32(r.MinutesToNews) : -1,
                ReasoningText = r.ReasoningText ?? "",
                MlModelVersion = r.MlModelVersion ?? "",
                MlModelAccuracy = r.MlModelAccuracy != null ? Convert.ToDouble(r.MlModelAccuracy) : 0.0,
                PriceEntropy = r.PriceEntropy != null ? Convert.ToDouble(r.PriceEntropy) : 0.0,
                TrendMaturity = r.TrendMaturity != null ? Convert.ToInt32(r.TrendMaturity) : 0,
                PricePositionPct = r.PricePositionPct != null ? Convert.ToDouble(r.PricePositionPct) : 0.5,
                BbSqueeze = r.BbSqueeze != null ? Convert.ToBoolean(r.BbSqueeze) : false,
                TaTelemetry = (string?)r.TaTelemetry,
                MlTelemetry = (string?)r.MlTelemetry,
                SmcTelemetry = (string?)r.SmcTelemetry
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();
            });
        }

        public static async Task ReleasePendingTradeClaimAsync(string id)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    await conn.ExecuteAsync("UPDATE pending_trades SET claimed_at = NULL WHERE id = @id;", new { id }, commandTimeout: 8);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] Failed to release claim for {id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Defers a pending trade's claim eligibility across all workers by adjusting claimed_at in the database.
        /// Prevents other parallel workers from picking up the trade before broker candles arrive.
        /// </summary>
        public static async Task DeferPendingTradeClaimAsync(string id, int seconds = 3)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    await conn.ExecuteAsync(@"
                        UPDATE pending_trades 
                        SET claimed_at = NOW() - INTERVAL '60 seconds' + (INTERVAL '1 second' * @seconds)
                        WHERE id = @id;", new { id, seconds }, commandTimeout: 8);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] Failed to defer claim for {id}: {ex.Message}");
            }
        }

        public static async Task ResetAllOrphanedClaimsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    int resetCount = await conn.ExecuteAsync("UPDATE pending_trades SET claimed_at = NULL WHERE claimed_at IS NOT NULL;", commandTimeout: 8);
                    if (resetCount > 0)
                    {
                        BotLogger.Info($"[TradeRepository] Reset {resetCount} orphaned claimed_at locks on startup.");
                    }
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] Failed to reset orphaned claims on startup: {ex.Message}");
            }
        }

        public static async Task DeletePendingTradeAsync(string id)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    await conn.ExecuteAsync("DELETE FROM pending_trades WHERE id = @id", new { id }, commandTimeout: 8);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] DeletePendingTradeAsync notice: {ex.Message}");
            }
        }

        public static async Task<(int Total, int Verified, int Correct)> GetOverallStatsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return (0, 0, 0);
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    int pending = (int)(await conn.ExecuteScalarAsync<long?>(
                        "SELECT COUNT(*) FROM pending_trades WHERE direction IN ('BUY', 'PUT')", commandTimeout: 8) ?? 0L);
                    var res = await conn.QueryFirstOrDefaultAsync($@"
                        SELECT COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_win THEN 1 ELSE 0 END), 0) as Correct
                        FROM trade_outcomes
                        WHERE direction IN ('BUY', 'PUT')
                          AND was_win IS NOT NULL
                          {eraFilter}", commandTimeout: 8);
                    
                    int verified = res != null ? Convert.ToInt32(res.Verified) : 0;
                    int correct = res != null ? Convert.ToInt32(res.Correct) : 0;
                    return (pending + verified, verified, correct);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetOverallStatsAsync notice: {ex.Message}");
                return (0, 0, 0);
            }
        }

        public static async Task<(int Total, int Verified, int Correct)> GetStatsAsync(string asset, string timeframe)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return (0, 0, 0);
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    int pending = (int)(await conn.ExecuteScalarAsync<long?>(
                        "SELECT COUNT(*) FROM pending_trades WHERE asset = @asset AND timeframe = @timeframe AND direction IN ('BUY', 'PUT')",
                        new { asset, timeframe }, commandTimeout: 8) ?? 0L);
                    var res = await conn.QueryFirstOrDefaultAsync($@"
                        SELECT COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_win THEN 1 ELSE 0 END), 0) as Correct
                        FROM trade_outcomes
                        WHERE asset = @asset AND timeframe = @timeframe
                          AND direction IN ('BUY', 'PUT')
                          AND was_win IS NOT NULL
                          {eraFilter}",
                        new { asset, timeframe }, commandTimeout: 8);
                    
                    int verified = res != null ? Convert.ToInt32(res.Verified) : 0;
                    int correct = res != null ? Convert.ToInt32(res.Correct) : 0;
                    return (pending + verified, verified, correct);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetStatsAsync notice: {ex.Message}");
                return (0, 0, 0);
            }
        }

        public static async Task<List<(string asset, string timeframe, int total, int verified, int correct)>> GetAllStatsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<(string, string, int, int, int)>();
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    
                    var pendingRows = await conn.QueryAsync(
                        "SELECT asset, timeframe, COUNT(*) as Pending FROM pending_trades WHERE direction IN ('BUY', 'PUT') GROUP BY asset, timeframe", commandTimeout: 8);
                    
                    var rows = await conn.QueryAsync($@"
                        SELECT asset, timeframe, COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_win THEN 1 ELSE 0 END), 0) as Correct
                        FROM trade_outcomes
                        WHERE direction IN ('BUY', 'PUT')
                          AND was_win IS NOT NULL
                          {eraFilter}
                        GROUP BY asset, timeframe", commandTimeout: 8);
                    
                    var result = new List<(string, string, int, int, int)>();
                    foreach (var r in rows)
                    {
                        int verified = Convert.ToInt32(r.verified);
                        int pending = 0;
                        foreach (var p in pendingRows)
                        {
                            if (p.asset == r.asset && p.timeframe == r.timeframe)
                            {
                                pending = Convert.ToInt32(p.pending);
                                break;
                            }
                        }
                        result.Add((r.asset, r.timeframe, verified + pending, verified, Convert.ToInt32(r.correct)));
                    }
                    return result;
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetAllStatsAsync notice: {ex.Message}");
                return new List<(string, string, int, int, int)>();
            }
        }

        public static async Task<System.Collections.Generic.List<bool?>> GetRecentOutcomesAsync(int limit = 10, DateTime? sinceUtc = null)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new System.Collections.Generic.List<bool?>();
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    string sinceFilter = sinceUtc.HasValue
                        ? "AND verified_at > @SinceUtcStr"
                        : "";
                    string sinceUtcStr = sinceUtc?.ToUniversalTime().ToString("o") ?? "";
                    var rows = await conn.QueryAsync<bool?>($@"
                        SELECT was_win 
                        FROM trade_outcomes 
                        WHERE verified_at IS NOT NULL 
                          AND direction IN ('BUY', 'PUT')
                          {eraFilter}
                          {sinceFilter}
                        ORDER BY verified_at DESC 
                        LIMIT @Limit
                    ", new { Limit = limit, SinceUtcStr = sinceUtcStr }, commandTimeout: 8);
                    return rows.ToList();
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetRecentOutcomesAsync notice: {ex.Message}");
                return new System.Collections.Generic.List<bool?>();
            }
        }

        public static async Task<System.Collections.Generic.List<bool>> GetRecentOutcomesForAssetAsync(string asset, string timeframe, int limit = 20, DateTime? sinceUtc = null)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new System.Collections.Generic.List<bool>();
            try
            {
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    string eraFilter = ValutaBot.MiniApp.PriceFeed.UsePocketOption
                        ? $"AND created_at::timestamptz >= '{PoEraCutoff}'::timestamptz"
                        : "";
                    string sinceFilter = sinceUtc.HasValue
                        ? "AND verified_at > @SinceUtcStr"
                        : "";
                    string sinceUtcStr = sinceUtc?.ToUniversalTime().ToString("o") ?? "";
                    var rows = await conn.QueryAsync<bool>($@"
                        SELECT was_win 
                        FROM trade_outcomes 
                        WHERE asset = @Asset AND timeframe = @Timeframe AND was_win IS NOT NULL 
                          AND direction IN ('BUY', 'PUT')
                          {eraFilter}
                          {sinceFilter}
                        ORDER BY verified_at DESC 
                        LIMIT @Limit
                    ", new { Asset = asset, Timeframe = timeframe, Limit = limit, SinceUtcStr = sinceUtcStr }, commandTimeout: 8);
                    return rows.ToList();
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetRecentOutcomesForAssetAsync notice: {ex.Message}");
                return new System.Collections.Generic.List<bool>();
            }
        }

        /// <summary>
        /// Returns the total number of verified (fully labeled) real BUY/PUT rows in trade_outcomes.
        /// Used by CircuitBreakerService to autonomously decide whether the system
        /// is still in dataset-collection mode (CB bypassed) or live-trading mode (CB active).
        /// </summary>
        public static async Task<int> GetVerifiedOutcomesCountAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return 0;
                return await DbConnectionFactory.WithRetryAsync(async conn =>
                {
                    if (ValutaBot.MiniApp.PriceFeed.UsePocketOption)
                    {
                        return await conn.ExecuteScalarAsync<int>(
                            $"SELECT COUNT(*) FROM trade_outcomes WHERE created_at::timestamptz >= '{PoEraCutoff}'::timestamptz AND direction IN ('BUY', 'PUT');", commandTimeout: 8);
                    }
                    return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trade_outcomes WHERE direction IN ('BUY', 'PUT');", commandTimeout: 8);
                });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] GetVerifiedOutcomesCountAsync failed: {ex.Message}");
                return 0;
            }
        }
    }
}
