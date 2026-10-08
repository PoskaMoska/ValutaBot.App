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
        double? adxOverride = null, double? atrOverride = null, bool isForex = false,
        double? pdiOverride = null, double? mdiOverride = null)
    {
        var d = ScoreTimeframeDetailed(asset, timeframe, prices, volumes, candles, adxOverride, atrOverride, isForex, pdiOverride, mdiOverride);
        return (d.Score, d.Confidence, d.RsiVal, d.HmaVal, d.VolStrengthVal, d.AtrVal);
    }

    public TaScoringDetail ScoreTimeframeDetailed(
        string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<double> volumes, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default,
        double? adxOverride = null, double? atrOverride = null, bool isForex = false,
        double? pdiOverride = null, double? mdiOverride = null)
    {
        if (prices.Length < 14 || candles.Length < 14)
        {
            BotLogger.Warn($"[TAEngine] Not enough candles for full analysis ({prices.Length}/14). Returning neutral score.");
            return new TaScoringDetail(0.0, 50.0, 50.0, prices.Length > 0 ? prices[^1] : 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, "NOT_ENOUGH_DATA", 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        bool isSubMinute = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase);

        int rsiPeriod = isSubMinute ? 8 : 14;
        int hmaPeriod = isSubMinute ? 6 : 9;
        int adxPeriod = isSubMinute ? 7 : 14;
        int atrPeriod = isSubMinute ? 7 : 14;

        double rsi        = ComputeRsi(asset, timeframe, candles, rsiPeriod);
        double connorsRsi = ComputeConnorsRsi(asset, timeframe, candles);
        var (hmaCurrent, hmaPrevious) = _cache.GetHmaWithSlope(asset, timeframe, candles, hmaPeriod);
        double hma        = hmaCurrent;
        double hmaSlope   = hmaCurrent - hmaPrevious;

        var (adxVal, pdiVal, mdiVal) = adxOverride.HasValue
            ? (adxOverride.Value, pdiOverride ?? 0.0, mdiOverride ?? 0.0)
            : (candles.Length > 0 ? ComputeTrueAdx(asset, timeframe, candles, adxPeriod) : (20.0, 0.0, 0.0));

        double atrVal = atrOverride.HasValue
            ? atrOverride.Value
            : (candles.Length > 0 ? ComputeAtr(asset, timeframe, candles, atrPeriod) : 0);

        double score      = 0.0;
        double confidence = 55.0;

        double volRatio = CalculateVolatilityRatio(prices);
        double microVel = prices.Length >= 5
            ? (prices[^1] - prices[^5]) / Math.Max(1e-8, prices[^5]) * 10_000.0
            : 0.0;

        // Detect dead flat market (range < 35% of ATR over 10 bars)
        bool isDeadMarket = false;
        if (prices.Length >= 10 && atrVal > 1e-9)
        {
            double minP = double.MaxValue, maxP = double.MinValue;
            for (int i = prices.Length - 10; i < prices.Length; i++)
            {
                if (prices[i] < minP) minP = prices[i];
                if (prices[i] > maxP) maxP = prices[i];
            }
            if ((maxP - minP) < (atrVal * 0.35))
                isDeadMarket = true;
        }

        double velContrib     = 0.0;
        double hmaContrib     = 0.0;
        double rsiContrib     = 0.0;
        double connorsContrib = 0.0;
        string regime         = "UNKNOWN";

        if (isSubMinute)
        {
            bool isChaos = volRatio > 1.8;
            bool isStrongMomentum = Math.Abs(microVel) >= 4.0;
            bool isDecelerating = prices.Length >= 4 && Math.Abs(prices[^1] - prices[^2]) < Math.Abs(prices[^2] - prices[^3]);

            if (isDeadMarket)
            {
                regime = "DEAD_FLAT";
                score = 0.0;
                confidence = 50.0;
            }
            else if (isChaos)
            {
                regime = "CHAOS_EXPANSION";
                hmaContrib = 0.0;
                velContrib = Math.Clamp(microVel / 50.0, -0.20, 0.20);
                if (rsi > 70.0) rsiContrib = -0.40;
                else if (rsi < 30.0) rsiContrib = 0.40;

                score = velContrib + rsiContrib;
                confidence = 55.0 + Math.Min(Math.Abs(microVel) * 1.0, 10.0);
            }
            else if (isStrongMomentum)
            {
                if (microVel > 0)
                {
                    regime = "BULLISH_MOMENTUM";
                    hmaContrib = hmaSlope > 0 ? 0.35 : (hmaSlope < 0 ? -0.15 : 0.0);
                    velContrib = Math.Clamp(microVel / 20.0, 0.10, 0.45);

                    if (rsi >= 50.0 && rsi <= 76.0)
                        rsiContrib = 0.20;
                    else if (rsi > 76.0)
                        rsiContrib = (isDecelerating || rsi > 82.0) ? -0.30 : 0.0;
                    else if (rsi < 45.0)
                        rsiContrib = 0.25;

                    connorsContrib = Math.Clamp(((connorsRsi - 50.0) / 50.0) * 0.15, -0.15, 0.15);
                }
                else
                {
                    regime = "BEARISH_MOMENTUM";
                    hmaContrib = hmaSlope < 0 ? -0.35 : (hmaSlope > 0 ? 0.15 : 0.0);
                    velContrib = Math.Clamp(microVel / 20.0, -0.45, -0.10);

                    if (rsi <= 50.0 && rsi >= 24.0)
                        rsiContrib = -0.20;
                    else if (rsi < 24.0)
                        rsiContrib = (isDecelerating || rsi < 18.0) ? 0.30 : 0.0;
                    else if (rsi > 55.0)
                        rsiContrib = -0.25;

                    connorsContrib = Math.Clamp(((connorsRsi - 50.0) / 50.0) * 0.15, -0.15, 0.15);
                }

                score = hmaContrib + velContrib + rsiContrib + connorsContrib;
                confidence = 60.0 + Math.Min(Math.Abs(microVel) * 1.5, 15.0);
                if ((microVel > 0 && hmaSlope > 0) || (microVel < 0 && hmaSlope < 0))
                    confidence += 8.0;
            }
            else
            {
                regime = "RANGING_CHANNEL";
                hmaContrib = hmaSlope > 0 ? 0.20 : (hmaSlope < 0 ? -0.20 : 0.0);
                velContrib = Math.Clamp(microVel / 25.0, -0.20, 0.20);

                if (rsi > 68.0) rsiContrib = -0.40;
                else if (rsi < 32.0) rsiContrib = 0.40;

                connorsContrib = -Math.Clamp(((connorsRsi - 50.0) / 50.0) * 0.15, -0.15, 0.15);

                score = hmaContrib + velContrib + rsiContrib + connorsContrib;
                confidence = 55.0 + (Math.Abs(rsi - 50.0) > 18.0 ? 8.0 : 0.0);
            }
        }
        else
        {
            double trendMultiplier = Math.Clamp((adxVal - 18.0) / 10.0, 0.0, 1.0);
            double rangeMultiplier = 1.0 - trendMultiplier;

            if (volRatio > 1.8)
            {
                regime = "CHAOS_MINUTE";
                trendMultiplier = 0.0;
                rangeMultiplier = 1.5;
            }
            else if (trendMultiplier > 0.5)
            {
                regime = pdiVal > mdiVal ? "BULLISH_TREND_MINUTE" : "BEARISH_TREND_MINUTE";
            }
            else
            {
                regime = "RANGING_MINUTE";
            }

            double rsiOverbought = (adxVal > 30.0) ? 80.0 : ((adxVal < 20.0) ? 65.0 : 70.0);
            double rsiOversold   = (adxVal > 30.0) ? 20.0 : ((adxVal < 20.0) ? 35.0 : 30.0);

            if (rsi > rsiOverbought) rsiContrib = -0.5 * rangeMultiplier;
            else if (rsi < rsiOversold) rsiContrib = 0.5 * rangeMultiplier;

            if (pdiVal > mdiVal) velContrib += 0.6 * trendMultiplier;
            if (mdiVal > pdiVal) velContrib -= 0.6 * trendMultiplier;

            double connorsSignal = (connorsRsi - 50.0) / 50.0;
            connorsContrib = -Math.Clamp(connorsSignal * 0.15, -0.15, 0.15) * rangeMultiplier;

            hmaContrib = (hmaSlope > 0 ? 0.40 : (hmaSlope < 0 ? -0.40 : 0.0)) * trendMultiplier;

            score = rsiContrib + velContrib + connorsContrib + hmaContrib;

            if (adxVal > 25.0)
                confidence += Math.Min((adxVal - 25.0) * 0.8, 20.0);
        }

        double volStrength = 0.0;
        double volContrib  = 0.0;
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
                double rollingCvd = 0;
                int cvdLookback = Math.Min(5, Math.Min(prices.Length - 1, volumes.Length - 1));
                for (int i = 1; i <= cvdLookback; i++)
                {
                    double pc = prices[^i] - prices[^(i + 1)];
                    double v  = volumes[^i];
                    rollingCvd += pc >= 0 ? v : -v;
                }

                double ratio = lastVol / avgVol;
                double cvdNorm = Math.Clamp(rollingCvd / (avgVol * cvdLookback), -1.0, 1.0);
                volStrength = cvdNorm * Math.Max(0.0, Math.Min(ratio - 0.8, 1.0));

                double volBonus = Math.Abs(volStrength) * 10.0;
                if (!isDeadMarket)
                {
                    confidence += Math.Min(volBonus, 10.0);
                    volContrib = Math.Clamp(volStrength * 0.15, -0.20, 0.20);
                    score += volContrib;
                }
            }
        }

        if (!isDeadMarket && (rsi <= 30.0 || rsi >= 70.0))
            confidence += Math.Min(Math.Abs(rsi - 50.0) * 0.3, 5.0);

        if (isDeadMarket)
        {
            score = 0.0;
            confidence = 50.0;
        }

        score = Math.Tanh(score);

        return new TaScoringDetail(
            Score: score,
            Confidence: Math.Clamp(confidence, 50.0, 95.0),
            RsiVal: Math.Round(rsi, 1),
            HmaVal: Math.Round(hma, 5),
            VolStrengthVal: Math.Round(volStrength, 2),
            AtrVal: Math.Round(atrVal, 6),
            HmaSlope: Math.Round(hmaSlope, 6),
            MicroVel: Math.Round(microVel, 2),
            VolRatio: Math.Round(volRatio, 2),
            Regime: regime,
            VelContrib: Math.Round(velContrib, 3),
            HmaContrib: Math.Round(hmaContrib, 3),
            RsiContrib: Math.Round(rsiContrib, 3),
            ConnorsContrib: Math.Round(connorsContrib, 3),
            VolStrengthContrib: Math.Round(volContrib, 3)
        );
    }

    public record GatekeeperResult(bool IsTradeable, string Reason, double Atr, double Adx);

    public GatekeeperResult ValidateMarketGatekeeper(string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default)
    {
        if (prices.Length < 15) return new GatekeeperResult(false, "Недостаточно данных цены для проверки Gatekeeper", 0, 0);

        // FIX ROOT CAUSE: Shared Cache Poisoning. 
        // Gatekeeper used to pass `candles` (which included the live forming candle) to ComputeAtr.
        // This caused the IndicatorCache to advance its permanent state up to the last closed candle.
        // Later, EvaluateTechnicalIndicatorsAsync passed `closedCandles` (without the live candle) to ComputeAtr.
        // The cache's "live overlay" logic would then apply the last closed candle AGAIN on top of the permanent state,
        // resulting in double-counting the last closed candle and permanently warping ADX and ATR.
        var closedCandles = candles.Length > 1 ? candles.Slice(0, candles.Length - 1) : candles;
        
        // FIX ROOT CAUSE: Shared Cache Poisoning.
        // Gatekeeper MUST NOT use the shared IndicatorCache (ComputeAtr / ComputeTrueAdx).
        // Those methods advance the permanent per-(asset,tf) cache state.
        // If Gatekeeper runs first with `allCandles` (including the unclosed live candle),
        // ScoreTimeframe's subsequent call with `closedCandles` would see unseen=0,
        // skip the rebuild, and double-apply the last closed candle via the "live overlay".
        // Solution: use cache-bypassing Raw methods that compute fresh state each time
        // without touching the shared ConcurrentDictionary entries.
        double atr = closedCandles.Length >= 15
            ? IndicatorCache.ComputeAtrRaw(closedCandles)
            : 0;
        var (adx, _, _) = closedCandles.Length >= 15
            ? IndicatorCache.ComputeAdxRaw(closedCandles)
            : (20.0, 0.0, 0.0);

        double minPrice = double.MaxValue;
        double maxPrice = double.MinValue;
        int startIdx = prices.Length - 15;
        for (int i = startIdx; i < prices.Length; i++)
        {
            if (prices[i] < minPrice) minPrice = prices[i];
            if (prices[i] > maxPrice) maxPrice = prices[i];
        }
        
        double priceRange = maxPrice - minPrice;
        if (priceRange > 1.0) BotLogger.Warn($"[Gatekeeper Debug] min={minPrice}, max={maxPrice}. Last 15 prices: " + string.Join(", ", prices.Slice(prices.Length - 15).ToArray()));
        // Fix: when ATR = 0 (not yet warmed up), fallback to an asset-appropriate
        // minimum pip range so the dead-market check is never silently disabled.
        int zeroRangeCount = 0;
        if (candles.Length >= prices.Length)
        {
            for (int i = startIdx; i < prices.Length; i++)
            {
                if (Math.Abs(candles[i].High - candles[i].Low) < 1e-10) zeroRangeCount++;
            }
        }

        bool isSubMinute = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase); double deadMarketThreshold = (atr > 0) ? (atr * 0.05) : (asset.Contains("JPY") ? 0.001 : 0.00001); int maxZeroCandles = isSubMinute ? 14 : 10;
        if (priceRange < deadMarketThreshold || zeroRangeCount >= maxZeroCandles)
        {
            BotLogger.Warn($"[Gatekeeper] Market is completely flat / frozen. PriceRange={priceRange}, ZeroRangeCandles={zeroRangeCount}/15. Aborting analysis.");
            return new GatekeeperResult(false, "⚠️ Рынок в состоянии застоя (пустые или нулевые свечи).\n\nВозможные причины: нет живых тиков от Tiingo WebSocket для данной пары, рынок закрыт, или недостаточно данных для субминутного таймфрейма. Попробуйте позже или выберите другую пару.", atr, adx);
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

        if (longVol < 1e-5) return 1.0;
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
