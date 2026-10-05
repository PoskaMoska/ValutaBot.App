import sys, re

with open("MiniApp/Data/Repositories/TradeRepository.cs", "r", encoding="utf-8") as f:
    code = f.read()

# Fix 1: SavePendingTradeAsync
sql_find = """INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, probability, ta_score, of_score, smc_score, ml_prob, ml_score, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson)"""
sql_replace = """INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, probability, ta_score, of_score, smc_score, ml_prob, ml_score, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, of_state, dynamic_horizon, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @Probability, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @SmcBosDir, @SmcHasOb, @SmcHasFvg, @OfDeltaRatio, @OfState, @DynamicHorizon, @FeaturesJson)"""
code = code.replace(sql_find, sql_replace)

dapper_find = """record.MlScore,
                    record.FeaturesJson
                });"""
dapper_replace = """record.MlScore,
                    record.SmcBosDir,
                    record.SmcHasOb,
                    record.SmcHasFvg,
                    record.OfDeltaRatio,
                    record.OfState,
                    record.DynamicHorizon,
                    record.FeaturesJson
                });"""
code = code.replace(dapper_find, dapper_replace)

# Fix 2: GetPendingTradesToVerifyAsync
sql_get_find = """probability as "Probability", features_json as "FeaturesJson", ta_score as "TaScore", of_score as "OfScore", smc_score as "SmcScore", ml_prob as "MlProb", ml_score as "MlScore"
                FROM pending_trades"""
sql_get_replace = """probability as "Probability", features_json as "FeaturesJson", ta_score as "TaScore", of_score as "OfScore", smc_score as "SmcScore", ml_prob as "MlProb", ml_score as "MlScore",
                       smc_bos_dir as "SmcBosDir", smc_has_ob as "SmcHasOb", smc_has_fvg as "SmcHasFvg", of_delta_ratio as "OfDeltaRatio", of_state as "OfState", dynamic_horizon as "DynamicHorizon"
                FROM pending_trades"""
code = code.replace(sql_get_find, sql_get_replace)

map_find = """MlScore = r.MlScore != null ? Convert.ToDouble(r.MlScore) : 0.0
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();"""
map_replace = """MlScore = r.MlScore != null ? Convert.ToDouble(r.MlScore) : 0.0,
                SmcBosDir = r.SmcBosDir ?? "NONE",
                SmcHasOb = r.SmcHasOb != null ? Convert.ToBoolean(r.SmcHasOb) : false,
                SmcHasFvg = r.SmcHasFvg != null ? Convert.ToBoolean(r.SmcHasFvg) : false,
                OfDeltaRatio = r.OfDeltaRatio != null ? Convert.ToDouble(r.OfDeltaRatio) : 1.0,
                OfState = r.OfState ?? "NEUTRAL",
                DynamicHorizon = r.DynamicHorizon != null ? Convert.ToInt32(r.DynamicHorizon) : 3
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();"""
code = code.replace(map_find, map_replace)

with open("MiniApp/Data/Repositories/TradeRepository.cs", "w", encoding="utf-8") as f:
    f.write(code)

print("Replaced successfully")
