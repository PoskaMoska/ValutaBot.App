using System.Collections.Concurrent;
using System.Globalization;
using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Market-Regime Aware Auto-Calibrating Signal Weight Engine for Forex & OTC market pairs.
/// Classifies current market phase ("TRENDING_IMPULSE", "RANGING_FLAT", "HIGH_VOLATILITY_CHAOS")
/// and applies adaptive regime weight matrices combined with rolling empirical win-rate statistics.
/// </summary>
public class AutoCalibrationEngine : IAutoCalibrationEngine
{
    public enum MarketRegime
    {
        TrendingImpulse,
        RangingFlat,
        HighVolatilityChaos
    }

    public readonly record struct SignalKey(string Source, string Asset, string Timeframe)
    {
        public override string ToString() => $"{Source}_{Asset}_{Timeframe}";
    }

    private class SourceStats
    {
        public int TotalTrades { get; set; }
        public double EmaWinRate { get; set; } = 0.50; // Нейтральный старт
    }

    private readonly ConcurrentDictionary<SignalKey, SourceStats> _statsMap = new();

    // L2-FIX: Восстанавливает состояние EMA-весов из PostgreSQL после рестарта.
    // Вызывается из TradeOutcomeTracker.InitializeAsync после загрузки калибровочных данных.
    public void RestoreState(string sourceName, string asset, string timeframe, int totalTrades, double emaWinRate)
    {
        var key = new SignalKey(sourceName, asset, timeframe);
        var stats = _statsMap.GetOrAdd(key, _ => new SourceStats());
        lock (stats)
        {
            stats.TotalTrades = totalTrades;
            stats.EmaWinRate  = emaWinRate;
        }
    }

    public IEnumerable<(SignalKey key, int totalTrades, double emaWinRate)> GetAllStats()
    {
        foreach (var kv in _statsMap)
        {
            int trades;
            double winRate;
            lock (kv.Value) 
            { 
                trades = kv.Value.TotalTrades;
                winRate = kv.Value.EmaWinRate;
            }
            yield return (kv.Key, trades, winRate);
        }
    }

    public MarketRegime DetectMarketRegime(double adx, double volRatio, double rsi, ReadOnlySpan<double> prices = default)
    {
        double entropy = prices.IsEmpty ? 0.5 : CalculateShannonEntropy(prices);

        // Dominant regime classification (used for logging/labels)
        if (adx >= 25.0 && volRatio <= 2.0 && entropy < 0.75)
        {
            return MarketRegime.TrendingImpulse;
        }

        if (entropy > 0.90 || volRatio > 1.75 || Math.Abs(rsi - 50.0) > 28.0)
        {
            return MarketRegime.HighVolatilityChaos;
        }

        return MarketRegime.RangingFlat;
    }

    private double CalculateShannonEntropy(ReadOnlySpan<double> prices)
    {
        int len = Math.Min(prices.Length, 1000);
        if (len < 10) return 1.0;

        int bins = (int)Math.Ceiling(1.0 + Math.Log2(len));

        Span<double> returns = stackalloc double[len - 1];
        double minRet = double.MaxValue;
        double maxRet = double.MinValue;

        int startIndex = prices.Length - len;
        for (int i = 1; i < len; i++)
        {
            double prev = prices[startIndex + i - 1];
            double curr = prices[startIndex + i];
            double ret = prev != 0 ? (curr - prev) / prev : 0;
            returns[i - 1] = ret;

            if (ret < minRet) minRet = ret;
            if (ret > maxRet) maxRet = ret;
        }

        if (minRet == maxRet) return 0.0;

        Span<int> histogram = stackalloc int[bins];
        double binSize = (maxRet - minRet) / bins;

        for (int i = 0; i < returns.Length; i++)
        {
            int binIndex = (int)((returns[i] - minRet) / binSize);
            if (binIndex == bins) binIndex--;
            histogram[binIndex]++;
        }

        double entropy = 0;
        for (int i = 0; i < bins; i++)
        {
            if (histogram[i] > 0)
            {
                double p = (double)histogram[i] / returns.Length;
                entropy -= p * Math.Log2(p);
            }
        }
        
        double maxEntropy = Math.Log2(bins);
        return maxEntropy > 0 ? entropy / maxEntropy : 0;
    }

    public double GetCalibratedRegimeWeight(
        string sourceName,
        string asset,
        string timeframe,
        MarketRegime regime)
    {
        var statsKey = new SignalKey(sourceName, asset, timeframe);
        double empiricalWinRate = _statsMap.TryGetValue(statsKey, out var stats) ? stats.EmaWinRate : 0.50;

        double baseWeight = sourceName switch
        {
            "TechAnalysis" => regime switch
            {
                MarketRegime.TrendingImpulse    => 1.2,
                MarketRegime.RangingFlat        => 0.8,
                MarketRegime.HighVolatilityChaos => 0.5,
                _ => 1.0
            },
            "OrderFlow" => regime switch
            {
                MarketRegime.TrendingImpulse    => 0.9,
                MarketRegime.RangingFlat        => 1.3, // OF хорошо работает в диапазонах (развороты)
                MarketRegime.HighVolatilityChaos => 0.7,
                _ => 1.0
            },
            "SMC" => regime switch
            {
                MarketRegime.TrendingImpulse    => 1.1, // BOS работает в тренде
                MarketRegime.RangingFlat        => 0.6, // SMC слабее в узких диапазонах
                MarketRegime.HighVolatilityChaos => 1.4, // Liquidity sweeps — в хаосе лучший сигнал
                _ => 1.0
            },
            // L4-FIX: LIGHTGBM и SKENDER_MATH ранее всегда возвращали 1.0 (ветка _).
            // Теперь калибруются по режиму — EMA-винрейт применяется корректно.
            "LIGHTGBM" => regime switch
            {
                MarketRegime.TrendingImpulse    => 1.3, // ML отлично распознаёт импульсы
                MarketRegime.RangingFlat        => 0.9,
                MarketRegime.HighVolatilityChaos => 0.6, // Хаос плохо предсказывается ML
                _ => 1.0
            },
            "SKENDER_MATH" => regime switch
            {
                MarketRegime.TrendingImpulse    => 1.0,
                MarketRegime.RangingFlat        => 1.2, // Математика устойчивее в флэте
                MarketRegime.HighVolatilityChaos => 0.8,
                _ => 1.0
            },
            _ => 1.0
        };

        // Calibration formula:
        // Weight is heavily penalized if empirical win rate drops below 50%.
        // Weight is boosted if empirical win rate is above 50%.
        double confidenceMultiplier = (empiricalWinRate - 0.50) * 2.0; // scales -1.0 to 1.0
        
        // Final weight is BaseWeight modified by up to +- 50% based on empirical performance.
        double finalWeight = baseWeight * (1.0 + (confidenceMultiplier * 0.5));
        
        // Clamp bounds
        return Math.Clamp(finalWeight, 0.1, 2.5);
    }

    public void RecordSourceOutcome(string sourceName, string asset, string timeframe, bool isWin)
    {
        var statsKey = new SignalKey(sourceName, asset, timeframe);
        var stats = _statsMap.GetOrAdd(statsKey, _ => new SourceStats());

        lock (stats)
        {
            stats.TotalTrades++;
            
            // EMA: alpha=0.05 (~20 trades) to prevent overfitting
            double alpha = 0.05;
            double outcomeVal = isWin ? 1.0 : 0.0;
            
            stats.EmaWinRate = (alpha * outcomeVal) + ((1.0 - alpha) * stats.EmaWinRate);
            
            // Persist to DB asynchronously
            _ = Task.Run(() => ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SaveCalibrationStateAsync(
                sourceName, asset, timeframe, stats.TotalTrades, stats.EmaWinRate));
        }
    }

    public double GetEmpiricalWinRate(string sourceName, string asset, string timeframe)
    {
        var statsKey = new SignalKey(sourceName, asset, timeframe);
        return _statsMap.TryGetValue(statsKey, out var stats) ? stats.EmaWinRate : 0.50;
    }

    public string GetStatsReport(string sourceName, string asset, string timeframe)
    {
        var statsKey = new SignalKey(sourceName, asset, timeframe);
        if (_statsMap.TryGetValue(statsKey, out var stats))
        {
            return $"Trades: {stats.TotalTrades}, EMA WinRate: {(stats.EmaWinRate * 100).ToString("F1", CultureInfo.InvariantCulture)}%";
        }
        return "No Data";
    }
}
