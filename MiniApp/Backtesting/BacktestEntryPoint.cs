using System;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Backtesting;

namespace ValutaBot.App.MiniApp.Backtesting
{
    /// <summary>
    /// РўРѕС‡РєР° РІС…РѕРґР° Р±РµРєС‚РµСЃС‚Р°. Р’С‹Р·С‹РІР°РµС‚СЃСЏ РёР· Program.cs РїСЂРё РЅР°Р»РёС‡РёРё Р°СЂРіСѓРјРµРЅС‚Р° --backtest.
    ///
    /// РСЃРїРѕР»СЊР·РѕРІР°РЅРёРµ:
    ///   dotnet run -- --backtest m1 50000
    ///   dotnet run -- --backtest s5 10000
    ///   dotnet run -- --backtest m1 100000 --refresh
    /// </summary>
    public static class BacktestEntryPoint
    {
        public static async Task RunAsync(string[] args)
        {
            Console.WriteLine();
            Console.WriteLine("в•”в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•—");
            Console.WriteLine("в•‘   ValutaBot Walk-Forward Backtest System          в•‘");
            Console.WriteLine("в•‘   Self-Learning Validation вЂ” EUR/USD OTC          в•‘");
            Console.WriteLine("в•љв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ќ");
            Console.WriteLine();

            // Init HTTP and ML
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddHttpClient();
            var sp = services.BuildServiceProvider();
            ValutaBot.MiniApp.MiniAppController.HttpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            ValutaBot.MiniApp.MLPythonService.Init("http://127.0.0.1:8765");
            System.Threading.Thread.Sleep(3000); // give it time to start

            // в”Ђв”Ђ РџР°СЂСЃРёРЅРі Р°СЂРіСѓРјРµРЅС‚РѕРІ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
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
            string tdInterval = isS5 ? "1min" : "1min"; // S5 РІСЃРµРіРґР° Р±Р°Р·РёСЂСѓРµС‚СЃСЏ РЅР° M1
            int    m1Count    = isS5 ? (int)Math.Ceiling(candleCount / 12.0) : candleCount;

            Console.WriteLine($"  Р РµР¶РёРј:    {timeframe.ToUpper()}");
            Console.WriteLine($"  РЎРІРµС‡РµР№:   {candleCount}{(isS5 ? " (РёР· ~" + m1Count + " M1 в†’ СЃРёРЅС‚РµР· S5)" : "")}");
            Console.WriteLine($"  РљСЌС€:      {(forceRefresh ? "РїСЂРёРЅСѓРґРёС‚РµР»СЊРЅРѕРµ РѕР±РЅРѕРІР»РµРЅРёРµ" : "РёСЃРїРѕР»СЊР·РѕРІР°С‚СЊ РµСЃР»Рё РµСЃС‚СЊ")}");
            Console.WriteLine();

            // в”Ђв”Ђ Р—Р°РіСЂСѓР·РєР° РґР°РЅРЅС‹С… в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
            var m1Candles = await HistoricalDataLoader.LoadAsync(
                totalCandles: m1Count,
                interval:     tdInterval,
                forceRefresh: forceRefresh);

            if (m1Candles.Length < 100)
            {
                Console.WriteLine("[BacktestEntryPoint] РќРµРґРѕСЃС‚Р°С‚РѕС‡РЅРѕ РґР°РЅРЅС‹С…. РџСЂРѕРІРµСЂСЊС‚Рµ API РєР»СЋС‡ Рё СЃРѕРµРґРёРЅРµРЅРёРµ.");
                return;
            }

            Console.WriteLine($"[BacktestEntryPoint] Р—Р°РіСЂСѓР¶РµРЅРѕ M1 СЃРІРµС‡РµР№: {m1Candles.Length}");

            // в”Ђв”Ђ РЎРёРЅС‚РµР· S5 РµСЃР»Рё РЅСѓР¶РЅРѕ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
            var candles = isS5
                ? S5CandleSynthesizer.SynthesizeFromM1(m1Candles)
                : m1Candles;

            if (isS5)
                Console.WriteLine($"[BacktestEntryPoint] S5-СЃРёРЅС‚РµР·: {m1Candles.Length} M1 в†’ {candles.Length} S5 СЃРІРµС‡РµР№");

            // в”Ђв”Ђ Р—Р°РїСѓСЃРє Р±РµРєС‚РµСЃС‚Р° в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
            await CombinedColdAnalyzer.RunAsync(candleCount);
        }
    }
}


