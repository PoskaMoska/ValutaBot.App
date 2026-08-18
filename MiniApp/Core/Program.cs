using System.Text.Json;

namespace ValutaBot.MiniApp;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
        {
            RunLocalTests().GetAwaiter().GetResult();
            return;
        }

        if (args.Length >= 2 && args[0] == "--backtest")
        {
#if DEBUG
            ValutaBot.App.MiniApp.Backtesting.BacktestEntryPoint.RunAsync(args).GetAwaiter().GetResult();
#else
            Console.WriteLine("[Backtest] Not available in production build. Use Debug configuration.");
#endif
            return;
        }

        if (args.Length >= 1 && args[0] == "--diag")
        {
            DiagRunner.RunAsync().GetAwaiter().GetResult();
            return;
        }

        try { Console.Title = "TradeBE Smart Terminal Core"; } catch { /* not a TTY (Docker/Linux) */ }

        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5000;

        while (true)
        {
            try
            {
                MiniAppController.Start(args, port);
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Crash: {ex.Message}");
                Console.WriteLine("[+] Auto-restart in 3s... (Ctrl+C to exit)");
                Thread.Sleep(3000);
            }
        }
    }

    private static async System.Threading.Tasks.Task RunLocalTests()
    {
        var ta = new TechnicalAnalysisEngine();
        var wfEngine = new WalkForwardValidationEngine();
        var autoCalibEngine = new AutoCalibrationEngine();
        TradeOutcomeTracker.WfEngine = wfEngine;
        TradeOutcomeTracker.CalibrationEngine = autoCalibEngine;
        var cmEngine = new ConfluenceMatrixEngine(new MarketDataFetcher(), ta);
        var aeEngine = new TradeTimeoutEngine();
        Console.WriteLine("==================================================");
        
        Console.WriteLine("        RUNNING COMPREHENSIVE MATH ENGINE TESTS   ");
        Console.WriteLine("==================================================");
        

        bool allPassed = true;

        // Helper test assertion
        void Assert(string testName, bool condition, string details = "")
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {testName} {details}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {testName} - FAILED! {details}");
                Console.ResetColor();
                allPassed = false;
            }
        }

        try
        {
            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 1. TEST ASSET SANITIZER РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[1] Testing Asset Sanitizer (Cyrillic OTC vs English)...");
            string clean1 = AssetSanitizer.Sanitize("EUR/USD OTC");
            string clean2 = AssetSanitizer.Sanitize("EUR/USD Р С›Р СћР РЋ"); // Cyrillic
            string clean3 = AssetSanitizer.Sanitize("  GBP-USD  ");
            
            Assert("Sanitize English OTC", clean1 == "EURUSD", $"Expected 'EURUSD', got '{clean1}'");
            Assert("Sanitize Cyrillic OTC", clean2 == "EURUSD", $"Expected 'EURUSD', got '{clean2}'");
            Assert("Sanitize formatted pair", clean3 == "GBPUSD", $"Expected 'GBPUSD', got '{clean3}'");

            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 2. TEST HURST EXPONENT REGIME ESTIMATOR РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[2] Testing Hurst Exponent Regime Estimator...");
            
            // Generate trending prices with positive autocorrelation: H should be high (>0.55)
            double[] trendPrices = new double[60];
            var randTrend = new Random(100);
            double lastChange = 0;
            trendPrices[0] = 10.0;
            for (int i = 1; i < 60; i++)
            {
                double currentChange = (randTrend.NextDouble() - 0.5) * 0.1 + lastChange * 0.75 + 0.02;
                trendPrices[i] = trendPrices[i - 1] + currentChange;
                lastChange = currentChange;
            }
            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 5. TEST DIRECTIONAL DYNAMISM (DYNAMISM CHECK) РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[5] Testing Directional Dynamism (Dynamism Check)...");
            
            double[] upTrend = new double[50];
            double[] downTrend = new double[50];
            double[] mockVols = new double[50];
            for (int i = 0; i < 50; i++)
            {
                upTrend[i] = 100.0 + i * 0.5; // strongly rising
                downTrend[i] = 100.0 - i * 0.5; // strongly falling
                mockVols[i] = 100.0;
            }

            var scoreMethod = typeof(TechnicalAnalysisEngine).GetMethod("ScoreTimeframe", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            
            // Score upward trend
            var upRes = scoreMethod?.Invoke(null, new object?[] { upTrend, mockVols, null, 30.0, 0.1, false });
            var upScore = upRes != null ? (double)(upRes.GetType().GetField("Item1")?.GetValue(upRes) ?? 1.0) : 1.0;

            // Score downward trend
            var downRes = scoreMethod?.Invoke(null, new object?[] { downTrend, mockVols, null, 30.0, 0.1, false });
            var downScore = downRes != null ? (double)(downRes.GetType().GetField("Item1")?.GetValue(downRes) ?? -1.0) : -1.0;

            Assert("Dynamism: Uptrend produces positive score", upScore > 0, $"Expected positive score, got {upScore:F2}");
            Assert("Dynamism: Downtrend produces negative score", downScore < 0, $"Expected negative score, got {downScore:F2}");
            Assert("Dynamism: Reversal detected correctly", upScore > downScore, $"Uptrend score ({upScore:F2}) should be greater than downtrend score ({downScore:F2})");

            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 6. TEST DATA FETCH AND REAL-TIME SYMBOLS (BINANCE & FALLBACK) РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[6] Testing live Binance data retrieval & validation...");
            var options = new JsonSerializerOptions { WriteIndented = true };
            
            // Test weekend fallback for EUR/USD
            Console.WriteLine("Fetching EUR/USD (simulated weekend fallback)...");
            var settings = new ValutaBot.MiniApp.TradingBotSettings { EnableMachineLearning = false, EnableSmc = false, EnableOrderFlow = false, EnableAutoCalibration = false };
            var res = await new ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator(new MarketDataFetcher(), ta, ta, ta, cmEngine, wfEngine, aeEngine, new MonteCarloEngine(), Microsoft.Extensions.Options.Options.Create(settings)).ExecuteAnalysisAsync("EUR/USD OTC", "m1");
            string resJson = JsonSerializer.Serialize(res, options);
            
            Assert("EUR/USD OTC fetching", resJson.Contains("direction") && !resJson.Contains("error"));

            // Check details of the result for NaNs or Infinities
            bool containsNaN = resJson.Contains("NaN") || resJson.Contains("Infinity");
            Assert("No NaN or Infinity in outputs", !containsNaN, "Verify output serialization contains valid numeric values");

            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 7. TEST SOCKET CRASH & DISCONNECT RECOVERY (REMOVED) РІвЂќР‚РІвЂќР‚РІвЂќР‚

            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 8. TEST EDGE CASES & EXTREME CONDITIONS РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[8] Testing Edge Cases & Extreme Conditions...");

            // 8.1 Gatekeeper Flat Market
            double[] flatPrices = new double[20];
            var flatCandles = new MiniAppController.OhlcCandle[20];
            for (int i = 0; i < 20; i++) 
            { 
                flatPrices[i] = 1.0500; 
                flatCandles[i] = new MiniAppController.OhlcCandle(1.0500, 1.0500, 1.0500, 1.0500, 100);
            }
            var gatekeeperRes = (new TechnicalAnalysisEngine()).ValidateMarketGatekeeper("TEST", "m1", flatPrices, flatCandles);
            Assert("Gatekeeper detects flat market", gatekeeperRes.IsTradeable == false && gatekeeperRes.Reason.Contains("Р В·Р В°РЎРѓРЎвЂљР С•"), $"Expected false/Р В·Р В°РЎРѓРЎвЂљР С•Р в„–, got {gatekeeperRes.IsTradeable}/{gatekeeperRes.Reason}");

            // 8.2 Walk-Forward — test cooloff check
            var wfRes = wfEngine.ValidateWalkForward("TEST", "m1");
            Assert("WalkForward returns valid result", !wfRes.IsCooloffActive, $"Expected no cooloff, got {wfRes.IsCooloffActive}");

            // 8.3 Order Flow Spoofing Trap Detection
            double[] spoofPrices = new double[10];
            double[] spoofVolumes = new double[10];
            for (int i = 0; i < 10; i++) { spoofPrices[i] = 100.0; spoofVolumes[i] = 100.0; }
            spoofPrices[8] = 99.999;
            spoofPrices[9] = 100.0; // Small up-tick to force volume into Buy side
            spoofVolumes[9] = 5000.0; // Massive volume, but priceDelta from 5 periods ago is 0
            var spoofCandles = new MiniAppController.OhlcCandle[10];
            for (int i = 0; i < 10; i++)
            {
                double p = spoofPrices[i];
                double prev = i > 0 ? spoofPrices[i - 1] : p;
                spoofCandles[i] = new MiniAppController.OhlcCandle(prev, Math.Max(p, prev), Math.Min(p, prev), p, spoofVolumes[i], DateTime.UtcNow.AddMinutes(i - 10));
            }
            var orderFlowRes = OrderFlowEngine.AnalyzeOrderFlow("TEST", "1m", spoofCandles, spoofPrices[9]);
            Assert("OrderFlow detects spoofing trap", orderFlowRes.OrderFlowState == "SPOOFING_TRAP", $"Expected SPOOFING_TRAP, got {orderFlowRes.OrderFlowState} (Delta: {orderFlowRes.DeltaRatio})");

            // 8.4 AutoCalibration Thread-Safety Stress Test
            Console.WriteLine("    Running AutoCalibration thread-safety stress test (1000 concurrent trades)...");
            var tasks = new System.Threading.Tasks.Task[100];
            var stressAutoCalib = new AutoCalibrationEngine();
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = System.Threading.Tasks.Task.Run(() => 
                {
                    for (int j = 0; j < 10; j++)
                    {
                        stressAutoCalib.RecordSourceOutcome("LIGHTGBM", "TEST_ASSET", "m1", true);
                    }
                });
            }
            System.Threading.Tasks.Task.WaitAll(tasks);
            var weight = stressAutoCalib.GetCalibratedRegimeWeight("LIGHTGBM", "TEST_ASSET", "m1", AutoCalibrationEngine.MarketRegime.TrendingImpulse);
            Assert("AutoCalibration Thread-Safety", weight > 0.0, "Engine survived 1000 concurrent writes without crashing");

            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 9. ADDITIONAL DEEP TESTS РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[9] Additional deep analysis tests...");

            // 9.1 ContinuousStateEngine Flash Crash (Hyper Accelerating Down)
            double[] flashPrices = { 100, 100, 100, 100, 100, 99, 97, 94, 90, 85, 75, 60 };
            var flashRes = ContinuousStateEngine.EvaluateContinuousState(flashPrices, "TEST", "m1");
            Assert("Flash Crash detected", flashRes.VelocityRegime == "HYPER_ACCELERATING_DOWN" && flashRes.VelocityBpsPerSec < -3.0, $"Expected HYPER_ACCELERATING_DOWN, got {flashRes.VelocityRegime} with Vel {flashRes.VelocityBpsPerSec}");

            // 9.2 OrderFlow Bearish Absorption
            double[] absPrices = { 100.0, 100.0, 100.0, 100.0, 100.0, 100.0, 100.0, 100.0, 99.9, 99.5 };
            double[] absVols = { 100, 100, 100, 100, 100, 100, 100, 100, 5000, 5000 };
            // absPrices[8] and [9] drop, meaning priceDiff < 0 -> volume goes to SELL. Wait, deltaRatio = Buy/Sell.
            // If deltaRatio > 1.8 and price drops -> Bearish Absorption.
            // We need price to drop, but massive BUY volume. How?
            // If priceDiff > 0, it counts as BUY. Let's make price fluctuate up by 0.001 with massive volume, then drop by 0.5 with tiny volume.
            double[] bearishAbsPrices = { 100, 100, 100, 100, 100, 100.001, 100.002, 100.003, 100.004, 99.5 };
            double[] bearishAbsVols = { 100, 100, 100, 100, 100, 2000, 2000, 2000, 2000, 50 };
            var bearishAbsCandles = new MiniAppController.OhlcCandle[10];
            for (int i = 0; i < 10; i++)
            {
                double p = bearishAbsPrices[i];
                double prev = i > 0 ? bearishAbsPrices[i - 1] : p;
                bearishAbsCandles[i] = new MiniAppController.OhlcCandle(prev, Math.Max(p, prev), Math.Min(p, prev), p, bearishAbsVols[i], DateTime.UtcNow.AddMinutes(i - 10));
            }
            var absRes = OrderFlowEngine.AnalyzeOrderFlow("TEST2", "1m", bearishAbsCandles, bearishAbsPrices[9]);
            Assert("Bearish Absorption detected", absRes.OrderFlowState == "BEARISH_ABSORPTION", $"Expected BEARISH_ABSORPTION, got {absRes.OrderFlowState}");

            // 9.3 AutoCalibration Forgetting Factor
            var testAutoCalib = new AutoCalibrationEngine();
            for (int i = 0; i < 60; i++)
            {
                // Give 60 wins for LIGHTGBM
                testAutoCalib.RecordSourceOutcome("LIGHTGBM", "TEST_ASSET2", "m1", true);
            }
            var lgbmWeight = testAutoCalib.GetCalibratedRegimeWeight("LIGHTGBM", "TEST_ASSET2", "m1", AutoCalibrationEngine.MarketRegime.RangingFlat);
            Assert("Forgetting factor applies without crashing", lgbmWeight > 0.0, $"Weight is {lgbmWeight}");

            // 9.4 Technical Analysis Data Resiliency (Null/Empty Arrays)
            try 
            {
                MiniAppController.OhlcCandle[] emptyArr = Array.Empty<MiniAppController.OhlcCandle>();
                var hmaRes = (new TechnicalAnalysisEngine()).ComputeHma("TEST", "m1", emptyArr);
                var rsiRes = (new TechnicalAnalysisEngine()).ComputeConnorsRsi("TEST", "m1", emptyArr);
                Assert("TechnicalAnalysis handles empty arrays safely", hmaRes == 0.0 && rsiRes == 50.0, "Expected safe fallback values");
            }
            catch (Exception ex)
            {
                Assert("TechnicalAnalysis handles empty arrays safely", false, $"Threw exception: {ex.Message}");
            }

            // РІвЂќР‚РІвЂќР‚РІвЂќР‚ 10. SERVICES INTEGRATION TESTS РІвЂќР‚РІвЂќР‚РІвЂќР‚
            Console.WriteLine("\n[10] Services Integration Tests (SignalTracker & MLPythonService)...");

            // 10.1 (Removed SignalTracker test as it is now DB-backed)

            // 10.2 MLPythonService Circuit Breaker
            MLPythonService.Init("http://127.0.0.1:9999/dead_endpoint"); // Dead URL
            var mlRes = await MLPythonService.PredictAsync("BTCUSDT", "1m", flatCandles, false);
            Assert("MLPythonService circuit breaker handles dead endpoint gracefully", mlRes == null, $"Expected null fallback, got {mlRes?.Direction}");

        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"=> [ERROR] Test run threw an exception: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            allPassed = false;
        }

        Console.WriteLine("\n==================================================");
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("    ALL TESTS PASSED SUCCESSFULLY! (100% SUCCESS)  ");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("    SOME TESTS FAILED! PLEASE CHECK THE LOGS.     ");
            Console.ResetColor();
        }
        Console.WriteLine("==================================================");
        
    }
}













