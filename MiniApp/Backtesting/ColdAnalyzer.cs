using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Indicators;

namespace ValutaBot.App.MiniApp.Backtesting
{
    public static class ColdAnalyzer
    {
        public static async Task RunComponentAnalysisAsync(int candleCount)
        {
            Console.WriteLine($"\n=== ВАЛЮТНЫЙ БОТ: ХОЛОДНЫЙ АНАЛИЗ УЗЛОВ СИСТЕМЫ (C#) ===");
            Console.WriteLine($"Загрузка {candleCount} исторических свечей из кэша...");
            var candles = await HistoricalDataLoader.LoadAsync(candleCount, "1min", forceRefresh: false);
            if (candles.Length < 100) return;
            Console.WriteLine($"Успешно загружено: {candles.Length} свечей EUR/USD.");

            var taEngine = new TechnicalAnalysisEngine();

            int horizon = 5;

            int taWins = 0, taLosses = 0;
            int taStrongWins = 0, taStrongLosses = 0;
            
            int smcWins = 0, smcLosses = 0;
            int smcSweepWins = 0, smcSweepLosses = 0;

            int ofWins = 0, ofLosses = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 200; i < candles.Length - horizon; i++)
            {
                var slice = candles.AsSpan(0, i + 1);
                var currentPrice = candles[i].Close;
                var futurePrice = candles[i + horizon].Close;
                bool actualUp = futurePrice > currentPrice;

                // 1. Math TA (нужно передавать последние 60 свечей)
                double[] closes = new double[60], vols = new double[60];
                int start = Math.Max(0, i - 59);
                for (int j = 0; j < 60 && start + j <= i; j++)
                {
                    closes[j] = candles[start + j].Close;
                    vols[j] = candles[start + j].Volume;
                }
                
                var taScoreResult = taEngine.ScoreTimeframe("EUR/USD OTC", "1m", closes, vols, slice);
                double taScore = taScoreResult.score;
                
                // TA Any signal
                if (Math.Abs(taScore) > 0.1)
                {
                    bool isBuy = taScore > 0;
                    if (isBuy == actualUp) taWins++; else taLosses++;
                }
                
                // TA Strong signal
                if (Math.Abs(taScore) > 0.7)
                {
                    bool isBuy = taScore > 0;
                    if (isBuy == actualUp) taStrongWins++; else taStrongLosses++;
                }

                // 2. SMC
                var smcResult = SmcEngine.AnalyzeSmcStructure("EUR/USD OTC", "1m", slice, currentPrice);
                string smcBos = smcResult.BosDirection;
                string smcSweep = smcResult.SweepDirection;
                
                if (smcBos == "BULLISH" || smcBos == "BEARISH")
                {
                    bool isBuy = smcBos == "BULLISH";
                    if (isBuy == actualUp) smcWins++; else smcLosses++;
                }
                
                if (smcSweep == "BULLISH_SWEEP" || smcSweep == "BEARISH_SWEEP")
                {
                    bool isBuy = smcSweep == "BULLISH_SWEEP";
                    if (isBuy == actualUp) smcSweepWins++; else smcSweepLosses++;
                }

                // 3. Order Flow
                var ofResult = OrderFlowEngine.AnalyzeOrderFlow("EUR/USD OTC", "1m", slice, currentPrice);
                var ofState = ofResult.OrderFlowState;
                if (ofState != null && (ofState.Contains("BULLISH") || ofState.Contains("BEARISH")))
                {
                    bool isBuy = ofState.Contains("BULLISH");
                    if (isBuy == actualUp) ofWins++; else ofLosses++;
                }
            }

            Console.WriteLine($"\nАнализ завершен за {sw.ElapsedMilliseconds}мс.");
            Console.WriteLine("---------------------------------------------------------");
            Console.WriteLine(" РЕЗУЛЬТАТЫ ИЗОЛИРОВАННЫХ УЗЛОВ (WIN RATE на 5 минутах):");
            Console.WriteLine("---------------------------------------------------------");
            
            double taWr = taWins * 100.0 / Math.Max(1, taWins + taLosses);
            double taStrongWr = taStrongWins * 100.0 / Math.Max(1, taStrongWins + taStrongLosses);
            Console.WriteLine($"1. Узел МАТЕМАТИКИ И ТА (Skender):");
            Console.WriteLine($"   - Обычные сигналы (>0.1): {taWr:F2}%  (Сделок: {taWins+taLosses})");
            Console.WriteLine($"   - Сильные сигналы (>0.7): {taStrongWr:F2}%  (Сделок: {taStrongWins+taStrongLosses})");
            
            double smcWr = smcWins * 100.0 / Math.Max(1, smcWins + smcLosses);
            double sweepWr = smcSweepWins * 100.0 / Math.Max(1, smcSweepWins + smcSweepLosses);
            Console.WriteLine($"\n2. Узел SMART MONEY CONCEPTS (SMC):");
            Console.WriteLine($"   - Break of Structure (Тренд): {smcWr:F2}%  (Сделок: {smcWins+smcLosses})");
            Console.WriteLine($"   - Liquidity Sweep (Разворот): {sweepWr:F2}%  (Сделок: {smcSweepWins+smcSweepLosses})");
            
            double ofWr = ofWins * 100.0 / Math.Max(1, ofWins + ofLosses);
            Console.WriteLine($"\n3. Узел ORDER FLOW (Микроструктура объемов):");
            Console.WriteLine($"   - Вливания объемов: {ofWr:F2}%  (Сделок: {ofWins+ofLosses})");
            Console.WriteLine("---------------------------------------------------------\n");
        }
    }
}
