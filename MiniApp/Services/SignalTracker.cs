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

    // B2-FIX: Semaphore prevents concurrent VerifyPendingAsync runs
    private static readonly SemaphoreSlim _verifySemaphore = new(1, 1);

    // L1-FIX: 30-РЎРѓР ВµР С”РЎС“Р Р…Р Т‘Р Р…РЎвЂ№Р в„– Р С”РЎРЊРЎв‚¬ signal_votes РІР‚вЂќ РЎС“Р В±Р С‘РЎР‚Р В°Р ВµРЎвЂљ 3 SELECT Р Р…Р В° Р С”Р В°Р В¶Р Т‘РЎвЂ№Р в„– РЎвЂљР С‘Р С”
    private static List<(string signalName, int verified, int correct)>? _signalVotesCache;
    private static DateTime _signalVotesCacheExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _signalVotesCacheLock = new(1, 1);

    private static readonly Timer _verifyTimer;

    static SignalTracker()
    {
        // Run verification every 30 seconds in the background
        _verifyTimer = new Timer(
            _ => Task.Run(async () =>
            {
                // B2-FIX: WaitAsync(0) РІР‚вЂќ non-blocking try-acquire. If already running, skip this tick.
                if (!await _verifySemaphore.WaitAsync(0)) return;
                try { await VerifyPendingAsync(); }
                catch (Exception ex) { Console.WriteLine($"[Tracker] Verify error: {ex.Message}"); }
                finally { _verifySemaphore.Release(); }
            }),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );
    }

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
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.ResolvePendingTradeAsync(record.Id, exitPrice.Value, isCorrect);
                        
                        foreach (var kvp in record.SourceDirections)
                        {
                            if (kvp.Value == "NEUTRAL") continue;
                            bool isSourceCorrect = (kvp.Value == "BUY" && exitPrice.Value > price) || (kvp.Value == "PUT" && exitPrice.Value < price);
                            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.RecordSignalVoteAsync(kvp.Key, isSourceCorrect);
                        }
                        
                        if (TradeOutcomeTracker.WfEngine != null) {
                            TradeOutcomeTracker.WfEngine.ProcessOutcome(record.Asset, record.Timeframe, isCorrect);
                        }
                        if (TradeOutcomeTracker.CalibrationEngine != null) {
                            TradeOutcomeTracker.CalibrationEngine.RecordOutcome(record.Asset, record.Timeframe, isCorrect);
                        }
                        InvalidateSignalVotesCache();
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

    public static async Task VerifyPendingAsync()
    {
        var now = DateTime.UtcNow;
        var toCheck = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.GetPendingTradesToVerifyAsync(now);

        if (toCheck.Count == 0) return;

        Console.WriteLine($"[Tracker] Verifying {toCheck.Count} prediction(s)...");

        foreach (var record in toCheck)
        {
            // Drop predictions older than 24h that still can't be verified
            if ((now - record.CreatedAt).TotalHours > 24)
            {
                await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                continue;
            }

            double? exitPrice = await FetchExitPriceAsync(record);
            if (exitPrice == -1)
            {
                // Unverifiable (e.g. missing API key, or invalid OTC pair). Delete it.
                await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                Console.WriteLine($"[Tracker] ~ {record.Asset}/{record.Timeframe} вЂ” unverifiable, discarded");
                continue;
            }
            if (exitPrice == null || exitPrice <= 0)
                continue; // try again next cycle

            double priceDiff = (exitPrice.Value - record.EntryPrice) / record.EntryPrice;

            // Р Рћ (Pocket Option) Concept Fix: Binary Options pay out even for a 1-pip difference. 
            // We must not treat small movements as 'flat market'. Only exact Ties (epsilon 1e-8) are Refunds.
            double minMove = 1e-8;
            if (Math.Abs(priceDiff) < minMove)
            {
                await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                Console.WriteLine($"[Tracker] ~ {record.Asset}/{record.Timeframe} РІР‚вЂќ flat market, discarded");
                continue;
            }

            if (record.Direction == "NEUTRAL")
            {
                await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);
                continue;
            }

            bool correct = record.Direction == "BUY" ? priceDiff > 0 : priceDiff < 0;
            record.ExitPrice  = exitPrice.Value;
            record.PnlBps     = Math.Round(priceDiff * 10000, 2);
            record.WasCorrect = correct;

            string winDirection = priceDiff > 0 ? "BUY" : "PUT";
            if (record.SourceDirections != null)
            {
                foreach (var kv in record.SourceDirections)
                {
                    if (kv.Value != "NEUTRAL")
                    {
                        bool wasSourceCorrect = kv.Value == winDirection;
                        await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.RecordSignalVoteAsync(kv.Key, wasSourceCorrect);
                    }
                }
            }

            _ = Task.Run(async () =>
            {
                try { await TradeOutcomeTracker.OnTradeVerifiedAsync(record); }
                catch (Exception ex) { Console.WriteLine($"[Tracker] ML Tracker Error for {record.Id}: {ex.Message}"); }
            });

            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DeletePendingTradeAsync(record.Id);

            string icon = correct ? "РІСљвЂ¦" : "РІСњРЉ";
            Console.WriteLine(
                $"[Tracker] {icon} {record.Asset}/{record.Timeframe} {record.Direction} " +
                $"entry={record.EntryPrice:F5} exit={exitPrice:F5} " +
                $"pnl={record.PnlBps:+0.0;-0.0} bps");
        }
    }

    private static async Task<double?> FetchExitPriceAsync(PredictionRecord record)
    {
        string sym = record.BinanceSymbol;

        // Fast path: Web Socket live prices (no allocations)
        // B1-FIX: Always return rented arrays to ArrayPool even when count==0.
        // Previously: `TryGet(...) && count > 0` РІР‚вЂќ short-circuit skipped Return() when count==0 РІвЂ вЂ™ ArrayPool leak РІвЂ вЂ™ OOM.
        if (BinanceWebSocketStream.TryGetLiveCandles(sym, "1m", out double[] wsOpens, out double[] wsHighs, out double[] wsLows, out double[] wsPrices, out double[] wsVolumes, out int count))
        {
            try
            {
                if (count > 0) return wsPrices[count - 1];
            }
            finally
            {
                if (wsPrices != null) 
                {
                    System.Buffers.ArrayPool<double>.Shared.Return(wsOpens);
                    System.Buffers.ArrayPool<double>.Shared.Return(wsHighs);
                    System.Buffers.ArrayPool<double>.Shared.Return(wsLows);
                    System.Buffers.ArrayPool<double>.Shared.Return(wsPrices);
                }
                if (wsVolumes != null) 
                {
                    System.Buffers.ArrayPool<double>.Shared.Return(wsVolumes);
                }
            }
        }

        // Fallback: Binance REST API (Historical Kline)
        if (!record.IsForex)
        {
            try
            {
                long endTime = new DateTimeOffset(record.VerifyAt).ToUnixTimeMilliseconds();
                double? binancePrice = await MarketDataFetcher.Instance.FetchHistoricalPriceAsync(sym, endTime);
                if (binancePrice.HasValue) return binancePrice;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tracker] Binance historical kline fetch failed for {sym}: {ex.Message}");
            }
        }

        // 2. TwelveData REST API (Forex)
        if (record.IsForex)
        {
            if (string.IsNullOrEmpty(TwelveDataService.GetApiKey())) return -1;
            string symbol = TwelveDataService.ConvertToTwelveSymbol(record.Asset) ?? "";
            if (string.IsNullOrEmpty(symbol)) return -1;

            if (DateTime.UtcNow.DayOfWeek == DayOfWeek.Saturday || DateTime.UtcNow.DayOfWeek == DayOfWeek.Sunday)
            {
                Console.WriteLine($"[Tracker] Weekend detected. Skipping TwelveData fetch for {record.Asset} to avoid stale Friday prices.");
                return null;
            }

            try
            {
                // Р Рћ (Pocket Option) Concept Fix: Do NOT use FetchCurrentPriceAsync (live price slippage).
                // Fetch the last few candles and find the exact one that closed BEFORE VerifyAt.
                var data = await TwelveDataService.FetchCandlesAsync(record.Asset, "1min", limit: 3, cacheTtlSeconds: 0);
                if (data != null && data.Value.candles != null)
                {
                    var targetTime = record.VerifyAt;
                    // Candles are sorted oldest to newest (newest at ^1)
                    // We want the closest candle whose timestamp (start time) + 1 minute is <= targetTime
                    for (int i = data.Value.candles.Length - 1; i >= 0; i--)
                    {
                        var c = data.Value.candles[i];
                        if (c.Timestamp.AddMinutes(1) <= targetTime)
                        {
                            return c.Close;
                        }
                    }
                    // Fallback to the previous candle if exact match not found
                    if (data.Value.candles.Length >= 2) return data.Value.candles[^2].Close;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tracker] TwelveData fetch failed for {record.Asset}: {ex.Message}");
            }
        }

        return null;
    }

    // РІвЂќР‚РІвЂќР‚ Helpers РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚РІвЂќР‚

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




