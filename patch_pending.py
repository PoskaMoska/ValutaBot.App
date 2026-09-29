import re

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_insert = '''INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, ta_score, of_score, smc_score, ml_prob, ml_score)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore)'''

new_insert = '''INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions, ta_score, of_score, smc_score, ml_prob, ml_score, features_json)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BrokerSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr, @TaScore, @OfScore, @SmcScore, @MlProb, @MlScore, @FeaturesJson)'''

old_dapper = '''record.SmcScore,
                    record.MlProb,
                    record.MlScore
                });'''

new_dapper = '''record.SmcScore,
                    record.MlProb,
                    record.MlScore,
                    record.FeaturesJson
                });'''

content = content.replace(old_insert, new_insert)
content = content.replace(old_dapper, new_dapper)

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'w', encoding='utf-8') as f:
    f.write(content)
