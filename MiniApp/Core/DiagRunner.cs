using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using ValutaBot.MiniApp;

public static class DiagRunner
{
    public static async Task RunAsync()
    {
        Console.WriteLine("📊 ДИАГНОСТИКА СИСТЕМЫ (ЛОКАЛЬНЫЙ ТЕСТ):");
        
        string asset = "EUR/USD";
        string timeframe = "1m";
        
        Console.WriteLine($"[1/3] Запрос котировок {asset} ({timeframe}) из TwelveData...");
        var sw = Stopwatch.StartNew();
        var data = await TwelveDataService.FetchCandlesAsync(asset, timeframe, 100, 0);
        sw.Stop();
        
        if (data == null || data.Value.candles.Length == 0)
        {
            Console.WriteLine("❌ Ошибка: Не удалось получить котировки.");
            return;
        }
        
        Console.WriteLine($"🌐 TwelveData Ping (Download Time): {sw.ElapsedMilliseconds} ms");
        
        var lastCandleTime = data.Value.candles.Last().Timestamp;
        var diff = DateTime.UtcNow - lastCandleTime;
        Console.WriteLine($"⏱ Отставание котировок (относительно UTC): {Math.Round(diff.TotalSeconds, 1)} сек (Последняя свеча: {lastCandleTime:HH:mm:ss})");
        
        Console.WriteLine($"[2/3] Запуск локального Python ML...");
        MLPythonService.Init("http://127.0.0.1:8765");
        await Task.Delay(2000); // Give it time to start
        
        Console.WriteLine($"[3/3] Запрос предсказания ИИ...");
        var ohlcSpan = data.Value.candles; // It's an array of OhlcCandle which PredictAsync expects
        sw.Restart();
        var mlPred = await MLPythonService.PredictAsync(asset, timeframe, ohlcSpan, true);
        sw.Stop();
        
        if (mlPred != null)
        {
            Console.WriteLine($"🧠 Скорость ответа ИИ: {sw.ElapsedMilliseconds} ms (Дирекция: {mlPred.Direction}, Уверенность: {mlPred.Confidence:F2})");
            Console.WriteLine($"🟢 Вердикт: Сетевая инфраструктура работает быстро.");
        }
        else
        {
            Console.WriteLine($"🔴 ИИ не отвечает (Локальный Python не готов).");
        }
    }
}
