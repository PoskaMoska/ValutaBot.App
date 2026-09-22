using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;

namespace ValutaBot.MiniApp;

/// <summary>
/// Tracks prediction signals and automatically verifies them after the candle expires.
/// Provides per-asset, per-timeframe, and per-source win rate statistics.
/// Now completely stateless (stores pending trades in PostgreSQL).
/// </summary>
public static class SignalTracker
{
    // Cooldown map using MemoryCache to automatically handle expiry without O(N) sweeping
    private static readonly Microsoft.Extensions.Caching.Memory.MemoryCache _cooldownCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
    // FIX #6: internal чтобы PendingTradeVerificationService мог читать цены без дублирования кода
    internal static readonly ConcurrentDictionary<string, double> _livePrices = new();

    public static void UpdateLivePrice(string asset, double price)
    {
        _livePrices[asset] = price;
    }

    private static List<(string signalName, int verified, int correct)>? _signalVotesCache;
    private static DateTime _signalVotesCacheExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _signalVotesCacheLock = new(1, 1);
    // Legacy background verification timer removed.

    // РІвЂќР‚РІвЂќР‚ Public Write API РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

    /// <summary>
    /// Record a new prediction. Will be verified automatically after expiryCandles Р“вЂ” timeframeSecs seconds.
    /// </summary>
    public static async Task RecordPredictionAsync(
        string direction,
        string asset,
        string timeframe,
        double price,
        int expiryCandles = 3,
        int timeframeSecs = 60,
        bool isForex = false,
        Dictionary<string, string>? sourceDirections = null,
        double taScore = 0.0,
        double ofScore = 0.0,
        double smcScore = 0.0,
        double mlProb = 0.0, double mlScore = 0.0)
    {
        string sym = asset.ToUpper();
        var now = DateTime.UtcNow;
        long currentTicks = now.Ticks;
        long intervalTicks = TimeSpan.FromSeconds(timeframeSecs).Ticks;
        DateTime gridTime = new DateTime(currentTicks - (currentTicks % intervalTicks), DateTimeKind.Utc);
        DateTime verifyAt = gridTime.AddSeconds(expiryCandles * timeframeSecs);

        string cooldownKey = $"{asset}_{timeframe}";
        
        bool isOnCooldown = _cooldownCache.TryGetValue(cooldownKey, out _);
        if (isOnCooldown)
        {
            BotLogger.Warn($"[Tracker] Cooldown active for {cooldownKey}. Skipping duplicate signal recording.");
            return;
        }

        // FIX PRIORITY-6: Cooldown увеличен до 10 секунд
        // MemoryCache автоматически удалит ключ через 10 секунд без ручного O(N) прохода сборщика мусора.
        _cooldownCache.Set(cooldownKey, true, TimeSpan.FromSeconds(10));

        var record = new PredictionRecord
        {
            Id          = Guid.NewGuid().ToString("N")[..8],
            Direction   = direction,
            Asset       = asset,
            Timeframe   = timeframe,
            BrokerSymbol = sym,
            EntryPrice  = price,
            CreatedAt   = DateTime.UtcNow,
            VerifyAt    = verifyAt,
            IsForex     = isForex,
            SourceDirections = sourceDirections ?? new Dictionary<string, string>(),
            TaScore = taScore,
            OfScore = ofScore,
            SmcScore = smcScore,
            MlProb = mlProb,
            MlScore = mlScore
        };

        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SavePendingTradeAsync(record);
        
        // Local Task.Run verification removed. PendingTradeVerificationService handles all verifications.

        Console.WriteLine($"[Tracker] Recorded {direction} {asset}/{timeframe} @ {price:F5} " +
                          $"— target verify at {verifyAt:HH:mm:ss}");
    }

    // ──────────────── Public Read API ────────────────────────────────────────────────────────

    public static async Task<AccuracyStats> GetOverallStatsAsync()
    {
        var (total, verified, correct) = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetOverallStatsAsync();
        return new AccuracyStats("ALL", total, verified, correct);
    }

    public static async Task<AccuracyStats> GetStatsAsync(string asset, string timeframe)
    {
        var (total, verified, correct) = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetStatsAsync(asset, timeframe);
        return new AccuracyStats($"{asset}_{timeframe}", total, verified, correct);
    }

    public static async Task<AccuracyStats[]> GetAllStatsAsync()
    {
        var rows = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetAllStatsAsync();
        return rows.Select(r => new AccuracyStats($"{r.asset}_{r.timeframe}", r.total, r.verified, r.correct)).ToArray();
    }

    public static async Task<int> GetPendingCountAsync()
    {
        var (total, verified, _) = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetOverallStatsAsync();
        return total - verified;
    }

    public static async Task<(string name, double agreeRatePct, double weight, int count)[]> GetSignalStatsAsync()
    {
        var votes = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetAllSignalVotesAsync();
        return votes.Select(v =>
        {
            double agreeRate = v.verified > 0 ? (double)v.correct / v.verified : 0.5;
            double weight = Math.Clamp(agreeRate / 0.5, 0.2, 2.0); // simple calibration
            return (v.signalName, Math.Round(agreeRate * 100, 1), Math.Round(weight, 2), v.verified);
        }).OrderByDescending(s => s.Item2).ToArray();
    }

    public static double CalculateSignalWeight(System.Collections.Generic.IEnumerable<(string signalName, int verified, int correct)> votes, string signalName, double baseWeight = 1.0)
    {
        var v = System.Linq.Enumerable.FirstOrDefault(votes, x => x.signalName == signalName);
        if (v.verified <= 0) return baseWeight; // BUG-3 FIX: verified=0 → division by zero
        double agreeRate = (double)v.correct / v.verified;
        double adjustment = agreeRate / 0.5;
        return System.Math.Clamp(baseWeight * adjustment, 0.2, 2.0);
    }

    public static async Task<double> GetSignalWeightAsync(string signalName, double baseWeight = 1.0)
    {
        // L1-FIX: Р ВРЎРѓР С—Р С•Р В»РЎРЉР В·РЎС“Р ВµР С Р С”РЎРЊРЎв‚¬ 30 РЎРѓР ВµР С” РІР‚вЂќ РЎС“Р В±Р С‘РЎР‚Р В°Р ВµР С SELECT Р Р…Р В° Р С”Р В°Р В¶Р Т‘РЎвЂ№Р в„– РЎвЂљР С‘Р С”
        if (_signalVotesCache == null || DateTime.UtcNow > _signalVotesCacheExpiry)
        {
            await _signalVotesCacheLock.WaitAsync();
            try
            {
                // Double-check Р С—Р С•РЎРѓР В»Р Вµ Р С—Р С•Р В»РЎС“РЎвЂЎР ВµР Р…Р С‘РЎРЏ Р В±Р В»Р С•Р С”Р С‘РЎР‚Р С•Р Р†Р С”Р С‘
                if (_signalVotesCache == null || DateTime.UtcNow > _signalVotesCacheExpiry)
                {
                    _signalVotesCache = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetAllSignalVotesAsync();
                    _signalVotesCacheExpiry = DateTime.UtcNow.AddSeconds(30);
                }
            }
            finally
            {
                _signalVotesCacheLock.Release();
            }
        }
        return CalculateSignalWeight(_signalVotesCache, signalName, baseWeight);
    }

    // РІвЂќР‚РІвЂќР‚ Background Verification РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

    // Validation logic (VerifyPendingAsync and FetchExitPriceAsync) was fully surgically excised (Ace of Swords).
    // The legacy timer caused race conditions with the new memory-driven validator,
    // and using JSON serialization on old archive rows crashed the system (10 of Swords + 6 of Cups).

    private static string MapToBrokerSymbol(string asset) =>
        asset.ToUpper()
             .Replace("OTC", "")
             .Replace("/", "")
             .Replace(" ", "")
             .Replace("-", "")
             .Trim() switch
        {
            "EUR" or "EURUSD"  => "EURUSDT",
            "GBP" or "GBPUSD"  => "GBPUSDT",
            "AUD" or "AUDUSD"  => "AUDUSDT",
            "BTC" or "BITCOIN" => "BTCUSDT",
            "ETH"              => "ETHUSDT",
            "SOL"              => "SOLUSDT",
            var s when s.Length > 0 && !s.EndsWith("USDT") => s + "USDT",
            var s => s
        };

    // РІвЂќР‚РІвЂќР‚ Data Types РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

    public class PredictionRecord
    {
        public string   Id            { get; set; } = "";
        public string   Direction     { get; set; } = "";
        public string   Asset         { get; set; } = "";
        public string   Timeframe     { get; set; } = "";
        public string   BrokerSymbol  { get; set; } = "";
        public double   EntryPrice    { get; set; }
        public double?  ExitPrice     { get; set; }
        public double   PnlBps        { get; set; }
        public DateTime CreatedAt     { get; set; }
        public DateTime VerifyAt      { get; set; }
        public bool     IsForex       { get; set; }
        public bool?    WasCorrect    { get; set; }
        public Dictionary<string, string> SourceDirections { get; set; } = new();
        
        public double TaScore { get; set; }
        public double OfScore { get; set; }
        public double SmcScore { get; set; }
        public double MlProb { get; set; }
        public double MlScore { get; set; }
    }

    public class AccuracyStats
    {
        public string Key { get; }
        public int Total { get; }
        public int Verified { get; }
        public int Correct { get; }
        public int Incorrect => Verified - Correct;
        public int Pending => Total - Verified;

        public AccuracyStats(string key, int total, int verified, int correct)
        {
            Key = key;
            Total = total;
            Verified = verified;
            Correct = correct;
        }

        public double WinRate => Verified > 0
            ? Math.Round((double)Correct / Verified * 100, 1)
            : 0;
        public bool HasData => Verified >= 5;

        public double CalibrationFactor => HasData
            ? Math.Clamp(WinRate / 50.0, 0.7, 1.3)
            : 1.0;
    }
}





