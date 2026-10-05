"""
Comprehensive fix: Add rich market context columns to trade_outcomes.

New columns added:
  trade_outcomes: market_regime, velocity_regime, atr_at_signal, adx_at_signal,
                  rsi_at_signal, higher_tf_aligned, minutes_to_news,
                  session, day_of_week, hour_utc

Columns that flow through pending_trades (captured at signal time):
  market_regime, velocity_regime, atr_at_signal, adx_at_signal,
  rsi_at_signal, higher_tf_aligned, minutes_to_news

Columns computed at verification time from created_at:
  session, day_of_week, hour_utc
"""
import sys

# ─────────────────────────────────────────────────────────────────
# 1. DbConnectionFactory.cs — add migrations for new columns
# ─────────────────────────────────────────────────────────────────
print("Patching DbConnectionFactory.cs ...")
with open("MiniApp/Data/DbConnectionFactory.cs", "r", encoding="utf-8") as f:
    code = f.read()

# Add migrations for trade_outcomes
old = "BEGIN ALTER TABLE pending_trades ADD COLUMN dynamic_horizon INTEGER NOT NULL DEFAULT 3; EXCEPTION WHEN duplicate_column THEN END;"
new = """BEGIN ALTER TABLE pending_trades ADD COLUMN dynamic_horizon INTEGER NOT NULL DEFAULT 3; EXCEPTION WHEN duplicate_column THEN END;
                    -- Rich market context (pending_trades → trade_outcomes pipeline)
                    BEGIN ALTER TABLE pending_trades ADD COLUMN market_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN velocity_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN atr_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN adx_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN rsi_at_signal DOUBLE PRECISION NOT NULL DEFAULT 50.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN higher_tf_aligned BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN minutes_to_news INTEGER NOT NULL DEFAULT -1; EXCEPTION WHEN duplicate_column THEN END;"""

if old in code:
    code = code.replace(old, new)
    print("  ✓ pending_trades migrations added")
else:
    print("  ✗ Could not find pending_trades dynamic_horizon migration anchor")

# Add migrations for trade_outcomes
old2 = "BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_state TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;"
new2 = """BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_state TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    -- Rich market context columns for analytics and ML feature engineering
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN market_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN velocity_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN atr_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN adx_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN rsi_at_signal DOUBLE PRECISION NOT NULL DEFAULT 50.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN higher_tf_aligned BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN minutes_to_news INTEGER NOT NULL DEFAULT -1; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN session TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN day_of_week INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN hour_utc INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;"""

if old2 in code:
    code = code.replace(old2, new2)
    print("  ✓ trade_outcomes migrations added")
else:
    print("  ✗ Could not find trade_outcomes of_state migration anchor")

with open("MiniApp/Data/DbConnectionFactory.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("  Done.\n")

# ─────────────────────────────────────────────────────────────────
# 2. TradeOutcomeRecord — add new fields
# ─────────────────────────────────────────────────────────────────
print("Patching TradeRepository.cs (TradeOutcomeRecord) ...")
with open("MiniApp/Data/Repositories/TradeRepository.cs", "r", encoding="utf-8") as f:
    code = f.read()

old = '        public int    DynamicHorizon { get; set; }            // candles until expiry (1-4)\n    }'
new = '''        public int    DynamicHorizon { get; set; }            // candles until expiry (1-4)

        // === Market Context for Analytics & ML ===
        public string MarketRegime   { get; set; } = "UNKNOWN";  // TREND | FLAT | CHAOS (GMM from Python)
        public string VelocityRegime { get; set; } = "UNKNOWN";  // SLOW | MODERATE | FAST | EXPLOSIVE
        public double AtrAtSignal    { get; set; }               // ATR in price units at signal time
        public double AdxAtSignal    { get; set; }               // ADX 0-100 at signal time
        public double RsiAtSignal    { get; set; } = 50.0;       // RSI 0-100 at signal time
        public bool   HigherTfAligned { get; set; }              // higher TF candle direction matches signal
        public int    MinutesToNews  { get; set; } = -1;         // -1 = no upcoming news

        // === Time Context (computed at verification) ===
        public string Session        { get; set; } = "UNKNOWN";  // TOKYO | LONDON | NY | OVERLAP | QUIET
        public int    DayOfWeek      { get; set; }               // 1=Mon … 7=Sun
        public int    HourUtc        { get; set; }               // 0-23 UTC
    }'''

if old in code:
    code = code.replace(old, new)
    print("  ✓ TradeOutcomeRecord fields added")
else:
    print("  ✗ Could not find TradeOutcomeRecord DynamicHorizon anchor")

# Update SaveTradeOutcomeAsync SQL
old_insert = '''INSERT INTO trade_outcomes (id, asset, timeframe, direction, entry_price, exit_price, pnl_bps, was_win, probability, ta_score, of_score, smc_score, ml_prob, ml_score, features_json, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon, created_at, verified_at)
                VALUES (@Id, @Asset, @Timeframe, @Direction, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @CreatedAt, @VerifiedAt)'''
new_insert = '''INSERT INTO trade_outcomes (id, asset, timeframe, direction, entry_price, exit_price, pnl_bps, was_win, probability, ta_score, of_score, smc_score, ml_prob, ml_score, features_json, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon, market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news, session, day_of_week, hour_utc, created_at, verified_at)
                VALUES (@Id, @Asset, @Timeframe, @Direction, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews, @Session, @DayOfWeek, @HourUtc, @CreatedAt, @VerifiedAt)'''

if old_insert in code:
    code = code.replace(old_insert, new_insert)
    print("  ✓ SaveTradeOutcomeAsync INSERT updated")
else:
    print("  ✗ Could not find SaveTradeOutcomeAsync INSERT")

# Update the ON CONFLICT SET clause 
old_conflict = '''ON CONFLICT (id) DO UPDATE SET
                    exit_price = EXCLUDED.exit_price, pnl_bps = EXCLUDED.pnl_bps, was_win = EXCLUDED.was_win, verified_at = EXCLUDED.verified_at, probability = EXCLUDED.probability'''
new_conflict = '''ON CONFLICT (id) DO UPDATE SET
                    exit_price = EXCLUDED.exit_price, pnl_bps = EXCLUDED.pnl_bps, was_win = EXCLUDED.was_win, verified_at = EXCLUDED.verified_at, probability = EXCLUDED.probability,
                    market_regime = EXCLUDED.market_regime, velocity_regime = EXCLUDED.velocity_regime, atr_at_signal = EXCLUDED.atr_at_signal, adx_at_signal = EXCLUDED.adx_at_signal,
                    rsi_at_signal = EXCLUDED.rsi_at_signal, higher_tf_aligned = EXCLUDED.higher_tf_aligned, minutes_to_news = EXCLUDED.minutes_to_news,
                    session = EXCLUDED.session, day_of_week = EXCLUDED.day_of_week, hour_utc = EXCLUDED.hour_utc'''

if old_conflict in code:
    code = code.replace(old_conflict, new_conflict)
    print("  ✓ ON CONFLICT clause updated")
else:
    print("  ✗ Could not find ON CONFLICT clause - checking current structure")
    import re
    matches = re.findall(r'ON CONFLICT.*?\n', code)
    for m in matches[:3]:
        print("    Found:", m[:100])

# Update the Dapper anonymous object for SaveTradeOutcomeAsync
old_dapper = '''                    outcome.DynamicHorizon,
                    outcome.FeaturesJson,'''
new_dapper = '''                    outcome.DynamicHorizon,
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
                    outcome.FeaturesJson,'''

if old_dapper in code:
    code = code.replace(old_dapper, new_dapper)
    print("  ✓ Dapper anonymous object updated")
else:
    print("  ✗ Could not find Dapper object - searching for DynamicHorizon,")
    idx = code.find("DynamicHorizon,")
    if idx > 0:
        print("    Found at:", code[idx-50:idx+100])

# Update SavePendingTradeAsync - SQL
old_pending_sql = '''INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, probability, ta_score, of_score, smc_score, ml_prob, ml_score, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @FeaturesJson)'''
new_pending_sql = '''INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, probability, ta_score, of_score, smc_score, ml_prob, ml_score, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon, market_regime, velocity_regime, atr_at_signal, adx_at_signal, rsi_at_signal, higher_tf_aligned, minutes_to_news, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @MarketRegime, @VelocityRegime, @AtrAtSignal, @AdxAtSignal, @RsiAtSignal, @HigherTfAligned, @MinutesToNews, @FeaturesJson)'''

if old_pending_sql in code:
    code = code.replace(old_pending_sql, new_pending_sql)
    print("  ✓ SavePendingTradeAsync SQL updated")
else:
    print("  ✗ Could not find SavePendingTradeAsync SQL")

# Update SavePendingTradeAsync Dapper object
old_pending_dapper = '''                    record.DynamicHorizon,
                    record.FeaturesJson
                });'''
new_pending_dapper = '''                    record.DynamicHorizon,
                    record.MarketRegime,
                    record.VelocityRegime,
                    record.AtrAtSignal,
                    record.AdxAtSignal,
                    record.RsiAtSignal,
                    record.HigherTfAligned,
                    record.MinutesToNews,
                    record.FeaturesJson
                });'''

if old_pending_dapper in code:
    code = code.replace(old_pending_dapper, new_pending_dapper)
    print("  ✓ SavePendingTradeAsync Dapper object updated")
else:
    print("  ✗ Could not find SavePendingTradeAsync Dapper object")

# Update GetPendingTradesToVerifyAsync SELECT
old_get_sql = '''smc_bos_dir as "SmcBosDir", smc_has_ob as "SmcHasOb", smc_has_fvg as "SmcHasFvg", of_delta_ratio as "OfDeltaRatio", of_state as "OfState", dynamic_horizon as "DynamicHorizon"
                FROM pending_trades'''
new_get_sql = '''smc_bos_dir as "SmcBosDir", smc_has_ob as "SmcHasOb", smc_has_fvg as "SmcHasFvg", of_delta_ratio as "OfDeltaRatio", of_state as "OfState", dynamic_horizon as "DynamicHorizon",
                       market_regime as "MarketRegime", velocity_regime as "VelocityRegime", atr_at_signal as "AtrAtSignal", adx_at_signal as "AdxAtSignal",
                       rsi_at_signal as "RsiAtSignal", higher_tf_aligned as "HigherTfAligned", minutes_to_news as "MinutesToNews"
                FROM pending_trades'''

if old_get_sql in code:
    code = code.replace(old_get_sql, new_get_sql)
    print("  ✓ GetPendingTradesToVerifyAsync SELECT updated")
else:
    print("  ✗ Could not find GetPendingTradesToVerifyAsync SELECT")

# Update mapping in GetPendingTradesToVerifyAsync
old_map = '''                DynamicHorizon = r.DynamicHorizon != null ? Convert.ToInt32(r.DynamicHorizon) : 3
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();'''
new_map = '''                DynamicHorizon = r.DynamicHorizon != null ? Convert.ToInt32(r.DynamicHorizon) : 3,
                MarketRegime = r.MarketRegime ?? "UNKNOWN",
                VelocityRegime = r.VelocityRegime ?? "UNKNOWN",
                AtrAtSignal = r.AtrAtSignal != null ? Convert.ToDouble(r.AtrAtSignal) : 0.0,
                AdxAtSignal = r.AdxAtSignal != null ? Convert.ToDouble(r.AdxAtSignal) : 0.0,
                RsiAtSignal = r.RsiAtSignal != null ? Convert.ToDouble(r.RsiAtSignal) : 50.0,
                HigherTfAligned = r.HigherTfAligned != null ? Convert.ToBoolean(r.HigherTfAligned) : false,
                MinutesToNews = r.MinutesToNews != null ? Convert.ToInt32(r.MinutesToNews) : -1
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();'''

if old_map in code:
    code = code.replace(old_map, new_map)
    print("  ✓ GetPendingTradesToVerifyAsync mapping updated")
else:
    print("  ✗ Could not find GetPendingTradesToVerifyAsync mapping")

with open("MiniApp/Data/Repositories/TradeRepository.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("  Done.\n")

# ─────────────────────────────────────────────────────────────────
# 3. PredictionRecord — add new fields
# ─────────────────────────────────────────────────────────────────
print("Patching SignalTracker.cs (PredictionRecord) ...")
with open("MiniApp/Services/SignalTracker.cs", "r", encoding="utf-8") as f:
    code = f.read()

old = '''        public string SmcBosDir    { get; set; } = "NONE";
        public bool   SmcHasOb     { get; set; }
        public bool   SmcHasFvg    { get; set; }
        public double OfDeltaRatio { get; set; }
        public string OfState      { get; set; } = "NEUTRAL";
        public int    DynamicHorizon { get; set; } = 3;
    }'''
new = '''        public string SmcBosDir    { get; set; } = "NONE";
        public bool   SmcHasOb     { get; set; }
        public bool   SmcHasFvg    { get; set; }
        public double OfDeltaRatio { get; set; }
        public string OfState      { get; set; } = "NEUTRAL";
        public int    DynamicHorizon { get; set; } = 3;

        // Market context — captured at signal time, persisted through pending_trades
        public string MarketRegime    { get; set; } = "UNKNOWN";
        public string VelocityRegime  { get; set; } = "UNKNOWN";
        public double AtrAtSignal     { get; set; }
        public double AdxAtSignal     { get; set; }
        public double RsiAtSignal     { get; set; } = 50.0;
        public bool   HigherTfAligned { get; set; }
        public int    MinutesToNews   { get; set; } = -1;
    }'''

if old in code:
    code = code.replace(old, new)
    print("  ✓ PredictionRecord fields added")
else:
    print("  ✗ Could not find PredictionRecord SMC anchor")

# Update RecordPredictionAsync signature
old_sig = '''        string smcBosDir = "NONE", bool smcHasOb = false, bool smcHasFvg = false,
        double ofDeltaRatio = 1.0, string ofState = "NEUTRAL")'''
new_sig = '''        string smcBosDir = "NONE", bool smcHasOb = false, bool smcHasFvg = false,
        double ofDeltaRatio = 1.0, string ofState = "NEUTRAL",
        string marketRegime = "UNKNOWN", string velocityRegime = "UNKNOWN",
        double atrAtSignal = 0.0, double adxAtSignal = 0.0, double rsiAtSignal = 50.0,
        bool higherTfAligned = false, int minutesToNews = -1)'''

if old_sig in code:
    code = code.replace(old_sig, new_sig)
    print("  ✓ RecordPredictionAsync signature updated")
else:
    print("  ✗ Could not find RecordPredictionAsync signature")

# Update record initializer
old_init = '''            SmcBosDir = smcBosDir,
            SmcHasOb = smcHasOb,
            SmcHasFvg = smcHasFvg,
            OfDeltaRatio = ofDeltaRatio,
            OfState = ofState,
            DynamicHorizon = expiryCandles
        };'''
new_init = '''            SmcBosDir = smcBosDir,
            SmcHasOb = smcHasOb,
            SmcHasFvg = smcHasFvg,
            OfDeltaRatio = ofDeltaRatio,
            OfState = ofState,
            DynamicHorizon = expiryCandles,
            MarketRegime = marketRegime,
            VelocityRegime = velocityRegime,
            AtrAtSignal = atrAtSignal,
            AdxAtSignal = adxAtSignal,
            RsiAtSignal = rsiAtSignal,
            HigherTfAligned = higherTfAligned,
            MinutesToNews = minutesToNews
        };'''

if old_init in code:
    code = code.replace(old_init, new_init)
    print("  ✓ Record initializer updated")
else:
    print("  ✗ Could not find record initializer")

with open("MiniApp/Services/SignalTracker.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("  Done.\n")

# ─────────────────────────────────────────────────────────────────
# 4. TradeOutcomeTracker.cs — map new fields + compute session/time
# ─────────────────────────────────────────────────────────────────
print("Patching TradeOutcomeTracker.cs ...")
with open("MiniApp/Services/TradeOutcomeTracker.cs", "r", encoding="utf-8") as f:
    code = f.read()

old = '''                SmcBosDir = record.SmcBosDir,
                SmcHasOb = record.SmcHasOb,
                SmcHasFvg = record.SmcHasFvg,
                OfDeltaRatio = record.OfDeltaRatio,
                OfState = record.OfState,
                DynamicHorizon = record.DynamicHorizon,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };'''
new = '''                SmcBosDir = record.SmcBosDir,
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
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };'''

if old in code:
    code = code.replace(old, new)
    print("  ✓ outcomeRecord mapping updated")
else:
    print("  ✗ Could not find outcomeRecord mapping")

# Add ComputeSession helper before the closing brace of the class
old_class_end = "    public static int GetConsecutiveLosses(string asset, string timeframe)"
new_class_end = '''    /// <summary>
    /// Maps a UTC signal time to a forex trading session label.
    /// OVERLAP (London+NY) is the highest-volume window and most predictable for subminute TFs.
    /// </summary>
    private static string ComputeSession(DateTime utc)
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

    public static int GetConsecutiveLosses(string asset, string timeframe)'''

if old_class_end in code:
    code = code.replace(old_class_end, new_class_end)
    print("  ✓ ComputeSession helper added")
else:
    print("  ✗ Could not find class anchor for ComputeSession")

with open("MiniApp/Services/TradeOutcomeTracker.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("  Done.\n")

# ─────────────────────────────────────────────────────────────────
# 5. MarketAnalysisOrchestrator.cs — pass new data when recording
# ─────────────────────────────────────────────────────────────────
print("Patching MarketAnalysisOrchestrator.cs ...")
with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "r", encoding="utf-8") as f:
    code = f.read()

# The two RecordPredictionAsync calls already end with ofResult.OrderFlowState)
# We need to add the new context parameters. Both call sites.

old_call1 = '''                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", ofResult.DeltaRatio, ofResult.OrderFlowState);
            dbSw.Stop();
            traceLines.Add($"[8. '''
new_call1 = '''                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", ofResult.DeltaRatio, ofResult.OrderFlowState,
                mlPrediction?.ModelVersion?.Split('/').LastOrDefault() ?? "UNKNOWN",
                state.VelocityRegime ?? "UNKNOWN",
                mainAtr, mainAdx, taResult.rsiVal,
                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1);
            dbSw.Stop();
            traceLines.Add($"[8. '''

if old_call1 in code:
    code = code.replace(old_call1, new_call1)
    print("  ✓ Main RecordPredictionAsync call updated")
else:
    print("  ✗ Could not find main RecordPredictionAsync call")

old_call2 = '''                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", ofResult.DeltaRatio, ofResult.OrderFlowState);
             dbSw.Stop();'''
new_call2 = '''                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", ofResult.DeltaRatio, ofResult.OrderFlowState,
                mlPrediction?.ModelVersion?.Split('/').LastOrDefault() ?? "UNKNOWN",
                state.VelocityRegime ?? "UNKNOWN",
                mainAtr, mainAdx, taResult.rsiVal,
                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1);
             dbSw.Stop();'''

if old_call2 in code:
    code = code.replace(old_call2, new_call2)
    print("  ✓ SHADOW RecordPredictionAsync call updated")
else:
    print("  ✗ Could not find SHADOW RecordPredictionAsync call")

with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("  Done.\n")

print("All patches complete!")
