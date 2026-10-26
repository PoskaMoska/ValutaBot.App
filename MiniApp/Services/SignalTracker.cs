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
    // Cooldown map using atomic CAS ticks to prevent duplicate trade entry races
    private static readonly ConcurrentDictionary<string, long> _cooldownExpiryTicks = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly ConcurrentDictionary<string, double> _livePrices = new();

    public static void UpdateLivePrice(string asset, double price)
    {
        _livePrices[asset] = price;
    }

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
        if (!PriceFeed.UsePocketOption && MarketDataFetcher.IsWeekendNow()) { Console.WriteLine($"[Tracker] Weekend OTC mode active. Skipping recording for {asset}."); return; }

        // Hard Circuit Breaker Guard: Never record a live order when Circuit Breaker is active!
        bool isRealTrade = direction is "BUY" or "PUT";
        if (isRealTrade && TradeOutcomeTracker.CircuitBreaker?.IsHalted() == true)
        {
            BotLogger.Warn($"[Tracker] BLOCKED live {direction} on {asset}: Circuit Breaker is HALTED ({TradeOutcomeTracker.CircuitBreaker.GetHaltedReason()}). Downgrading to SHADOW.");
            direction = "SHADOW_" + direction;
            isRealTrade = false;
        }

        string sym = asset.ToUpper();
        var now = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        int durationSecs = Math.Max(1, expiryCandles) * timeframeSecs;
        DateTime verifyAt = now.AddSeconds(durationSecs);

        string cooldownKey = isRealTrade ? $"REAL_{asset}_{timeframe}" : $"SHADOW_{asset}_{timeframe}";

        // Separate REAL vs SHADOW cooldowns so background SHADOW/HOLD samples never block real live trades.
        double cooldownSecs = isRealTrade
            ? Math.Max(durationSecs, timeframeSecs)
            : Math.Max(1.0, timeframeSecs - 1.0);

        long nowTicks = DateTime.UtcNow.Ticks;
        long newExpiryTicks = nowTicks + TimeSpan.FromSeconds(cooldownSecs).Ticks;

        while (true)
        {
            if (_cooldownExpiryTicks.TryGetValue(cooldownKey, out long activeExpiryTicks))
            {
                if (nowTicks < activeExpiryTicks)
                {
                    BotLogger.Warn($"[Tracker] Cooldown active for {cooldownKey}. Skipping duplicate signal recording.");
                    return;
                }
                if (_cooldownExpiryTicks.TryUpdate(cooldownKey, newExpiryTicks, activeExpiryTicks))
                    break;
            }
            else
            {
                if (_cooldownExpiryTicks.TryAdd(cooldownKey, newExpiryTicks))
                    break;
            }
        }

        var record = new PredictionRecord
        {
            Id          = Guid.NewGuid().ToString("N")[..8],
            Direction   = direction,
            Asset       = asset,
            Timeframe   = timeframe,
            BrokerSymbol = sym,
            EntryPrice  = price,
            CreatedAt   = now,
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

    public static Task<(string name, double agreeRatePct, double weight, int count)[]> GetSignalStatsAsync()
    {
        return Task.FromResult(Array.Empty<(string name, double agreeRatePct, double weight, int count)>());
    }

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
