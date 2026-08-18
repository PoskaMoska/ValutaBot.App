using System;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Backtesting;

namespace ValutaBot.App.MiniApp.Backtesting
{
    /// <summary>
    /// Точка входа бектеста. Вызывается из Program.cs при наличии аргумента --backtest.
    ///
    /// Использование:
    ///   dotnet run -- --backtest m1 50000
    ///   dotnet run -- --backtest s5 10000
    ///   dotnet run -- --backtest m1 100000 --refresh
    /// </summary>
    public static class BacktestEntryPoint
    {
        public static async Task RunAsync(string[] args)
        {
            Console.WriteLine();
            Console.WriteLine("╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("║   ValutaBot Walk-Forward Backtest System          ║");
            Console.WriteLine("║   Self-Learning Validation — EUR/USD OTC          ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════╝");
            Console.WriteLine();

            // Init HTTP and ML
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddHttpClient();
            var sp = services.BuildServiceProvider();
            ValutaBot.MiniApp.MiniAppController.HttpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            ValutaBot.MiniApp.MLPythonService.Init("http://127.0.0.1:8765");
            System.Threading.Thread.Sleep(3000); // give it time to start

            // ── Парсинг аргументов ─────────────────────────────────────────
            string timeframe   = "m1";
            int    candleCount = 50_000;
            bool   forceRefresh = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--backtest":
                        if (i + 1 < args.Length) { timeframe = args[++i].ToLower(); }
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int n)) { candleCount = n; i++; }
                        break;
                    case "--refresh":
                        forceRefresh = true;
                        break;
                }
            }

            bool isS5         = timeframe is "s5" or "5s";
            string tdInterval = isS5 ? "1min" : "1min"; // S5 всегда базируется на M1
            int    m1Count    = isS5 ? (int)Math.Ceiling(candleCount / 12.0) : candleCount;

            Console.WriteLine($"  Режим:    {timeframe.ToUpper()}");
            Console.WriteLine($"  Свечей:   {candleCount}{(isS5 ? " (из ~" + m1Count + " M1 → синтез S5)" : "")}");
            Console.WriteLine($"  Кэш:      {(forceRefresh ? "принудительное обновление" : "использовать если есть")}");
            Console.WriteLine();

            // ── Загрузка данных ────────────────────────────────────────────
            var m1Candles = await HistoricalDataLoader.LoadAsync(
                totalCandles: m1Count,
                interval:     tdInterval,
                forceRefresh: forceRefresh);

            if (m1Candles.Length < 100)
            {
                Console.WriteLine("[BacktestEntryPoint] Недостаточно данных. Проверьте API ключ и соединение.");
                return;
            }

            Console.WriteLine($"[BacktestEntryPoint] Загружено M1 свечей: {m1Candles.Length}");

            // ── Синтез S5 если нужно ───────────────────────────────────────
            var candles = isS5
                ? S5CandleSynthesizer.SynthesizeFromM1(m1Candles)
                : m1Candles;

            if (isS5)
                Console.WriteLine($"[BacktestEntryPoint] S5-синтез: {m1Candles.Length} M1 → {candles.Length} S5 свечей");

            // ── Запуск бектеста ────────────────────────────────────────────
            await BacktestRunner.RunAsync(candles, timeframe, isS5);
        }
    }
}
