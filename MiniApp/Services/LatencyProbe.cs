using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// Динамический измеритель задержки (RTT) до сервера рыночных данных.
///
/// Периодически пингует TwelveData API и сохраняет скользящее среднее RTT.
/// Результат используется фронтендом для компенсации сетевой задержки при
/// открытии опционной сделки (Pre-execution latency compensation).
///
/// Формула упреждения: SendAt = CandleCloseTime - measured_rtt_ms - JITTER_BUFFER_MS
/// </summary>
public static class LatencyProbe
{
    // Минимальный фиксированный буфер для компенсации джиттера и обработки на сервере брокера.
    private const int JitterBufferMs = 150;

    // Максимальное допустимое упреждение (не более 2.5 секунд).
    private const int MaxOffsetMs = 2500;

    // Минимальное упреждение (не менее 200ms даже при нулевом RTT).
    private const int MinOffsetMs = 200;

    // Количество последних замеров для скользящего среднего.
    private const int SampleWindow = 8;

    private static readonly double[] _samples = new double[SampleWindow];
    private static int _sampleIndex = 0;
    private static int _sampleCount = 0;
    private static readonly object _lock = new();

    // Последний измеренный RTT в миллисекундах.
    public static double LastRttMs { get; private set; } = 100.0;

    // Вычисленное упреждение в миллисекундах (RTT + JitterBuffer, зажатое в [Min, Max]).
    public static int SendAtOffsetMs
    {
        get
        {
            int offset = (int)Math.Round(LastRttMs) + JitterBufferMs;
            return Math.Clamp(offset, MinOffsetMs, MaxOffsetMs);
        }
    }

    /// <summary>
    /// Запускает фоновый цикл измерения RTT каждые 30 секунд.
    /// Вызывается единожды при старте приложения.
    /// </summary>
    public static void StartBackground(IHttpClientFactory? factory, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            // Первый замер — сразу при старте.
            await MeasureAsync(factory);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(ct);
                    await MeasureAsync(factory);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    BotLogger.Warn($"[LatencyProbe] Measurement error: {ex.Message}");
                }
            }
        }, ct);
    }

    /// <summary>
    /// Выполняет единичный замер RTT до TwelveData API.
    /// Использует среднее из 3 последовательных запросов для стабилизации результата.
    ///
    /// FIX 2 (2026-09-13): Replaced Binance stub (always 200ms hardcoded) with a real HTTP
    /// probe to https://api.twelvedata.com (root — no API key required, no quota consumed).
    /// On failure: keeps the last known RTT instead of silently resetting to 200ms,
    /// so SendAtOffsetMs stays meaningful even during temporary network blips.
    /// </summary>
    public static async Task MeasureAsync(IHttpClientFactory? factory)
    {
        const string PingTarget = "https://api.twelvedata.com"; // root — lightweight, no auth needed
        const int    Attempts   = 3;
        const int    TimeoutMs  = 2000;

        double totalMs  = 0;
        int    succeeded = 0;

        for (int i = 0; i < Attempts; i++)
        {
            try
            {
                HttpClient client = factory != null
                    ? factory.CreateClient("LatencyProbe")
                    : new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await client.GetAsync(new Uri(PingTarget));
                sw.Stop();

                totalMs += sw.Elapsed.TotalMilliseconds;
                succeeded++;
            }
            catch
            {
                // Network blip — this attempt doesn't contribute to the average.
            }
        }

        if (succeeded > 0)
        {
            double measured = totalMs / succeeded;
            AddSample(measured);
            BotLogger.Info($"[LatencyProbe] RTT to TwelveData: {measured:F0}ms (avg of {succeeded}/{Attempts} probes). SendAtOffset: {SendAtOffsetMs}ms");
        }
        else
        {
            // All attempts failed: keep the last known RTT (never reset to a fake value).
            BotLogger.Warn($"[LatencyProbe] All {Attempts} pings failed. Keeping last RTT: {LastRttMs:F0}ms.");
        }
    }

    /// <summary>
    /// Добавляет новый замер в скользящее окно и пересчитывает среднее.
    /// Использует ring buffer для O(1) операции без аллокаций.
    /// </summary>
    private static void AddSample(double rttMs)
    {
        lock (_lock)
        {
            _samples[_sampleIndex] = rttMs;
            _sampleIndex = (_sampleIndex + 1) % SampleWindow;
            if (_sampleCount < SampleWindow) _sampleCount++;

            double sum = 0;
            for (int i = 0; i < _sampleCount; i++) sum += _samples[i];
            LastRttMs = sum / _sampleCount;
        }
    }
}
