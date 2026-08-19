using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace ValutaBot.MiniApp;

/// <summary>
/// Tracks prediction signals and automatically verifies them after the candle expires.
/// Provides per-asset, per-timeframe, and per-source win rate statistics.
/// Now completely stateless (stores pending trades in PostgreSQL).
/// </summary>
public static class SignalTracker
{
    // Cooldown map to prevent duplicate signals spam (fine to stay in memory)
    private static readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private static readonly ConcurrentDictionary<string, double> _livePrices = new();

    public static void UpdateLivePrice(string asset, double price)
    {
        _livePrices[asset] = price;
    }

    private static List<(string signalName, int verified, int correct)>? _signalVotesCache;
    public static DateTime _signalVotesCacheExpiry = DateTime.MinValue;
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
        string? binanceSymbol = null,
        Dictionary<string, string>? sourceDirections = null)
    {
        string sym = (binanceSymbol ?? MapToBinanceSymbol(asset)).ToUpper();
        int verifyDelaySecs = expiryCandles * timeframeSecs + 5; // +5s buffer for candle close

        string cooldownKey = $"{asset}_{timeframe}";
        var now = DateTime.UtcNow;
        bool isOnCooldown = true;

        _cooldowns.AddOrUpdate(cooldownKey,
            _ => { isOnCooldown = false; return now; },
            (_, lastSignalAt) =>
            {
                if ((now - lastSignalAt).TotalSeconds >= 30)
                {
                    isOnCooldown = false;
                    return now;
                }
                return lastSignalAt;
            });

        if (isOnCooldown)
        {
            BotLogger.Warn($"[Tracker] Cooldown active for {cooldownKey}. Skipping duplicate signal recording.");
            return;
        }

        var record = new PredictionRecord
        {
            Id          = Guid.NewGuid().ToString("N")[..8],
            Direction   = direction,
            Asset       = asset,
            Timeframe   = timeframe,
            BinanceSymbol = sym,
            EntryPrice  = price,
            CreatedAt   = DateTime.UtcNow,
            VerifyAt    = DateTime.UtcNow.AddSeconds(verifyDelaySecs),
            IsForex     = isForex,
            SourceDirections = sourceDirections ?? new Dictionary<string, string>()
        };

        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SavePendingTradeAsync(record);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(verifyDelaySecs));
                double? exitPrice = null;
                
                if (_livePrices.TryGetValue(asset, out double memPrice))
                {
                    exitPrice = memPrice;
                }
                else if (!isForex)
                {
                    if (BinanceWebSocketStream.TryGetLiveCandles(sym, "1m", out _, out _, out _, out double[] wsPrices, out _, out int count) && count > 0)
                    {
                        exitPrice = wsPrices[count - 1];
                    }
                }
                
                if (exitPrice.HasValue && exitPrice.Value > 0)
                {
                    double priceDiff = (exitPrice.Value - price) / price;
                    if (Math.Abs(priceDiff) < 1e-8)
                    {
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                    }
                    else
                    {
                        bool isCorrect = (direction == "BUY" && exitPrice.Value > price) || (direction == "PUT" && exitPrice.Value < price);

                        // Save verified outcome and remove from pending
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SaveTradeOutcomeAsync(
                            new ValutaBot.App.MiniApp.Data.Repositories.TradeOutcomeRecord
                            {
                                Id = record.Id,
                                Direction = direction,
                                Asset = asset,
                                Timeframe = timeframe,
                                EntryPrice = price,
                                ExitPrice = exitPrice.Value,
                                PnlBps = Math.Round((exitPrice.Value - price) / price * 10000, 2),
                                WasWin = isCorrect,
                                CreatedAt = record.CreatedAt.ToString("O"),
                                VerifiedAt = DateTime.UtcNow.ToString("O")
                            });
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);

                        // Record per-source signal votes
                        foreach (var kvp in record.SourceDirections)
                        {
                            if (kvp.Value == "NEUTRAL") continue;
                            bool isSourceCorrect = (kvp.Value == "BUY" && exitPrice.Value > price) || (kvp.Value == "PUT" && exitPrice.Value < price);
                            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.RecordSignalVoteAsync(kvp.Key, isSourceCorrect);
                        }

                        // Update WalkForward engine
                        if (TradeOutcomeTracker.WfEngine != null)
                            TradeOutcomeTracker.WfEngine.RecordTradeOutcome(asset, timeframe, isCorrect);

                        // Update AutoCalibration engine
                        if (TradeOutcomeTracker.CalibrationEngine != null)
                            TradeOutcomeTracker.CalibrationEngine.RecordSourceOutcome("ENSEMBLE", asset, timeframe, isCorrect);

                        // Complete SGD online learning feedback chain
                        _ = Task.Run(() => MLPythonService.RecordOnlineTradeOutcomeAsync(
                            asset, timeframe, price, exitPrice.Value, direction,
                            wasWin: isCorrect, isForex: record.IsForex));

                        // Invalidate signal votes cache so UI refreshes
                        _signalVotesCacheExpiry = DateTime.MinValue;
                    }
                }
                else 
                {
                    await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[InMemoryVerify] Failed: {ex.Message}");
            }
        });

        Console.WriteLine($"[Tracker] Recorded {direction} {asset}/{timeframe} @ {price:F5} " +
                          $"РІР‚вЂќ verify in {verifyDelaySecs}s");
    }

    // РІвЂќР‚РІвЂќР‚ Public Read API РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

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
        if (v.verified < 5) return baseWeight;
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

    private static string MapToBinanceSymbol(string asset) =>
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
        public string   BinanceSymbol { get; set; } = "";
        public double   EntryPrice    { get; set; }
        public double?  ExitPrice     { get; set; }
        public double   PnlBps        { get; set; }
        public DateTime CreatedAt     { get; set; }
        public DateTime VerifyAt      { get; set; }
        public bool     IsForex       { get; set; }
        public bool?    WasCorrect    { get; set; }
        public Dictionary<string, string> SourceDirections { get; set; } = new();
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




