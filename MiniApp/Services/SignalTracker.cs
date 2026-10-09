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
    // FIX #6: internal ????? PendingTradeVerificationService ??? ?????? ???? ??? ???????????? ????
    internal static readonly ConcurrentDictionary<string, double> _livePrices = new();

    public static void UpdateLivePrice(string asset, double price)
    {
        _livePrices[asset] = price;
    }

    private static List<(string signalName, int verified, int correct)>? _signalVotesCache;
    private static DateTime _signalVotesCacheExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _signalVotesCacheLock = new(1, 1);
    // Legacy background verification timer removed.

    // ── Public Write API ───────────────────────────────────────────────────

    /// <summary>
    /// Record a new prediction. Will be verified automatically after expiryCandles × timeframeSecs seconds.
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
        int probability = 50,
        double taScore = 0.0,
        double smcScore = 0.0,
        double mlProb = 0.0, double mlScore = 0.0, string featuresJson = "",
        string smcBosDir = "NONE", bool smcHasOb = false, bool smcHasFvg = false,
        double ofDeltaRatio = 1.0, string ofState = "NEUTRAL",
        string marketRegime = "UNKNOWN", string velocityRegime = "UNKNOWN",
        double atrAtSignal = 0.0, double adxAtSignal = 0.0, double rsiAtSignal = 50.0,
        bool higherTfAligned = false, int minutesToNews = -1,
        string reasoningText = "", string mlModelVersion = "", double mlModelAccuracy = 0.0,
        double priceEntropy = 0.0, int trendMaturity = 0, double pricePositionPct = 0.5, bool bbSqueeze = false, string? taTelemetry = null, string? mlTelemetry = null, string? smcTelemetry = null)
    {
        if (MarketDataFetcher.IsWeekendNow()) { Console.WriteLine($"[Tracker] Weekend OTC mode active. Skipping recording for {asset}."); return; }
        string sym = asset.ToUpper();
        var now = DateTime.UtcNow;
        // FIX FORENSICS: Real broker expiry is measured from trade entry time (now), NOT from the past grid boundary.
        // Using gridTime truncated trades placed mid-candle (e.g. 5s trades verified after 2s, 1m trades after 30s).
        // Adding durationSecs + 1s buffer guarantees the trade has fully expired before verification.
        int durationSecs = Math.Max(1, expiryCandles) * timeframeSecs;
        DateTime verifyAt = now.AddSeconds(durationSecs + 1);

        string cooldownKey = $"{asset}_{timeframe}";
        
        bool isOnCooldown = _cooldownCache.TryGetValue(cooldownKey, out _);
        if (isOnCooldown)
        {
            BotLogger.Warn($"[Tracker] Cooldown active for {cooldownKey}. Skipping duplicate signal recording.");
            return;
        }

        // FIX PRIORITY-6: Cooldown ???????? ?? 10 ??????
        // MemoryCache ????????????? ?????? ???? ????? 10 ?????? ??? ??????? O(N) ??????? ???????? ??????.
        int cooldownCandles = Math.Max(expiryCandles, 3); _cooldownCache.Set(cooldownKey, true, TimeSpan.FromSeconds(cooldownCandles * timeframeSecs));

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
            Probability = probability,
            TaScore = taScore,
            SmcScore = smcScore,
            MlProb = mlProb,
            MlScore = mlScore,
            FeaturesJson = featuresJson,
            SmcBosDir = smcBosDir,
            SmcHasOb = smcHasOb,
            SmcHasFvg = smcHasFvg,
            OfDeltaRatio = ofDeltaRatio,
            OfState = ofState,
            DynamicHorizon = expiryCandles,
            MarketRegime = marketRegime,
            VelocityRegime = velocityRegime,
            AtrAtSignal = atrAtSignal,
            AdxAtSignal = adxAtSignal,
            RsiAtSignal = rsiAtSignal,
            HigherTfAligned = higherTfAligned,
            MinutesToNews = minutesToNews,
            ReasoningText = reasoningText,
            MlModelVersion = mlModelVersion,
            MlModelAccuracy = mlModelAccuracy,
            PriceEntropy = priceEntropy,
            TrendMaturity = trendMaturity,
            PricePositionPct = pricePositionPct,
            BbSqueeze = bbSqueeze,
            TaTelemetry = taTelemetry,
            MlTelemetry = mlTelemetry,
            SmcTelemetry = smcTelemetry
        };

        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SavePendingTradeAsync(record);
        
        // Local Task.Run verification removed. PendingTradeVerificationService handles all verifications.

        Console.WriteLine($"[Tracker] Recorded {direction} {asset}/{timeframe} @ {price:F5} " +
                          $"? target verify at {verifyAt:HH:mm:ss}");
    }

    // ---------------- In-Memory TTL Cache for High-Frequency Read API ------------------------
    private static AccuracyStats? _cachedOverallStats;
    private static DateTime _overallStatsExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _overallStatsLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, (AccuracyStats stats, DateTime expiry)> _cachedAssetStats = new();

    public static async Task<AccuracyStats> GetOverallStatsAsync()
    {
        if (_cachedOverallStats != null && DateTime.UtcNow < _overallStatsExpiry)
            return _cachedOverallStats;

        await _overallStatsLock.WaitAsync();
        try
        {
            if (_cachedOverallStats != null && DateTime.UtcNow < _overallStatsExpiry)
                return _cachedOverallStats;

            var (total, verified, correct) = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetOverallStatsAsync();
            var stats = new AccuracyStats("ALL", total, verified, correct);
            _cachedOverallStats = stats;
            _overallStatsExpiry = DateTime.UtcNow.AddSeconds(60);
            return stats;
        }
        finally
        {
            _overallStatsLock.Release();
        }
    }

    public static async Task<AccuracyStats> GetStatsAsync(string asset, string timeframe)
    {
        string key = $"{asset.ToUpper()}_{timeframe.ToLower()}";
        if (_cachedAssetStats.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.expiry)
            return entry.stats;

        var (total, verified, correct) = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetStatsAsync(asset, timeframe);
        var stats = new AccuracyStats($"{asset}_{timeframe}", total, verified, correct);
        _cachedAssetStats[key] = (stats, DateTime.UtcNow.AddSeconds(60));
        return stats;
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
        if (v.verified <= 0) return baseWeight; // BUG-3 FIX: verified=0 > division by zero
        double agreeRate = (double)v.correct / v.verified;
        double adjustment = agreeRate / 0.5;
        return System.Math.Clamp(baseWeight * adjustment, 0.2, 2.0);
    }

    public static async Task<double> GetSignalWeightAsync(string signalName, double baseWeight = 1.0)
    {
        // L1-FIX: Используем кэш 30 сек — убираем SELECT на каждый тик
        if (_signalVotesCache == null || DateTime.UtcNow > _signalVotesCacheExpiry)
        {
            await _signalVotesCacheLock.WaitAsync();
            try
            {
                // Double-check после получения блокировки
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

    // ── Background Verification ────────────────────────────────────────────

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

    // ── Data Types ─────────────────────────────────────────────────────────

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
        
        public int Probability { get; set; }
        public double TaScore { get; set; }
        public double OfScore { get; set; }
        public double SmcScore { get; set; }
        public double MlProb { get; set; }
        public double MlScore { get; set; }
        public string FeaturesJson { get; set; } = "";
        public string SmcBosDir { get; set; } = "NONE";
        public bool SmcHasOb { get; set; }
        public bool SmcHasFvg { get; set; }
        public double OfDeltaRatio { get; set; }
        public string OfState { get; set; } = "NEUTRAL";
        public int DynamicHorizon { get; set; } = 3;

        // Market context — captured at signal time, flows through pending_trades to trade_outcomes
        public string MarketRegime    { get; set; } = "UNKNOWN";
        public string VelocityRegime  { get; set; } = "UNKNOWN";
        public double AtrAtSignal     { get; set; }
        public double AdxAtSignal     { get; set; }
        public double RsiAtSignal     { get; set; } = 50.0;
        public bool   HigherTfAligned { get; set; }
        public int    MinutesToNews   { get; set; } = -1;

        // Decision reasoning + ML model metadata
        public string ReasoningText   { get; set; } = "";
        public string MlModelVersion  { get; set; } = "";
        public double MlModelAccuracy { get; set; }
        
        // Phase 3 context
        public double PriceEntropy { get; set; }
        public int    TrendMaturity { get; set; }
        public double PricePositionPct { get; set; }
        public bool   BbSqueeze { get; set; }

        // Excursion metrics during trade lifetime
        public double MaxFavorableBps { get; set; } = 0.0;
        public double MaxAdverseBps   { get; set; } = 0.0;

        public string? TaTelemetry { get; set; }
        public string? MlTelemetry { get; set; }
        public string? SmcTelemetry { get; set; }
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






