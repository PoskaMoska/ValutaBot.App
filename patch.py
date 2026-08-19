import sys

with open('MiniApp/Services/SignalTracker.cs', 'r', encoding='utf-8') as f:
    code = f.read()

# 1. Remove the verify Timer
timer_start = code.find('private static readonly Timer _verifyTimer;')
if timer_start != -1:
    timer_end = code.find('// ──────── Public Write API', timer_start)
    if timer_end != -1:
        code = code[:timer_start] + code[timer_end:]

# 2. Add Task.Run inside RecordPredictionAsync
record_str = 'await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SavePendingTradeAsync(record);'
insert_idx = code.find(record_str)
if insert_idx != -1:
    insert_idx += len(record_str)
    
    in_memory_task = '''
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(verifyDelaySecs));
                double? exitPrice = null;
                
                if (_livePrices.TryGetValue(asset, out double memPrice))
                {
                    exitPrice = memPrice;
                }
                else if (!isForex)
                {
                    if (BinanceWebSocketStream.TryGetLiveCandles(sym, "1m", out _, out _, out _, out double[] wsPrices, out _, out int count) && count > 0)
                    {
                        exitPrice = wsPrices[count - 1];
                    }
                }
                
                if (exitPrice.HasValue && exitPrice.Value > 0)
                {
                    double priceDiff = (exitPrice.Value - price) / price;
                    if (Math.Abs(priceDiff) < 1e-8)
                    {
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                    }
                    else
                    {
                        bool isCorrect = (direction == "BUY" && exitPrice.Value > price) || (direction == "PUT" && exitPrice.Value < price);
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.ResolvePendingTradeAsync(record.Id, exitPrice.Value, isCorrect);
                        
                        foreach (var kvp in record.SourceDirections)
                        {
                            if (kvp.Value == "NEUTRAL") continue;
                            bool isSourceCorrect = (kvp.Value == "BUY" && exitPrice.Value > price) || (kvp.Value == "PUT" && exitPrice.Value < price);
                            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.RecordSignalVoteAsync(kvp.Key, isSourceCorrect);
                        }
                        
                        if (TradeOutcomeTracker.WfEngine != null) {
                            TradeOutcomeTracker.WfEngine.ProcessOutcome(record.Asset, record.Timeframe, isCorrect);
                        }
                        if (TradeOutcomeTracker.CalibrationEngine != null) {
                            TradeOutcomeTracker.CalibrationEngine.RecordOutcome(record.Asset, record.Timeframe, isCorrect);
                        }
                        InvalidateSignalVotesCache();
                    }
                }
                else 
                {
                    await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[InMemoryVerify] Failed: {ex.Message}");
            }
        });
'''
    code = code[:insert_idx] + in_memory_task + code[insert_idx:]

with open('MiniApp/Services/SignalTracker.cs', 'w', encoding='utf-8') as f:
    f.write(code)

print("Done")
