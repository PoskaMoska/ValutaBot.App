using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using ValutaBot.MiniApp;

public static class DiagRunner
{
    public static async Task RunAsync()
    {
        Console.WriteLine("Diagnostics:");
        
        string asset = "EUR/USD";
        string timeframe = "1m";
        
        Console.WriteLine($"[1/3] Fetching {asset} ({timeframe}) from Tiingo...");
        var sw = Stopwatch.StartNew();
        var data = await TiingoService.FetchCandlesAsync(asset, timeframe, 100, 0);
        sw.Stop();
        
        if (data == null || data.Value.candles.Length == 0)
        {
            Console.WriteLine("Error: failed to fetch candles.");
            return;
        }
        
        Console.WriteLine($"Tiingo Ping (Download Time): {sw.ElapsedMilliseconds} ms");
        
        var lastCandleTime = data.Value.candles.Last().Timestamp;
        var diff = DateTime.UtcNow - lastCandleTime;
        Console.WriteLine($"Staleness (UTC): {Math.Round(diff.TotalSeconds, 1)} sec (last candle: {lastCandleTime:HH:mm:ss})");
        
        Console.WriteLine($"[2/3] Init Python ML...");
        MLPythonService.Init("http://127.0.0.1:8765");
        await Task.Delay(2000);
        
        Console.WriteLine($"[3/3] Predicting...");
        var ohlcSpan = data.Value.candles;
        sw.Restart();
        var mlPred = await MLPythonService.PredictAsync(asset, timeframe, ohlcSpan, true);
        sw.Stop();
        
        if (mlPred != null)
        {
            Console.WriteLine($"ML response time: {sw.ElapsedMilliseconds} ms (Dir: {mlPred.Direction}, Conf: {mlPred.Confidence:F2})");
        }
        else
        {
            Console.WriteLine($"ML did not respond.");
        }
    }
}
