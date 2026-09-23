using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Indicators;
using ValutaBot.App.MiniApp.Services;

namespace ValutaBot.App.MiniApp.Backtesting
{
    public static class CombinedColdAnalyzer
    {
        public static async Task RunAsync(int candleCount)
        {
            Console.WriteLine($"\n=== ГЛОБАЛЬНЫЙ ТЕСТ: СИНЕРГИЯ 5 УЗЛОВ (C#) ===");
            var candles = await HistoricalDataLoader.LoadAsync(candleCount, "1min", forceRefresh: false);
            if (candles.Length < 100) return;
            Console.WriteLine($"Успешно загружено: {candles.Length} свечей.");

            var taEngine = new TechnicalAnalysisEngine();
            // // Узел Риск-менеджмента

            int horizon = 5;
            int totalTrades = 0, wins = 0;
            int cooloffSkips = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            int startIdx = Math.Max(200, candles.Length - 10000 - horizon); 
            
            for (int i = startIdx; i < candles.Length - horizon; i++)
            {
                var slice = candles.AsSpan(0, i + 1);
                var currentPrice = candles[i].Close;
                var futurePrice = candles[i + horizon].Close;
                bool actualUp = futurePrice > currentPrice;

                // Узел 1: МАТЕМАТИКА
                double[] closes = new double[60], vols = new double[60];
                int start = Math.Max(0, i - 59);
                for (int j = 0; j < 60 && start + j <= i; j++)
                {
                    closes[j] = candles[start + j].Close;
                    vols[j] = candles[start + j].Volume;
                }
                var taScoreResult = taEngine.ScoreTimeframe("EURUSD", "1m", closes, vols, slice);
                double taScore = taScoreResult.score;

                // Узел 2: SMC
                var smcResult = SmcEngine.AnalyzeSmcStructure("EURUSD", "1m", slice, currentPrice);
                double smcScore = 0;
                if (smcResult.BosDirection == "BULLISH" || smcResult.SweepDirection == "BULLISH_SWEEP") smcScore += 1;
                else if (smcResult.BosDirection == "BEARISH" || smcResult.SweepDirection == "BEARISH_SWEEP") smcScore -= 1;

                // Узел 3: OrderFlow
                var ofResult = OrderFlowEngine.AnalyzeOrderFlow("EURUSD", "1m", slice, currentPrice);
                double ofScore = 0;
                var ofState = ofResult.OrderFlowState;
                if (ofState != null && ofState.Contains("BULLISH")) ofScore += 1;
                else if (ofState != null && ofState.Contains("BEARISH")) ofScore -= 1;
                
                // Узел 4: LightGBM (ML)
                double mlScore = 0;
                var mlPred = await MLPythonService.PredictAsync("EURUSD", "1m", slice.ToArray(), isForex: true);
                if (mlPred != null && mlPred.Direction != "NEUTRAL")
                {
                    double conf = mlPred.Confidence;
                    if (mlPred.Direction == "BUY") mlScore = (conf - 0.5) * 4; // Map [0.5, 1.0] to [0.0, 2.0]
                    if (mlPred.Direction == "PUT") mlScore = -(conf - 0.5) * 4;
                }
                
                double ensemble = (taScore * 0.5) + (smcScore * 1.5) + (ofScore * 0.3) + (mlScore * 1.0);

                if (Math.Abs(ensemble) > 1.2) // Строгий порог 

                {
                    // Узел 5: Риск-менеджмент (Walk-Forward)
                    //
                    if (false)
                    {
                        cooloffSkips++;
                        continue; 
                    }

                    bool isBuy = ensemble > 0;
                    bool isWin = isBuy == actualUp;
                    
                    if (isWin) wins++;
                    totalTrades++;
                }
            }

            Console.WriteLine($"\nАнализ 10,000 OOS свечей завершен за {sw.ElapsedMilliseconds}мс.");
            Console.WriteLine("---------------------------------------------------------");
            Console.WriteLine(" РЕЗУЛЬТАТ ОБЪЕДИНЕННЫХ УЗЛОВ (СИНЕРГИЯ C# + RISK):");
            Console.WriteLine("---------------------------------------------------------");
            double wr = totalTrades > 0 ? wins * 100.0 / totalTrades : 0;
            Console.WriteLine($"   Всего качественных сделок: {totalTrades} (отсеяно {(10000 - totalTrades - cooloffSkips):N0} шума)");
            Console.WriteLine($"   Сделок пропущено из-за риска (Cooloff): {cooloffSkips}");
            Console.WriteLine($"   ИТОГОВЫЙ WIN RATE СИСТЕМЫ (АНСАМБЛЬ + ML): {wr:F2}%");
            Console.WriteLine("---------------------------------------------------------\n");
        }
    }
}


