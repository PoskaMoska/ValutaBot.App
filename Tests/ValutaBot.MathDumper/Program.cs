using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using ValutaBot.MiniApp;

namespace ValutaBot.MathDumper;

class Program
{
    static async Task Main(string[] args)
    {
        // Run deep verification fuzz tests
        Fuzzer.RunFuzzTests();

        string symbol = "EURUSDT";
        string interval = "1m";
        int limit = 1000;
        int targetHorizon = 5; // Look ahead 5 candles

        var candles = await FetchKlinsAsync(symbol, interval, limit);
        if (candles == null || candles.Length == 0) return;

        var engine = new TechnicalAnalysisEngine();
        
        int totalSweepSignals = 0;
        int successfulSweepSignals = 0;
        
        int totalBosSignals = 0;
        int successfulBosSignals = 0;

        bool wasBlockTrade = false;
        bool wasSweep = false;

        Console.WriteLine($"=== STARTING TRANSIENT STATE SIMULATION (Levels 1 & 2) ===");
        
        for (int i = 50; i < candles.Length - targetHorizon; i++)
        {
            var span = candles.AsSpan(0, i + 1);
            var currentCandle = span[^1];
            double currentPrice = currentCandle.Close;

            var smc = engine.GetSmcState(symbol, interval, span, currentPrice);
            var orderFlow = OrderFlowEngine.AnalyzeOrderFlow(symbol, interval, span, currentPrice);
            
            bool currentBlockTrade = orderFlow.IsInstitutionalBlockTrade;
            bool currentSweep = smc.HasLiquiditySweep;

            if (currentBlockTrade && !wasBlockTrade)
                Console.WriteLine($"[{currentCandle.Timestamp:HH:mm:ss}] [OrderFlow] Block Trade TRIGGERED");
            else if (!currentBlockTrade && wasBlockTrade)
                Console.WriteLine($"[{currentCandle.Timestamp:HH:mm:ss}] [OrderFlow] Block Trade RESET");
            else if (currentBlockTrade && wasBlockTrade)
                Console.WriteLine($"[{currentCandle.Timestamp:HH:mm:ss}] [OrderFlow] Block Trade LEAKED");

            if (currentSweep && !wasSweep)
                Console.WriteLine($"[{currentCandle.Timestamp:HH:mm:ss}] [SMC] Liquidity Sweep TRIGGERED");
            else if (!currentSweep && wasSweep)
                Console.WriteLine($"[{currentCandle.Timestamp:HH:mm:ss}] [SMC] Liquidity Sweep RESET");
            else if (currentSweep && wasSweep)
                Console.WriteLine($"[{currentCandle.Timestamp:HH:mm:ss}] [SMC] Liquidity Sweep LEAKED");

            wasBlockTrade = currentBlockTrade;
            wasSweep = currentSweep;

            double futurePrice = candles[i + targetHorizon].Close;

            if (currentSweep)
            {
                totalSweepSignals++;
                bool isBullishSweep = smc.SweepDirection == "BULLISH_SWEEP";
                
                if (isBullishSweep && futurePrice > currentPrice) successfulSweepSignals++;
                else if (!isBullishSweep && futurePrice < currentPrice) successfulSweepSignals++;
            }

            if (smc.HasBos)
            {
                totalBosSignals++;
                bool isBullishBos = smc.BosDirection == "BULLISH_BOS";
                
                if (isBullishBos && futurePrice > currentPrice) successfulBosSignals++;
                else if (!isBullishBos && futurePrice < currentPrice) successfulBosSignals++;
            }
        }

        Console.WriteLine($"\n=== PURE MATH ENGINE PERFORMANCE (Horizon: {targetHorizon} candles) ===");
        
        if (totalSweepSignals > 0) {
            double sweepWinrate = (double)successfulSweepSignals / totalSweepSignals * 100;
            Console.WriteLine($"SMC Liquidity Sweeps: {successfulSweepSignals} correct out of {totalSweepSignals} ({sweepWinrate:F1}% Winrate)");
        }
        
        if (totalBosSignals > 0) {
            double bosWinrate = (double)successfulBosSignals / totalBosSignals * 100;
            Console.WriteLine($"SMC Break of Structure (BOS): {successfulBosSignals} correct out of {totalBosSignals} ({bosWinrate:F1}% Winrate)");
        }
        
        Console.WriteLine($"\n=== LEVEL 3: ADAPTIVE REGIME SWITCHING TEST ===");
        
        int flatRegimeCount = 0;
        int trendRegimeCount = 0;
        double avgScoreFlat = 0;
        double avgScoreTrend = 0;

        for (int i = 50; i < candles.Length - targetHorizon; i++)
        {
            var span = candles.AsSpan(0, i + 1);
            var prices = new double[span.Length];
            var volumes = new double[span.Length];
            for (int j = 0; j < span.Length; j++) 
            {
                prices[j] = span[j].Close;
                volumes[j] = span[j].Volume;
            }

            var (adx, _, _) = engine.ComputeTrueAdx(symbol, interval, span);
            var (score, conf, rsi, hma, volStr, atr) = engine.ScoreTimeframe(symbol, interval, prices, volumes, span);

            if (adx < 20.0)
            {
                flatRegimeCount++;
                avgScoreFlat += Math.Abs(score);
            }
            else if (adx > 25.0)
            {
                trendRegimeCount++;
                avgScoreTrend += Math.Abs(score);
            }
        }

        if (flatRegimeCount > 0) avgScoreFlat /= flatRegimeCount;
        if (trendRegimeCount > 0) avgScoreTrend /= trendRegimeCount;

        Console.WriteLine($"Flat Regime (ADX < 20) Detected: {flatRegimeCount} times. Avg absolute TA score: {avgScoreFlat:F4}");
        Console.WriteLine($"Trend Regime (ADX > 25) Detected: {trendRegimeCount} times. Avg absolute TA score: {avgScoreTrend:F4}");
        Console.WriteLine($"=== SIMULATION COMPLETE ===");
    }

    private static async Task<MiniAppController.OhlcCandle[]> FetchKlinsAsync(string symbol, string interval, int limit)
    {
        string url = $"https://api.binance.com/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
        using var client = new HttpClient();
        var response = await client.GetAsync(url);
        var jsonStr = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(jsonStr);
        var arr = doc.RootElement.EnumerateArray().ToArray();
        
        var result = new MiniAppController.OhlcCandle[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var k = arr[i];
            long timeMs = k[0].GetInt64();
            double open = double.Parse(k[1].GetString() ?? "0", CultureInfo.InvariantCulture);
            double high = double.Parse(k[2].GetString() ?? "0", CultureInfo.InvariantCulture);
            double low = double.Parse(k[3].GetString() ?? "0", CultureInfo.InvariantCulture);
            double close = double.Parse(k[4].GetString() ?? "0", CultureInfo.InvariantCulture);
            double vol = double.Parse(k[5].GetString() ?? "0", CultureInfo.InvariantCulture);
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime;
            result[i] = new MiniAppController.OhlcCandle(open, high, low, close, vol, dt);
        }
        return result;
    }
}
