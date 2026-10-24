using ValutaBot.App.MiniApp.Data.Repositories;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public static class TradeOutcomeTracker
{
    public static ValutaBot.MiniApp.Services.ICircuitBreakerService? CircuitBreaker { get; set; }
    public static IAutoCalibrationEngine? AutoCalib { get; set; }
    private static volatile bool _initialized = false;
    private static readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private static readonly SemaphoreSlim _csvSemaphore = new(1, 1); // B5-FIX: Concurrent CSV write lock
private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _consecutiveLosses = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastTradeTime = new();

    private static int ComputeSecondsSinceLastTrade(string asset, string timeframe, DateTime createdAt)
    {
        string key = $"{asset}_{timeframe}";
        if (_lastTradeTime.TryGetValue(key, out DateTime lastTime))
        {
            int seconds = (int)(createdAt - lastTime).TotalSeconds;
            _lastTradeTime[key] = createdAt;
            return Math.Max(0, seconds);
        }
        _lastTradeTime[key] = createdAt;
        return -1; // First trade for this pair/tf
    }

    /// <summary>
    /// Maps a UTC signal time to a forex trading session label.
    /// OVERLAP (London+NY) is the highest-volume window and most predictable for subminute TFs.
    /// </summary>
    public static string ComputeSession(DateTime utc)
    {
        int h = utc.Hour;
        // OVERLAP = London/NY crossover 13:00–16:00 UTC (highest volume, tightest spreads)
        if (h >= 13 && h < 16) return "OVERLAP";
        // LONDON = 08:00–17:00 UTC
        if (h >= 8 && h < 17) return "LONDON";
        // NY = 13:00–21:00 UTC (already covers NY-only from 16–21)
        if (h >= 16 && h < 21) return "NY";
        // TOKYO = 00:00–09:00 UTC
        if (h >= 0 && h < 9) return "TOKYO";
        return "QUIET";
    }

    public static int GetConsecutiveLosses(string asset, string timeframe)
    {
        string key = $"{asset}_{timeframe}";
        return _consecutiveLosses.TryGetValue(key, out int count) ? count : 0;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _tfVerifiedCounters = new();

    public static async Task InitializeAsync()
    {
        if (_initialized) return;
        
        await _initSemaphore.WaitAsync();
        try
        {
            if (_initialized) return;

            // Pure Neural Brain active: indicator calibration table decoupled

            var outcomes = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.LoadTradeOutcomesAsync(1000);
            BotLogger.Info($"[TradeOutcomeTracker] Loaded {outcomes.Count} historical outcomes from PostgreSQL DB (for reporting only).");

            _initialized = true;
            BotLogger.Info("[TradeOutcomeTracker] Trade outcome tracker initialized successfully.");
        }
        catch (Exception ex)
        {
            BotLogger.Error("[TradeOutcomeTracker] Failed to initialize trade outcome tracker", ex);
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    public static async Task OnTradeVerifiedAsync(SignalTracker.PredictionRecord record)
    {
        await InitializeAsync();

        try
        {
            var outcomeRecord = new TradeOutcomeRecord
            {
                Id = record.Id,
                Asset = record.Asset,
                Timeframe = record.Timeframe,
                Direction = record.Direction,
                Probability = record.Probability,
                EntryPrice = record.EntryPrice,
                ExitPrice = record.ExitPrice ?? record.EntryPrice,
                PnlBps = record.PnlBps,
                WasWin = record.WasCorrect,
                TaScore = record.TaScore,
                OfScore = record.OfScore,
                SmcScore = record.SmcScore,
                MlProb = record.MlProb,
                MlScore = record.MlScore,
                FeaturesJson = record.FeaturesJson,
                SmcBosDir = record.SmcBosDir,
                SmcHasOb = record.SmcHasOb,
                SmcHasFvg = record.SmcHasFvg,
                OfDeltaRatio = record.OfDeltaRatio,
                OfState = record.OfState,
                DynamicHorizon = record.DynamicHorizon,
                MarketRegime = record.MarketRegime,
                VelocityRegime = record.VelocityRegime,
                AtrAtSignal = record.AtrAtSignal,
                AdxAtSignal = record.AdxAtSignal,
                RsiAtSignal = record.RsiAtSignal,
                HigherTfAligned = record.HigherTfAligned,
                MinutesToNews = record.MinutesToNews,
                Session = ComputeSession(record.CreatedAt),
                DayOfWeek = (int)record.CreatedAt.DayOfWeek == 0 ? 7 : (int)record.CreatedAt.DayOfWeek,
                HourUtc = record.CreatedAt.Hour,
                // Phase 1: Decision Snapshot
                TaDirection = record.SourceDirections.GetValueOrDefault("TechAnalysis", "NEUTRAL"),
                MlDirection = record.SourceDirections.GetValueOrDefault("LIGHTGBM", "NEUTRAL"),
                SmcDirection = record.SourceDirections.GetValueOrDefault("SMC", "NEUTRAL"),
                OfDirection = "NEUTRAL",
                ConflictCount = record.SourceDirections.Count(kv => kv.Value != "NEUTRAL" && kv.Value != record.Direction && !record.Direction.StartsWith("SHADOW")),
                ConfidenceBucket = $"{record.Probability / 10 * 10}-{record.Probability / 10 * 10 + 10}",
                WasCloseCall = Math.Abs(record.PnlBps) < 2.0,
                ConsecutiveLossesBefore = GetConsecutiveLosses(record.Asset, record.Timeframe),
                SecondsSinceLastTrade = ComputeSecondsSinceLastTrade(record.Asset, record.Timeframe, record.CreatedAt),
                ReasoningText = record.ReasoningText.Length > 2000 ? record.ReasoningText[..2000] : record.ReasoningText,
                MlModelVersion = record.MlModelVersion,
                MlModelAccuracy = record.MlModelAccuracy,
                MaxFavorableBps = record.MaxFavorableBps,
                MaxAdverseBps = record.MaxAdverseBps,
                PriceEntropy = record.PriceEntropy,
                TrendMaturity = record.TrendMaturity,
                PricePositionPct = record.PricePositionPct,
                BbSqueeze = record.BbSqueeze,
                TaTelemetry = record.TaTelemetry,
                MlTelemetry = record.MlTelemetry,
                SmcTelemetry = record.SmcTelemetry,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };

            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SaveTradeOutcomeAsync(outcomeRecord, record.Id);

            // Background neutral samples (HOLD) are strictly for offline Transformer training (export_dataset.py).
            // They must never affect Circuit Breaker, consecutive loss streaks, post-mortem dumps, or online RL weights.
            if (record.Direction is "HOLD" or "NEUTRAL" or "SHADOW_NEUTRAL")
            {
                return;
            }

            bool isLiveDirectional = record.Direction is "BUY" or "PUT";
            bool isShadowDirectional = record.Direction is "SHADOW_BUY" or "SHADOW_PUT";
            if (isShadowDirectional && CircuitBreaker != null && record.Probability >= 57)
            {
                _ = CircuitBreaker.CheckSymbolStateAsync(record.Asset, record.Timeframe, record.WasCorrect, isShadow: true);
            }

            if (isLiveDirectional)
            {
                if (CircuitBreaker != null)
                {
                    _ = CircuitBreaker.CheckStateAsync(); // Fire and forget so it doesn't block verification
                    _ = CircuitBreaker.CheckSymbolStateAsync(record.Asset, record.Timeframe, record.WasCorrect, isShadow: false);
                }

                // 🔥 DRIFT DETECTION 🔥
                _ = DriftDetectorService.AnalyzeAssetDriftAsync(record.Asset, record.Timeframe);
            }

            bool isExactDoji = Math.Abs(record.PnlBps) < 1e-4; // Exact tie / Refund
            string lossKey = $"{record.Asset}_{record.Timeframe}";
            
            if (isExactDoji)
            {
                if (isLiveDirectional)
                {
                    // A tie breaks the loss streak!
                    _consecutiveLosses[lossKey] = 0;
                    ValutaMetrics.TradeOutcomesTotal.WithLabels(record.Asset, record.Timeframe, "TIE").Inc();
                    ValutaMetrics.ConsecutiveLosses.WithLabels(record.Asset, record.Timeframe).Set(0);
                }
                BotLogger.Warn($"[TradeOutcomeTracker] Doji for {record.Asset} (entry==exit). Saved to DB but skipping ML/WF feedback.");
                return;
            }

            bool wasCorrect = record.WasCorrect ?? false;
            double exitPriceVal = record.ExitPrice ?? record.EntryPrice;
            
            if (isLiveDirectional)
            {
                if (wasCorrect)
                {
                    _consecutiveLosses[lossKey] = 0;
                    ValutaMetrics.TradeOutcomesTotal.WithLabels(record.Asset, record.Timeframe, "WIN").Inc();
                    ValutaMetrics.ConsecutiveLosses.WithLabels(record.Asset, record.Timeframe).Set(0);
                }
                else
                {
                    int losses = _consecutiveLosses.AddOrUpdate(lossKey, 1, (_, count) => count + 1);
                    ValutaMetrics.TradeOutcomesTotal.WithLabels(record.Asset, record.Timeframe, "LOSS").Inc();
                    ValutaMetrics.ConsecutiveLosses.WithLabels(record.Asset, record.Timeframe).Set(losses);

                    // DUMP POST-MORTEM SNAPSHOT ON LIVE LOSS
                    try
                    {
                        string dumpDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "losses");
                        System.IO.Directory.CreateDirectory(dumpDir);
                        string dumpFile = System.IO.Path.Combine(dumpDir, $"loss_{record.Asset}_{record.Timeframe}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                        string json = System.Text.Json.JsonSerializer.Serialize(outcomeRecord, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        await System.IO.File.WriteAllTextAsync(dumpFile, json);
                        BotLogger.Warn($"[PostMortem] Snapshot saved for loss on {record.Asset} at {dumpFile}");
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error("Failed to write post-mortem snapshot", ex);
                    }
                }
            }

            // ── TRAINING DOJI FILTER (Exact / Micro-Tie Threshold) ──
            // In binary options, even a 1-pipette (0.1 bps = 0.001%) move determines a full payout or loss.
            // Only skip exact/micro ties (< 0.0001% = 0.01 bps, matching export_dataset.py min_pnl_threshold)
            // so AutoCalib and OnlineMetaLearner receive 97%+ of real directional outcomes.
            double pctDiff = record.EntryPrice > 1e-8 ? Math.Abs(exitPriceVal - record.EntryPrice) / record.EntryPrice * 100.0 : 0;
            const double dojiThreshold = 0.0001;
            bool isTrainingDoji = pctDiff < dojiThreshold;

            if (isTrainingDoji)
            {
                BotLogger.Warn($"[TradeOutcomeTracker] Training Doji (Noise) for {record.Asset} (pctDiff={pctDiff:F4}% < {dojiThreshold:F3}%). Skipping ML/WF feedback to prevent weight poisoning.");
                return;
            }

            if (isLiveDirectional && record.SourceDirections != null && record.SourceDirections.Count > 0)
            {
                string winDirection = exitPriceVal > record.EntryPrice ? "BUY" : "PUT";
                foreach (var kv in record.SourceDirections)
                {
                    if (kv.Value != "NEUTRAL")
                    {
                        bool wasSourceCorrect = (kv.Value == winDirection);
                        AutoCalib?.RecordSourceOutcome(kv.Key, record.Asset, record.Timeframe, wasSourceCorrect, kv.Value);
                    }
                }
            }

            string cleanDirection = record.Direction.StartsWith("SHADOW_") 
                ? record.Direction["SHADOW_".Length..] 
                : record.Direction;

            // V3 DualStreamPatchBrain / SequenceBrain learns from PostgreSQL trade_outcomes via export_dataset.py;
            // OnlineMetaLearner above handles real-time ensemble adaptation in C# without dead HTTP /feedback calls.

            BotLogger.Info($"[TradeOutcomeTracker] Verified trade {record.Id} ({record.Asset} {record.Timeframe}) -> {(wasCorrect ? "WIN" : "LOSS")}.");



// EVOLUTION DUMP removed: redundant query bypassing 99% shadow outcomes

        }
        catch (Exception ex)
        {
            BotLogger.Error($"[TradeOutcomeTracker] Error processing trade outcome for {record.Id}", ex);
        }
    }

}










