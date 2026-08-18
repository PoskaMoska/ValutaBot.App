using System;
using System.Numerics;

namespace ValutaBot.MiniApp;

public class TechnicalAnalysisEngine : ITechnicalAnalysisEngine
{
    public static ITechnicalAnalysisEngine Instance { get; set; } = new TechnicalAnalysisEngine();

    private readonly IndicatorCache _cache = new();

    public double ComputeRsi(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
        => _cache.GetRsi(asset, timeframe, candles, period);

    public double ComputeConnorsRsi(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles)
        => _cache.GetConnorsRsi(asset, timeframe, candles);

    public double ComputeHma(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
        => _cache.GetHma(asset, timeframe, candles, period);

    public double ComputeEma(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
        => _cache.GetEma(asset, timeframe, candles, period);

    public (double adx, double pdi, double mdi) ComputeTrueAdx(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
        => _cache.GetAdx(asset, timeframe, candles, period);

    public double ComputeAtr(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
        => _cache.GetAtr(asset, timeframe, candles, period);

    public ValutaBot.MiniApp.Indicators.StatefulSmc GetSmcState(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice)
        => _cache.GetSmcState(asset, timeframe, candles, currentPrice);

    public (double score, double confidence, double rsiVal, double hmaVal, double volStrengthVal, double atrVal) ScoreTimeframe(
        string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<double> volumes, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default,
        double? adxOverride = null, double? atrOverride = null, bool isForex = false)
    {
        if (prices.Length < 14 || candles.Length < 14)
        {
            throw new Exception($"ОТКАЗ API: Недостаточно свечей для технического анализа. Получено {prices.Length}. Нужно минимум 14.");
        }

        // Стандартный RSI(14) — базовый сигнал (стабилен на всех таймфреймах).
        // ConnorsRSI — подтверждающий сигнал (нестабилен на M1, даёт крайние значения при нормальном рынке).
        double rsi        = ComputeRsi(asset, timeframe, candles, 14);
        double connorsRsi = ComputeConnorsRsi(asset, timeframe, candles);
        double hma = ComputeHma(asset, timeframe, candles, 9);
        double lastPrice = prices[^1];

        var (adxVal, pdiVal, mdiVal) = adxOverride.HasValue
            ? (adxOverride.Value, 0.0, 0.0)
            : (candles.Length > 0 ? ComputeTrueAdx(asset, timeframe, candles) : (20.0, 0.0, 0.0));

        double atrVal = atrOverride.HasValue
            ? atrOverride.Value
            : (candles.Length > 0 ? ComputeAtr(asset, timeframe, candles) : 0);

        double score = 0;
        double confidence = 60.0;

        // ── Dynamic Thresholds (Adaptive Bands) ──
        double rsiOverbought = 70.0;
        double rsiOversold = 30.0;
        
        if (adxVal > 30.0) {
            // In strong trends, we need EXTREME exhaustion to fade, or we just follow trend
            rsiOverbought = 80.0; 
            rsiOversold = 20.0;
        } else if (adxVal < 20.0) {
            // In quiet ranges, smaller deviations are valid reversion points
            rsiOverbought = 65.0; 
            rsiOversold = 35.0;
        }

        // Adaptive Regime-Switching Weights (Level 3 Fix)
        double hmaWeight = 0.15;

        if (adxVal < 20.0)
        {
            // Ranging Market: Extreme Mean-Reversion ONLY (Dynamic Thresholds)
            hmaWeight = 0.0;
            if (rsi > rsiOverbought) score -= 0.8;
            else if (rsi < rsiOversold) score += 0.8;
        }
        else if (adxVal > 25.0)
        {
            // Trending Market: Boost Trend-Following (PDI/MDI alignment)
            hmaWeight = 0.40;
            if (pdiVal > mdiVal) score += 0.6; // Up trend
            if (mdiVal > pdiVal) score -= 0.6; // Down trend
        }
        else 
        {
            // Neutral zone (Transition)
            if (rsi > 75.0) score -= 0.4;
            else if (rsi < 25.0) score += 0.4;
        }

        // ConnorsRSI как подтверждающий сигнал.
        double connorsSignal = (connorsRsi - 50.0) / 50.0;
        if (adxVal > 25.0)
            score += Math.Clamp(connorsSignal * 0.15, -0.15, 0.15); 
        else if (adxVal < 20.0)
            score -= Math.Clamp(connorsSignal * 0.10, -0.10, 0.10); 

        if (lastPrice > hma) score += hmaWeight;
        else if (lastPrice < hma) score -= hmaWeight;

        if (adxVal > 25.0)
        {
            confidence += Math.Min((adxVal - 25.0) * 0.8, 20.0);
        }

        // Исправление: volStrength считается по rolling CVD (5 свечей) вместо разницы одного тика.
        // Предыдущая версия брала sign(prices[^1] - prices[^2]) — чистый шум при нейтральном рынке.
        double volStrength = 0.0;
        if (volumes.Length >= 5)
        {
            int volCount = 0;
            double volSum = 0;
            int startIdx = Math.Max(0, volumes.Length - 21);
            for (int i = startIdx; i < volumes.Length - 1; i++)
            {
                volSum += volumes[i];
                volCount++;
            }
            double avgVol = volCount > 0 ? volSum / volCount : 0.0;
            double lastVol = volumes[^1];
            if (avgVol > 1e-9)
            {
                // Rolling CVD: накопленное давление покупателей/продавцов за 5 свечей вместо 1 тика
                double rollingCvd = 0;
                int cvdLookback = Math.Min(5, prices.Length - 1);
                for (int i = 1; i <= cvdLookback; i++)
                {
                    double pc = prices[^i] - prices[^(i + 1)];
                    double v  = i <= volumes.Length ? volumes[^i] : 0;
                    rollingCvd += pc >= 0 ? v : -v;
                }

                double ratio = lastVol / avgVol;
                // Нормализуем CVD по среднему объёму для масштабируемости
                double cvdNorm = avgVol > 1e-9 ? Math.Clamp(rollingCvd / (avgVol * cvdLookback), -1.0, 1.0) : 0;
                volStrength = cvdNorm * Math.Max(0.0, Math.Min(ratio - 0.8, 1.0));

                // Volume bonus: up to +10 confidence points
                double volBonus = Math.Abs(volStrength) * 10.0;
                confidence += Math.Min(volBonus, 10.0);
                score += Math.Clamp(volStrength * 0.15, -0.20, 0.20);
            }
        }

        // RSI extremes add conviction
        if (rsi <= 30.0 || rsi >= 70.0)
            confidence += Math.Min(Math.Abs(rsi - 50.0) * 0.3, 5.0);

        // Now achievable max: 60 (base) + 20 (ADX) + 10 (volume) + 5 (RSI) = 95
        return (score, Math.Clamp(confidence, 50.0, 95.0), Math.Round(rsi, 1), Math.Round(hma, 5), Math.Round(volStrength, 2), Math.Round(atrVal, 6));
    }

    public record GatekeeperResult(bool IsTradeable, string Reason, double Atr, double Adx);

    public GatekeeperResult ValidateMarketGatekeeper(string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default)
    {
        if (prices.Length < 15) return new GatekeeperResult(false, "Недостаточно данных цены для проверки Gatekeeper", 0, 0);

        double atr = candles.Length >= 15 ? ComputeAtr(asset, timeframe, candles) : 0;
        var (adx, _, _) = candles.Length >= 15 ? ComputeTrueAdx(asset, timeframe, candles) : (20.0, 0, 0);

        double minPrice = double.MaxValue;
        double maxPrice = double.MinValue;
        int startIdx = prices.Length - 15;
        for (int i = startIdx; i < prices.Length; i++)
        {
            if (prices[i] < minPrice) minPrice = prices[i];
            if (prices[i] > maxPrice) maxPrice = prices[i];
        }
        
        double priceRange = maxPrice - minPrice;
        // Fix: when ATR = 0 (not yet warmed up), fallback to an asset-appropriate
        // minimum pip range so the dead-market check is never silently disabled.
        double fallbackThreshold = asset.Length == 6 && asset.Contains("JPY") ? 0.005 : 0.00005;
        double deadMarketThreshold = atr > 0 ? Math.Max(1e-10, atr * 0.10) : fallbackThreshold;

        if (priceRange < deadMarketThreshold)
        {
            BotLogger.Warn($"[Gatekeeper] Market is completely flat / frozen. PriceRange={priceRange}, Threshold={deadMarketThreshold}. Aborting analysis.");
            return new GatekeeperResult(false, "⚠️ Рынок в состоянии застоя (нет колебаний цены).", atr, adx);
        }

        double maxCandleRange = 0;
        if (candles.Length > 0)
        {
            int cStartIdx = Math.Max(0, candles.Length - 3);
            for (int i = cStartIdx; i < candles.Length; i++)
            {
                double range = candles[i].High - candles[i].Low;
                if (range > maxCandleRange) maxCandleRange = range;
            }
        }
        
        if (atr > 0 && maxCandleRange > atr * 4.0)
        {
            BotLogger.Warn($"[Gatekeeper] Market Flash Crash detected! Single candle range {maxCandleRange} is > 4x ATR {atr}.");
            return new GatekeeperResult(false, "⚠️ Обнаружен аномальный выброс волатильности (Сквиз/Flash Crash). Торговля приостановлена для защиты депозита.", atr, adx);
        }

        return new GatekeeperResult(true, "Рынок активен", atr, adx);
    }

    public double CalculateVolatilityRatio(ReadOnlySpan<double> prices)
    {
        if (prices.Length < 26) return 1.0;

        Span<double> returns = stackalloc double[25];
        for (int i = 0; i < 25; i++)
        {
            int idx = prices.Length - 25 + i;
            double prevPrice = prices[idx - 1] <= 0 ? 1e-10 : prices[idx - 1];
            double currPrice = prices[idx] <= 0 ? 1e-10 : prices[idx];
            returns[i] = Math.Log(currPrice / prevPrice);
        }

        double shortVol = StandardDeviationScalar(returns.Slice(20, 5));
        double longVol = StandardDeviationScalar(returns.Slice(0, 20));

        if (longVol < 1e-10) return 1.0;
        return shortVol / longVol;
    }

    private static double StandardDeviationScalar(ReadOnlySpan<double> values)
    {
        int count = values.Length;
        if (count < 2) return 0.0;
        
        double sum = 0;
        for (int i = 0; i < count; i++) sum += values[i];
        double mean = sum / count;
        
        double sqSum = 0;
        for (int i = 0; i < count; i++)
        {
            double diff = values[i] - mean;
            sqSum += diff * diff;
        }
        
        return Math.Sqrt(sqSum / (count - 1));
    }
}
