using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// Динамический измеритель задержки (RTT) до сервера рыночных данных.
///
/// Периодически пингует API-эндпоинт Binance и сохраняет скользящее среднее RTT.
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

    // Количество последних замеров для скользящего среднего (EMA-сглаживание).
    private const int SampleWindow = 8;

    // Целевой эндпоинт для пинга — легковесный эндпоинт без данных.
    private const string PingUrl = "https://api.binance.com/api/v3/ping";

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
    /// Выполняет единичный замер RTT до Binance API.
    /// Использует среднее из 3 последовательных запросов для стабилизации результата.
    /// </summary>
        public static async Task MeasureAsync(IHttpClientFactory? factory)
    {
        // --- BINANCE DISABLED GLOBALLY ---
        // As per user request, Binance is disabled everywhere.
        // We will just simulate a fixed fake ping to keep math stable, 
        // without actually sending HTTP requests to Binance.
        await Task.Delay(1); // minimal async footprint
        AddSample(200.0);    // simulate 200ms RTT
    }

    /// <summary>
    /// Добавляет новый замер в скользящее окно и пересчитывает EMA-среднее.
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

