using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.Core;

namespace ValutaBot.MiniApp.Services;

/// <summary>
/// Passive monitor of the clock skew between our clock and the tick timestamps that drive candle time.
/// Candle times come from Pocket Option server time minus a hardcoded offset (PocketOptionWebSocketStream.ServerTimeOffsetSec),
/// and all freshness checks compare them with DateTime.UtcNow. If PO changes its timezone/DST or its clock drifts,
/// candles silently shift. This monitor measures (receive time - tick time) per live tick, keeps a rolling window,
/// and alerts admins when the median leaves a sane band. It never changes trading behaviour.
///
/// Sign convention: skew &gt; 0 means tick timestamps are in the PAST relative to us (normal: network delay);
/// skew &lt; 0 means timestamps are in the FUTURE (the dangerous direction: such candles still look "fresh").
/// </summary>
public static class ClockSkewMonitor
{
    public record SkewSnapshot(int Samples, double MedianMs, double P05Ms, double P95Ms, double MinMs, double MaxMs, bool Alerting, DateTime UpdatedUtc);

    private const int Capacity = 512;
    private const int MinSamples = 100;
    private const double AlertAbsMs = 10_000;   // alert when |median skew| > 10s
    private const double RecoverAbsMs = 5_000;  // hysteresis: recover when |median skew| < 5s
    private static readonly TimeSpan EvalInterval = TimeSpan.FromSeconds(60);

    private static readonly double[] _ring = new double[Capacity];
    private static readonly object _lock = new();
    private static int _next;
    private static int _count;
    private static DateTime _lastEvalUtc = DateTime.MinValue;
    private static bool _alerting;
    private static SkewSnapshot? _latest;

    /// <summary>Replaceable sink (tests swap it out). Default: Telegram admins.</summary>
    public static Func<string, Task> AlertSink { get; set; } = msg => TelegramBotService.SendMessageToAdmins(msg);

    public static SkewSnapshot? GetLatest() => _latest;

    /// <summary>Record one live (non-backfill) tick. Cheap: ring write + a throttled evaluation once a minute.</summary>
    public static void Record(long tickTimeUtcTicks) => Record(tickTimeUtcTicks, DateTime.UtcNow);

    public static void Record(long tickTimeUtcTicks, DateTime nowUtc)
    {
        double skewMs = (nowUtc.Ticks - tickTimeUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (double.IsNaN(skewMs) || double.IsInfinity(skewMs)) return;

        bool evaluate = false;
        lock (_lock)
        {
            _ring[_next] = skewMs;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            if (nowUtc - _lastEvalUtc >= EvalInterval)
            {
                _lastEvalUtc = nowUtc;
                evaluate = true;
            }
        }

        if (evaluate) Evaluate(nowUtc);
    }

    /// <summary>Compute statistics over the rolling window and raise/clear the alert with hysteresis.</summary>
    public static SkewSnapshot? Evaluate(DateTime nowUtc)
    {
        double[] window;
        lock (_lock)
        {
            if (_count == 0) return _latest;
            window = new double[_count];
            Array.Copy(_ring, window, _count);
        }

        Array.Sort(window);
        double Pct(double p) => window[Math.Clamp((int)Math.Round(p * (window.Length - 1)), 0, window.Length - 1)];
        double median = Pct(0.5);

        string? message = null;
        bool alertingNow;
        lock (_lock)
        {
            if (window.Length >= MinSamples)
            {
                if (!_alerting && Math.Abs(median) > AlertAbsMs)
                {
                    _alerting = true;
                    message = BuildAlert(median, window.Length, recovered: false);
                }
                else if (_alerting && Math.Abs(median) < RecoverAbsMs)
                {
                    _alerting = false;
                    message = BuildAlert(median, window.Length, recovered: true);
                }
            }
            alertingNow = _alerting;
            _latest = new SkewSnapshot(window.Length, Math.Round(median, 1), Math.Round(Pct(0.05), 1), Math.Round(Pct(0.95), 1),
                Math.Round(window[0], 1), Math.Round(window[^1], 1), alertingNow, nowUtc);
        }

        if (message != null)
        {
            BotLogger.Warn($"[ClockSkew] {message.Replace("\n", " | ")}");
            _ = Task.Run(async () =>
            {
                try { await AlertSink(message); }
                catch (Exception ex) { BotLogger.Warn($"[ClockSkew] Alert delivery failed: {ex.Message}"); }
            });
        }

        return _latest;
    }

    private static string BuildAlert(double medianMs, int samples, bool recovered)
    {
        if (recovered)
            return $"✅ <b>Clock skew normalized</b>\nMedian tick-time skew is back to {medianMs / 1000.0:F1}s ({samples} samples).";

        double sec = medianMs / 1000.0;
        string direction = sec < 0
            ? "метки времени тиков в БУДУЩЕМ (опасно: такие свечи считаются свежими)"
            : "метки времени тиков в прошлом (свечи будут считаться устаревшими, бот перестанет торговать)";

        string hint = "";
        double hours = Math.Abs(sec) / 3600.0;
        if (hours > 0.8 && Math.Abs(hours - Math.Round(hours)) < 0.1)
            hint = $"\n• Похоже на сдвиг часового пояса/DST (~{Math.Round(hours)} ч): проверьте константу <code>ServerTimeOffsetSec=7200</code> в PocketOptionWebSocketStream.";

        return $"⏱ <b>Clock skew detected</b>\n" +
               $"• Медиана перекоса: <b>{sec:F1} с</b> ({samples} тиков)\n" +
               $"• Смысл: {direction}" + hint;
    }

    /// <summary>Test helper: clear state.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            _next = 0; _count = 0; _alerting = false; _latest = null; _lastEvalUtc = DateTime.MinValue;
        }
    }
}
