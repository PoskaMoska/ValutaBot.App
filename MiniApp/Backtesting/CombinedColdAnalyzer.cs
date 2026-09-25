using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Indicators;
using ValutaBot.App.MiniApp.Services;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.App.MiniApp.Backtesting
{
    public static class CombinedColdAnalyzer
    {
        public static async Task RunAsync(int candleCount)
        {
            Console.WriteLine($"\n=== МУЛЬТИ-БЭКТЕСТ И НАКОПЛЕНИЕ БАЗЫ (C#) ===");
            string[] pairs = { "EURUSD", "AUDUSD", "GBPUSD", "USDCAD", "USDCHF", "USDJPY" };

            foreach (var asset in pairs)
            {
                Console.WriteLine($"\n[НАЧАЛО] Загрузка данных для {asset}...");
                string apiSymbol = asset.Insert(3, "/"); // EURUSD -> EUR/USD
                var candles = await HistoricalDataLoader.LoadAsync(candleCount, "1min", symbol: apiSymbol, forceRefresh: false);
                if (candles.Length < 100) 
                {
                    Console.WriteLine($"[ПРОПУСК] Для {asset} недостаточно данных ({candles.Length} свечей).");
                    continue;
                }
                
                Console.WriteLine($"Успешно загружено: {candles.Length} свечей для {asset}.");

                var taEngine = new TechnicalAnalysisEngine();
                var timeoutEngine = new TradeTimeoutEngine();
                int totalTrades = 0, wins = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                int startIdx = 200; 
                var pendingBatch = new List<TradeOutcomeRecord>();
                
                for (int i = startIdx; i < candles.Length - 5; i++)
                {
                    var slice = candles.AsSpan(0, i + 1);
                    var currentPrice = candles[i].Close;
                    double[] closes = new double[60], vols = new double[60];
                    int start = Math.Max(0, i - 59);
                    for (int j = 0; j < 60 && start + j <= i; j++)
                    {
                        closes[j] = candles[start + j].Close;
                        vols[j] = candles[start + j].Volume;
                    }
                    var taScoreResult = taEngine.ScoreTimeframe(asset, "1m", closes, vols, slice);
                    double taScore = taScoreResult.score;

                    var smcResult = SmcEngine.AnalyzeSmcStructure(asset, "1m", slice, currentPrice);
                    
                    var state = ContinuousStateEngine.EvaluateContinuousState(closes, asset, "1m");
                    var timeout = timeoutEngine.CalculateTimeout(asset, "1m", taScoreResult.atrVal, 1.0, smcResult, currentPrice, state, isForex: true);
                    int dynamicHorizon = timeout.TimeoutCandles;
                    
                    var futurePrice = candles[i + dynamicHorizon].Close;
                    bool actualUp = futurePrice > currentPrice;

                    double smcScore = 0;
                    if (smcResult.BosDirection == "BULLISH" || smcResult.SweepDirection == "BULLISH_SWEEP") smcScore += 1;
                    else if (smcResult.BosDirection == "BEARISH" || smcResult.SweepDirection == "BEARISH_SWEEP") smcScore -= 1;

                    var ofResult = OrderFlowEngine.AnalyzeOrderFlow(asset, "1m", slice, currentPrice);
                    double ofScore = 0;
                    var ofState = ofResult.OrderFlowState;
                    if (ofState != null && ofState.Contains("BULLISH")) ofScore += 1;
                    else if (ofState != null && ofState.Contains("BEARISH")) ofScore -= 1;
                    
                    double mlScore = 0;
                    // (ML Python Service call is bypassed during cold backtest generation 
                    // to prevent 1.5 million slow HTTP requests. We only need TA/SMC/OF features)
                    
                    double ensemble = (taScore * 0.5) + (smcScore * 1.5) + (ofScore * 0.3) + (mlScore * 1.0);

                    if (Math.Abs(ensemble) > 1.2)
                    {
                        bool isBuy = ensemble > 0;
                        bool isWin = isBuy == actualUp;
                        if (isWin) wins++;
                        totalTrades++;

                        double pnlBps = (futurePrice - currentPrice) / currentPrice * 10000.0 * (isBuy ? 1.0 : -1.0);

                        var record = new TradeOutcomeRecord
                        {
                            Id = $"BT_{asset}_{candles[i].Timestamp.Ticks}",
                            Asset = asset,
                            Timeframe = "1m",
                            Direction = isBuy ? "BUY" : "PUT",
                            EntryPrice = currentPrice,
                            ExitPrice = futurePrice,
                            PnlBps = pnlBps,
                            WasWin = isWin,
                            TaScore = taScore,
                            OfScore = ofScore,
                            SmcScore = smcScore,
                            MlScore = mlScore,
                            MlProb = 0.5, // ML prediction bypassed
                            CreatedAt = candles[i].Timestamp.ToString("O"),
                            VerifiedAt = candles[i + dynamicHorizon].Timestamp.ToString("O"),
                            // === Rich Features for LightGBM ===
                            SmcBosDir = smcResult.BosDirection ?? "NONE",
                            SmcHasOb  = smcResult.HasOrderBlock,
                            SmcHasFvg = smcResult.HasFvg,
                            OfDeltaRatio = ofResult.DeltaRatio,
                            OfState   = ofResult.OrderFlowState ?? "NEUTRAL",
                            DynamicHorizon = dynamicHorizon
                        };

                        pendingBatch.Add(record);
                    }

                    if (i % 1000 == 0)
                    {
                        if (pendingBatch.Count > 0)
                        {
                            await TradeRepository.SaveTradeOutcomesBatchAsync(pendingBatch);
                            pendingBatch.Clear();
                        }
                        Console.WriteLine($"[{asset}] Обработано {i} / {candles.Length} свечей. Сделок: {totalTrades}. Время: {sw.ElapsedMilliseconds / 1000.0:F1} сек.");
                    }
                }
                
                if (pendingBatch.Count > 0)
                {
                    await TradeRepository.SaveTradeOutcomesBatchAsync(pendingBatch);
                    pendingBatch.Clear();
                }

                Console.WriteLine($"\n[{asset}] Завершен прогон за {sw.ElapsedMilliseconds / 1000.0} сек.");
                double wr = totalTrades > 0 ? wins * 100.0 / totalTrades : 0;
                Console.WriteLine($"[{asset}] Сделок сохранено: {totalTrades} | ИТОГОВЫЙ WIN RATE: {wr:F2}%");
                Console.WriteLine("---------------------------------------------------------");
            }
            Console.WriteLine("\n=== ВСЕ ПАРЫ УСПЕШНО ОБРАБОТАНЫ ===\n");
        }
    }
}
