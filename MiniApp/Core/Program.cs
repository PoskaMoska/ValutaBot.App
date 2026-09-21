using System.Text.Json;

namespace ValutaBot.MiniApp;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--backtest")
        {
#if DEBUG
            ValutaBot.App.MiniApp.Backtesting.BacktestEntryPoint.RunAsync(args).GetAwaiter().GetResult();
#else
            Console.WriteLine("[Backtest] Not available in production build. Use Debug configuration.");
#endif
            return;
        }

        if (args.Length >= 2 && args[0] == "--replay-test")
        {
            string corpusPath = args[1];
            bool success = ValutaBot.MiniApp.Core.ReplayTestEngine.RunAllAsync(corpusPath).GetAwaiter().GetResult();
            Environment.Exit(success ? 0 : 1);
            return;
        }

        if (args.Length >= 4 && args[0] == "--export-csv")
        {
            // Usage: dotnet run -- --export-csv EUR/USD 1m output.csv
            string symbol = args[1];
            string interval = args[2];
            string outPath = args[3];
            ValutaBot.MiniApp.Core.ReplayTestEngine.ExportCsvAsync(symbol, interval, outPath).GetAwaiter().GetResult();
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
}
