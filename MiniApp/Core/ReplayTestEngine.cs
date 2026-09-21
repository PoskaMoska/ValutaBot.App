using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ValutaBot.MiniApp.Core
{
    public static class ReplayTestEngine
    {
        public class ExpectedResult
        {
            public string direction { get; set; } = "";
            public string? smcDirection { get; set; }
            public string? taDirection { get; set; }
            public string? ofDirection { get; set; }
            public string? lgbmDirection { get; set; }
        }

        private class FileMarketDataFetcher : MarketDataFetcher
        {
            private readonly MiniAppController.OhlcCandle[] _candles;

            public FileMarketDataFetcher(string csvPath)
            {
                var lines = File.ReadAllLines(csvPath).Skip(1).ToArray(); // skip header
                var list = new List<MiniAppController.OhlcCandle>();
                foreach(var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    // Expected CSV format: Time(str), Open, High, Low, Close, Volume
                    var ts = DateTime.Parse(parts[0]);
                    double o = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    double h = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    double l = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                    double c = double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
                    double v = double.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture);
                    list.Add(new MiniAppController.OhlcCandle(o, h, l, c, v, ts));
                }
                _candles = list.ToArray();
            }

            public override Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
            {
                return Task.FromResult(_candles.TakeLast(limit).ToArray());
            }
        }

        public static async Task<bool> RunAllAsync(string corpusPath)
        {
            if (!Directory.Exists(corpusPath))
            {
                Console.WriteLine($"[ReplayEngine] Corpus directory not found: {corpusPath}");
                return false;
            }

            var testDirs = Directory.GetDirectories(corpusPath);
            if (testDirs.Length == 0)
            {
                Console.WriteLine($"[ReplayEngine] No test folders found in {corpusPath}");
                return true;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine($"[ReplayEngine] Starting Replay Tests in {corpusPath}");
            Console.WriteLine("==================================================");

            bool allPassed = true;

            var ta = new TechnicalAnalysisEngine();
            var timeoutEngine = new TradeTimeoutEngine();
            var e2eSettings = new ValutaBot.MiniApp.TradingBotSettings { EnableMachineLearning = false, EnableSmc = true, EnableOrderFlow = true, EnableAutoCalibration = true };

            foreach (var testDir in testDirs)
            {
                string testName = Path.GetFileName(testDir);
                string csvPath = Path.Combine(testDir, "input.csv");
                string expectedPath = Path.Combine(testDir, "expected.json");

                if (!File.Exists(csvPath) || !File.Exists(expectedPath))
                {
                    Console.WriteLine($"[SKIP] {testName} - Missing input.csv or expected.json");
                    continue;
                }

                try
                {
                    var expectedJson = File.ReadAllText(expectedPath);
                    var expected = JsonSerializer.Deserialize<ExpectedResult>(expectedJson);

                    var fetcher = new FileMarketDataFetcher(csvPath);
                    var cmEngine = new ConfluenceMatrixEngine(fetcher, ta, new ValutaBot.MiniApp.AutoCalibrationEngine());
                    var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());
                    var orchestrator = new ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator(
                        fetcher, ta, ta, ta, cmEngine, timeoutEngine, new MonteCarloEngine(), Microsoft.Extensions.Options.Options.Create(e2eSettings),
                        new Microsoft.Extensions.Logging.Abstractions.NullLogger<ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator>()
                    );

                    var resultRaw = await orchestrator.ExecuteAnalysisAsync(testName, "m1");
                    var actualJson = JsonSerializer.Serialize(resultRaw);
                    var actual = JsonSerializer.Deserialize<ValutaBot.App.MiniApp.Models.AnalysisResponseDto>(actualJson);

                    bool pass = true;
                    var errors = new List<string>();

                    if (actual!.direction != expected!.direction)
                    {
                        pass = false; errors.Add($"Final direction mismatch: Expected '{expected.direction}', got '{actual.direction}'");
                    }
                    if (expected.smcDirection != null && actual.smcDirection != expected.smcDirection)
                    {
                        pass = false; errors.Add($"SMC direction mismatch: Expected '{expected.smcDirection}', got '{actual.smcDirection}'");
                    }
                    if (expected.taDirection != null && actual.taDirection != expected.taDirection)
                    {
                        pass = false; errors.Add($"TA direction mismatch: Expected '{expected.taDirection}', got '{actual.taDirection}'");
                    }
                    if (expected.ofDirection != null && actual.ofDirection != expected.ofDirection)
                    {
                        pass = false; errors.Add($"OrderFlow direction mismatch: Expected '{expected.ofDirection}', got '{actual.ofDirection}'");
                    }
                    if (expected.lgbmDirection != null && actual.lgbmDirection != expected.lgbmDirection)
                    {
                        pass = false; errors.Add($"ML direction mismatch: Expected '{expected.lgbmDirection}', got '{actual.lgbmDirection}'");
                    }

                    if (pass)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[PASS] {testName} -> All expected module consensus signals matched.");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[FAIL] {testName}");
                        foreach (var err in errors)
                        {
                            Console.WriteLine($"  - {err}");
                        }
                        Console.ResetColor();
                        allPassed = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] {testName} -> Exception during replay: {ex.Message}");
                    Console.ResetColor();
                    allPassed = false;
                }
            }

            Console.WriteLine("==================================================");
            if (allPassed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("ALL REPLAY TESTS PASSED!");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("SOME REPLAY TESTS FAILED! DEPLOYMENT BLOCKED.");
                Console.ResetColor();
            }

            return allPassed;
        }

        public static async Task ExportCsvAsync(string symbol, string interval, string outPath)
        {
            using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
            var rows = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, @"
                SELECT open_time, open, high, low, close, volume
                FROM historical_candles
                WHERE asset = @Asset AND interval = @Interval
                ORDER BY open_time ASC
            ", new { Asset = symbol, Interval = interval });

            var list = rows.ToList();
            if (list.Count == 0)
            {
                Console.WriteLine($"[Export] No data found for {symbol} {interval}");
                return;
            }

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var writer = new StreamWriter(outPath);
            writer.WriteLine("Time,Open,High,Low,Close,Volume");
            foreach (var r in list)
            {
                DateTime t = r.open_time;
                writer.WriteLine($"{t:O},{r.open.ToString(System.Globalization.CultureInfo.InvariantCulture)},{r.high.ToString(System.Globalization.CultureInfo.InvariantCulture)},{r.low.ToString(System.Globalization.CultureInfo.InvariantCulture)},{r.close.ToString(System.Globalization.CultureInfo.InvariantCulture)},{r.volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            Console.WriteLine($"[Export] Successfully wrote {list.Count} candles to {outPath}");
        }
    }
}
