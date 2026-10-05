"""
Phase 1: Complete Decision Snapshot
Adds: source directions, conflict count, reasoning_text, ml_model_version,
      ml_model_accuracy, confidence_bucket, was_close_call, 
      consecutive_losses_before, seconds_since_last_trade, MFE/MAE placeholders
"""
import sys, os
sys.stdout.reconfigure(encoding='utf-8')

def patch(path, find, replace, label):
    with open(path, "r", encoding="utf-8") as f:
        code = f.read()
    if find in code:
        code = code.replace(find, replace)
        with open(path, "w", encoding="utf-8") as f:
            f.write(code)
        print(f"  OK: {label}")
        return True
    else:
        print(f"  MISS: {label}")
        return False

# ═══════════════════════════════════════════════════════════
# 1. DbConnectionFactory — migrations
# ═══════════════════════════════════════════════════════════
print("=== 1. DbConnectionFactory.cs ===")

patch("MiniApp/Data/DbConnectionFactory.cs",
    "BEGIN ALTER TABLE trade_outcomes ADD COLUMN hour_utc INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;",
    """BEGIN ALTER TABLE trade_outcomes ADD COLUMN hour_utc INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    -- Phase 1: Complete Decision Snapshot
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ta_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN smc_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN conflict_count INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN confidence_bucket TEXT NOT NULL DEFAULT '50-60'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN was_close_call BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN consecutive_losses_before INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN seconds_since_last_trade INTEGER NOT NULL DEFAULT -1; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN reasoning_text TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_model_version TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_model_accuracy DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN max_favorable_bps DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN max_adverse_bps DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    -- pending_trades: reasoning + ml metadata for pipeline
                    BEGIN ALTER TABLE pending_trades ADD COLUMN reasoning_text TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ml_model_version TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ml_model_accuracy DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;""",
    "trade_outcomes + pending_trades Phase1 migrations")

# ═══════════════════════════════════════════════════════════
# 2. TradeOutcomeRecord — new properties
# ═══════════════════════════════════════════════════════════
print("\n=== 2. TradeOutcomeRecord ===")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """        // === Time Context (computed at verification time from CreatedAt) ===
        public string Session    { get; set; } = "UNKNOWN"; // TOKYO | LONDON | NY | OVERLAP | QUIET
        public int    DayOfWeek  { get; set; }               // 1=Mon ... 7=Sun
        public int    HourUtc    { get; set; }               // 0-23 UTC
    }""",
    """        // === Time Context (computed at verification time from CreatedAt) ===
        public string Session    { get; set; } = "UNKNOWN"; // TOKYO | LONDON | NY | OVERLAP | QUIET
        public int    DayOfWeek  { get; set; }               // 1=Mon ... 7=Sun
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
    }""",
    "TradeOutcomeRecord Phase1 properties")

# ═══════════════════════════════════════════════════════════
# 3. SaveTradeOutcomeAsync — extend INSERT + Dapper
# ═══════════════════════════════════════════════════════════
print("\n=== 3. SaveTradeOutcomeAsync ===")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                     session, day_of_week, hour_utc)
                    VALUES (@Id, @Asset, @Timeframe, @Direction, @Probability, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson, @CreatedAt::timestamptz, @VerifiedAt::timestamptz,
                            @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon,
                            @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews,
                            @Session, @DayOfWeek, @HourUtc)""",
    """                     session, day_of_week, hour_utc,
                     ta_direction, ml_direction, smc_direction, of_direction, conflict_count,
                     confidence_bucket, was_close_call, consecutive_losses_before, seconds_since_last_trade,
                     reasoning_text, ml_model_version, ml_model_accuracy, max_favorable_bps, max_adverse_bps)
                    VALUES (@Id, @Asset, @Timeframe, @Direction, @Probability, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson, @CreatedAt::timestamptz, @VerifiedAt::timestamptz,
                            @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon,
                            @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews,
                            @Session, @DayOfWeek, @HourUtc,
                            @TaDirection, @MlDirection, @SmcDirection, @OfDirection, @ConflictCount,
                            @ConfidenceBucket, @WasCloseCall, @ConsecutiveLossesBefore, @SecondsSinceLastTrade,
                            @ReasoningText, @MlModelVersion, @MlModelAccuracy, @MaxFavorableBps, @MaxAdverseBps)""",
    "INSERT columns + VALUES")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                        hour_utc = EXCLUDED.hour_utc
                """,
    """                        hour_utc = EXCLUDED.hour_utc,
                        ta_direction = EXCLUDED.ta_direction,
                        ml_direction = EXCLUDED.ml_direction,
                        smc_direction = EXCLUDED.smc_direction,
                        of_direction = EXCLUDED.of_direction,
                        conflict_count = EXCLUDED.conflict_count,
                        confidence_bucket = EXCLUDED.confidence_bucket,
                        was_close_call = EXCLUDED.was_close_call,
                        consecutive_losses_before = EXCLUDED.consecutive_losses_before,
                        seconds_since_last_trade = EXCLUDED.seconds_since_last_trade,
                        reasoning_text = EXCLUDED.reasoning_text,
                        ml_model_version = EXCLUDED.ml_model_version,
                        ml_model_accuracy = EXCLUDED.ml_model_accuracy,
                        max_favorable_bps = EXCLUDED.max_favorable_bps,
                        max_adverse_bps = EXCLUDED.max_adverse_bps
                """,
    "ON CONFLICT SET")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                    outcome.DayOfWeek,
                    outcome.HourUtc
                });""",
    """                    outcome.DayOfWeek,
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
                    outcome.MaxAdverseBps
                });""",
    "Dapper object")

# ═══════════════════════════════════════════════════════════
# 4. PredictionRecord — add reasoning + ml metadata
# ═══════════════════════════════════════════════════════════
print("\n=== 4. PredictionRecord ===")

patch("MiniApp/Services/SignalTracker.cs",
    """        // Market context -- captured at signal time, flows through pending_trades to trade_outcomes
        public string MarketRegime    { get; set; } = "UNKNOWN";
        public string VelocityRegime  { get; set; } = "UNKNOWN";
        public double AtrAtSignal     { get; set; }
        public double AdxAtSignal     { get; set; }
        public double RsiAtSignal     { get; set; } = 50.0;
        public bool   HigherTfAligned { get; set; }
        public int    MinutesToNews   { get; set; } = -1;
    }""",
    """        // Market context -- captured at signal time, flows through pending_trades to trade_outcomes
        public string MarketRegime    { get; set; } = "UNKNOWN";
        public string VelocityRegime  { get; set; } = "UNKNOWN";
        public double AtrAtSignal     { get; set; }
        public double AdxAtSignal     { get; set; }
        public double RsiAtSignal     { get; set; } = 50.0;
        public bool   HigherTfAligned { get; set; }
        public int    MinutesToNews   { get; set; } = -1;

        // Decision reasoning + ML model metadata
        public string ReasoningText   { get; set; } = "";
        public string MlModelVersion  { get; set; } = "";
        public double MlModelAccuracy { get; set; }
    }""",
    "PredictionRecord reasoning + ml metadata")

# ═══════════════════════════════════════════════════════════
# 5. SavePendingTradeAsync — add reasoning + ml fields
# ═══════════════════════════════════════════════════════════
print("\n=== 5. SavePendingTradeAsync ===")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                     market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews, @FeaturesJson)""",
    """                     market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news, reasoning_text, ml_model_version, ml_model_accuracy, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews, @ReasoningText, @MlModelVersion, @MlModelAccuracy, @FeaturesJson)""",
    "SavePendingTradeAsync SQL")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                    record.MinutesToNews,
                    record.FeaturesJson
                });""",
    """                    record.MinutesToNews,
                    record.ReasoningText,
                    record.MlModelVersion,
                    record.MlModelAccuracy,
                    record.FeaturesJson
                });""",
    "SavePendingTradeAsync Dapper")

# ═══════════════════════════════════════════════════════════
# 6. GetPendingTradesToVerifyAsync — read reasoning + ml
# ═══════════════════════════════════════════════════════════
print("\n=== 6. GetPendingTradesToVerifyAsync ===")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                       rsi_at_signal as ""RsiAtSignal"", higher_tf_aligned as ""HigherTfAligned"", minutes_to_news as ""MinutesToNews""
                FROM pending_trades """,
    """                       rsi_at_signal as ""RsiAtSignal"", higher_tf_aligned as ""HigherTfAligned"", minutes_to_news as ""MinutesToNews"",
                       reasoning_text as ""ReasoningText"", ml_model_version as ""MlModelVersion"", ml_model_accuracy as ""MlModelAccuracy""
                FROM pending_trades """,
    "GetPendingTradesToVerifyAsync SELECT")

patch("MiniApp/Data/Repositories/TradeRepository.cs",
    """                MinutesToNews = r.MinutesToNews != null ? Convert.ToInt32(r.MinutesToNews) : -1
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();""",
    """                MinutesToNews = r.MinutesToNews != null ? Convert.ToInt32(r.MinutesToNews) : -1,
                ReasoningText = r.ReasoningText ?? "",
                MlModelVersion = r.MlModelVersion ?? "",
                MlModelAccuracy = r.MlModelAccuracy != null ? Convert.ToDouble(r.MlModelAccuracy) : 0.0
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();""",
    "GetPendingTradesToVerifyAsync mapping")

# ═══════════════════════════════════════════════════════════
# 7. SignalTracker.RecordPredictionAsync — accept + store
# ═══════════════════════════════════════════════════════════
print("\n=== 7. SignalTracker.RecordPredictionAsync ===")

patch("MiniApp/Services/SignalTracker.cs",
    """        bool higherTfAligned = false, int minutesToNews = -1)""",
    """        bool higherTfAligned = false, int minutesToNews = -1,
        string reasoningText = "", string mlModelVersion = "", double mlModelAccuracy = 0.0)""",
    "RecordPredictionAsync signature")

patch("MiniApp/Services/SignalTracker.cs",
    """            MinutesToNews = minutesToNews
        };""",
    """            MinutesToNews = minutesToNews,
            ReasoningText = reasoningText,
            MlModelVersion = mlModelVersion,
            MlModelAccuracy = mlModelAccuracy
        };""",
    "Record initializer")

# ═══════════════════════════════════════════════════════════
# 8. MarketAnalysisOrchestrator — pass reasoning + ml
# ═══════════════════════════════════════════════════════════
print("\n=== 8. MarketAnalysisOrchestrator ===")

# Main call
patch("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs",
    """                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1);
            dbSw.Stop();
            traceLines.Add($"[8. """,
    """                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1,
                consensus.CombinedReasoningText ?? "",
                mlPrediction?.ModelVersion ?? "",
                mlPrediction?.Accuracy ?? 0.0);
            dbSw.Stop();
            traceLines.Add($"[8. """,
    "Main RecordPredictionAsync call")

# Shadow call
patch("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs",
    """                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1);
             dbSw.Stop();""",
    """                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1,
                consensus.CombinedReasoningText ?? "",
                mlPrediction?.ModelVersion ?? "",
                mlPrediction?.Accuracy ?? 0.0);
             dbSw.Stop();""",
    "SHADOW RecordPredictionAsync call")

# ═══════════════════════════════════════════════════════════
# 9. TradeOutcomeTracker — compute Phase1 fields
# ═══════════════════════════════════════════════════════════
print("\n=== 9. TradeOutcomeTracker ===")

# Add _lastTradeTime tracker
patch("MiniApp/Services/TradeOutcomeTracker.cs",
    """private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _consecutiveLosses = new();""",
    """private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _consecutiveLosses = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastTradeTime = new();""",
    "_lastTradeTime tracker")

# Extend outcome mapping
patch("MiniApp/Services/TradeOutcomeTracker.cs",
    """                Session = ComputeSession(record.CreatedAt),
                DayOfWeek = (int)record.CreatedAt.DayOfWeek == 0 ? 7 : (int)record.CreatedAt.DayOfWeek,
                HourUtc = record.CreatedAt.Hour,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };""",
    """                Session = ComputeSession(record.CreatedAt),
                DayOfWeek = (int)record.CreatedAt.DayOfWeek == 0 ? 7 : (int)record.CreatedAt.DayOfWeek,
                HourUtc = record.CreatedAt.Hour,
                // Phase 1: Decision Snapshot
                TaDirection = record.SourceDirections.GetValueOrDefault("TechAnalysis", "NEUTRAL"),
                MlDirection = record.SourceDirections.GetValueOrDefault("LIGHTGBM", "NEUTRAL"),
                SmcDirection = record.SourceDirections.GetValueOrDefault("SMC", "NEUTRAL"),
                OfDirection = record.SourceDirections.GetValueOrDefault("OrderFlow", "NEUTRAL"),
                ConflictCount = record.SourceDirections.Count(kv => kv.Value != "NEUTRAL" && kv.Value != record.Direction && !record.Direction.StartsWith("SHADOW")),
                ConfidenceBucket = $"{record.Probability / 10 * 10}-{record.Probability / 10 * 10 + 10}",
                WasCloseCall = Math.Abs(record.PnlBps) < 2.0,
                ConsecutiveLossesBefore = GetConsecutiveLosses(record.Asset, record.Timeframe),
                SecondsSinceLastTrade = ComputeSecondsSinceLastTrade(record.Asset, record.Timeframe, record.CreatedAt),
                ReasoningText = record.ReasoningText.Length > 2000 ? record.ReasoningText[..2000] : record.ReasoningText,
                MlModelVersion = record.MlModelVersion,
                MlModelAccuracy = record.MlModelAccuracy,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };""",
    "outcomeRecord Phase1 mapping")

# Add ComputeSecondsSinceLastTrade helper
patch("MiniApp/Services/TradeOutcomeTracker.cs",
    """    /// <summary>
    /// Maps a UTC signal time to a forex trading session label.
    /// OVERLAP (London+NY) is the highest-volume window and most predictable for subminute TFs.
    /// </summary>""",
    """    private static int ComputeSecondsSinceLastTrade(string asset, string timeframe, DateTime createdAt)
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
    /// </summary>""",
    "ComputeSecondsSinceLastTrade helper")

print("\n=== ALL PATCHES COMPLETE ===")
