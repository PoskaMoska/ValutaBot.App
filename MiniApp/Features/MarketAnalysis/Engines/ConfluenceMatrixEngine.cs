using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public record ConfluenceMatrixResult(
    double ConfluenceRatio,
    bool IsGoldenSetup,
    int ProbabilityBoost,
    string ConfluenceLabel,
    string SummaryReasoning,
    Dictionary<string, string> TimeframeDirections,
    string DominantDirection              // "BUY" | "PUT" | "NEUTRAL"
);

public class ConfluenceMatrixEngine(
    MarketDataFetcher fetcher,
    IMarketAnalyzer marketAnalyzer,
    Microsoft.Extensions.Options.IOptions<TradingBotSettings>? options = null) : IConfluenceMatrixEngine
{
    // в”Ђв”Ђ 4D Matrix в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    public async Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null)
    {
        var (microTf, primaryTf, macroTf) = Resolve3DTimeframes(primaryTimeframe);

        try
        {
            var microTask   = fetcher.FetchBinanceWithFallback(binanceSymbol, microTf,   asset, 40);
            var primaryTask = fetcher.FetchBinanceWithFallback(binanceSymbol, primaryTf, asset, 40);
            var macroTask   = fetcher.FetchBinanceWithFallback(binanceSymbol, macroTf,   asset, 40);

            await Task.WhenAll(microTask, primaryTask, macroTask);

            var (microPrices,   microVolumes)   = await microTask;
            var (primaryPrices, primaryVolumes) = await primaryTask;
            var (macroPrices,   macroVolumes)   = await macroTask;

            string dirMicro   = ScoreDirection(microPrices,   microVolumes, microTf, asset);
            string dirPrimary = ScoreDirection(primaryPrices, primaryVolumes, primaryTf, asset);
            string dirMacro   = ScoreDirection(macroPrices,   macroVolumes, macroTf, asset);

            var tfDirs = new Dictionary<string, string>
            {
                [microTf.ToUpper()]   = dirMicro,
                [primaryTf.ToUpper()] = dirPrimary,
                [macroTf.ToUpper()]   = dirMacro,
            };

            var counts    = tfDirs.Values.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
            int buyCount  = counts.GetValueOrDefault("BUY", 0);
            int putCount  = counts.GetValueOrDefault("PUT", 0);
            int maxAgree  = Math.Max(buyCount, putCount);

            double confluenceRatio = Math.Round(maxAgree / 3.0, 2);
            string dominantDir     = buyCount == putCount ? "NEUTRAL"
                                   : buyCount > putCount ? "BUY" : "PUT";
            bool isGoldenSetup     = confluenceRatio >= 0.99;

            int boost = confluenceRatio switch
            {
                >= 0.99 => 12,
                >= 0.65 => 6,
                _       => 0
            };

            string label = confluenceRatio switch
            {
                >= 0.99 => "\u2b50 ⭐ ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "\u26a1 ⚡ СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "\ud83d\udcca СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };

            string summary = $"\u2022 \U0001f3af 3D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label}";

            BotLogger.Info($"[Confluence 3D] {asset} | Ratio: {confluenceRatio * 100}% ({maxAgree}/3 {dominantDir}) | Boost: +{boost}% | Golden: {isGoldenSetup}");

            return new ConfluenceMatrixResult(
                ConfluenceRatio:      confluenceRatio,
                IsGoldenSetup:        isGoldenSetup,
                ProbabilityBoost:     boost,
                ConfluenceLabel:      label,
                SummaryReasoning:     summary,
                TimeframeDirections:  tfDirs,
                DominantDirection:    dominantDir
            );
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[Confluence 3D] Error evaluating matrix for {asset}", ex);
            return new ConfluenceMatrixResult(
                ConfluenceRatio: 0.0,
                IsGoldenSetup: false,
                ProbabilityBoost: 0,
                ConfluenceLabel: "⚠️ 3D Matrix Unavailable",
                SummaryReasoning: "MTF sync failed due to rate limits",
                TimeframeDirections: new Dictionary<string, string>(),
                DominantDirection: "NEUTRAL"
            );
        }
    }

        private static (string micro, string primary, string macro)
        Resolve3DTimeframes(string tf) =>
        tf.ToLower() switch
        {
            // Sub-minute fixes: The horizon is 5 candles.
            // Micro captures 1-2 candles, Primary is the timeframe itself, Macro captures 3x-5x the horizon.
            "s5"                                     => ("s5",  "s15", "m1"),
            "s10"                                    => ("s5",  "s10", "s30"),
            "s15"                                    => ("s5",  "s15", "m1"),
            "s30"                                    => ("s15", "s30", "m1"),
            "m1"                                     => ("s30", "m1",  "m5"),
            "m2" or "m3"                             => ("m1",  "m3",  "m15"),
            "m5"                                     => ("m1",  "m5",  "m15"),
            "m15"                                    => ("m5",  "m15", "h1"),
            _                                        => ("s30", "m1",  "m5")
        };

    /// <summary>
    /// Scores directional bias for a single timeframe using the full
    /// TechnicalAnalysisEngine pipeline (HMA, ConnorsRSI, ADX, Volume).
    ///
    /// FIX: Previously passed candles=null to ScoreTimeframe, which caused
    /// candles.Length == 0 &lt; 14 в†’ always return score=0.0 в†’ always "NEUTRAL".
    /// Now constructs a real OhlcCandle[] from price/volume arrays.
    /// </summary>
    private string ScoreDirection(double[] prices, double[] volumes, string tf, string asset = "global")
    {
        if (prices == null || prices.Length < 10) 
        {
            throw new Exception($"ОТКАЗ API: Получено {(prices == null ? 0 : prices.Length)} свечей для матрицы (нужно мин 10).");
        }

        double avgDiff = 0;
        if (prices.Length > 1) {
            for (int k = 1; k < prices.Length; k++) avgDiff += Math.Abs(prices[k] - prices[k - 1]);
            avgDiff /= (prices.Length - 1);
        }
        if (avgDiff == 0) avgDiff = prices[0] * 0.0001;

        // ArrayPool: вместо new OhlcCandle[n] (4 аллокации на запрос) берём буфер из пула.
        var candles = ArrayPool<MiniAppController.OhlcCandle>.Shared.Rent(prices.Length);
        try
        {
            // FIX C-2: Use the correct timeframe step for synthetic timestamps.
            // Previously AddMinutes(i) always used 1-minute steps, making H1 candles
            // appear to span 40 minutes instead of 40 hours — invalidating all time-based indicators.
            int tfSeconds = tf.ToLower() switch
            {
                "s3"  => 3,  "s5"  => 5,  "s10" => 10, "s15" => 15, "s30" => 30,
                "m1"  => 60, "m2"  => 120, "m3" => 180, "m5" => 300,
                "m15" => 900, "m30" => 1800,
                "h1"  => 3600, "h4" => 14400, "d1" => 86400,
                _ => 60
            };
            var baseTime = DateTime.UtcNow.AddSeconds(-(long)(prices.Length - 1) * tfSeconds);
            for (int i = 0; i < prices.Length; i++)
            {
                double v = volumes != null && i < volumes.Length ? volumes[i] : 1.0;
                double open = i > 0 ? prices[i - 1] : prices[i];
                double close = prices[i];
                double high = Math.Max(open, close) + avgDiff * 0.5;
                double low = Math.Min(open, close) - avgDiff * 0.5;

                // OhlcCandle is a positional record: (Open, High, Low, Close, Volume, Timestamp)
                candles[i] = new MiniAppController.OhlcCandle(
                    open, high, low, close,
                    v,
                    baseTime.AddSeconds((long)i * tfSeconds)
                );
            }

            // FIX ROOT CAUSE #3: Include asset in cache key so different assets don't share
            // indicator state inside ConfluenceMatrix. Previously "4dmatrix_{tf}" was the same
            // for EUR/USD and GBP/USD analysed concurrently → cross-asset RSI/HMA bleeding.
            var (score, _, _, _, _, _) = marketAnalyzer.ScoreTimeframe(
                $"4dmatrix_{asset}_{tf}", tf, prices,
                volumes: volumes,
                candles: candles.AsSpan(0, prices.Length)
            );

            // Порог поднят с ±0.10 до ±0.20: при шкале [-1, +1] прежний порог 0.10
            // классифицировал ~80% шумового рынка как направленный сигнал (BUY/PUT).
            return score > 0.20 ? "BUY" : score < -0.20 ? "PUT" : "NEUTRAL";
        }
        finally
        {
            ArrayPool<MiniAppController.OhlcCandle>.Shared.Return(candles);
        }
    }


    // FIX PRIORITY-4: Скоринг направления на основе реальных OhlcCandle[] (из Orchestrator'а).
    // В отличие от ScoreDirection (который строил OHLC синтетически из avgDiff±0.5),
    // этот метод передаёт реальные High/Low свечей → ATR/ADX корректны → нет шума ±12%.
    private string ScoreDirectionFromCandles(
        MiniAppController.OhlcCandle[] ohlcCandles,
        double[] prices,
        double[] volumes,
        string tf,
        string asset = "global")
    {
        if (prices == null || prices.Length < 10 || ohlcCandles == null || ohlcCandles.Length < 10)
        {
            BotLogger.Info($"[Confluence 3D] Not enough real OHLC candles for {tf} ({prices?.Length ?? 0}) — returning NEUTRAL.");
            return "NEUTRAL";
        }

        try
        {
            // Передаём реальные OhlcCandle[] (с настоящими High/Low) напрямую в ScoreTimeframe
            // FIX ROOT CAUSE #3: Include asset in cache key for per-asset isolation
            var (score, _, _, _, _, _) = marketAnalyzer.ScoreTimeframe(
                $"4dmatrix_{asset}_{tf}", tf, prices,
                volumes: volumes,
                candles: ohlcCandles.AsSpan()
            );

            // Порог ±0.20: при шкале [-1, +1] отсекает рыночный шум
            return score > 0.20 ? "BUY" : score < -0.20 ? "PUT" : "NEUTRAL";
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[Confluence 3D] ScoreDirectionFromCandles failed for {tf}: {ex.Message}");
            return "NEUTRAL";
        }
    }


    // FIX PRIORITY-1: Перегрузка принимает уже загруженные primary+macro свечи из Orchestrator'а.
    // Только microTF требует отдельного fetch (1 HTTP-запрос вместо 3).
    // Это устраняет главную причину нестабильности: TwelveData rate limit (7 req/min).
    public async Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null,
        MiniAppController.OhlcCandle[]? primaryCandles = null,
        double[]? primaryPrices = null,
        double[]? primaryVolumes = null,
        MiniAppController.OhlcCandle[]? macroCandles = null,
        double[]? macroPrices = null,
        double[]? macroVolumes = null)
    {
        // Если pre-loaded данные не переданы — откат на старый метод с тремя fetch
        if (primaryCandles == null || primaryPrices == null ||
            primaryCandles.Length < 10 || primaryPrices.Length < 10 ||
            macroCandles == null || macroPrices == null ||
            macroCandles.Length < 10 || macroPrices.Length < 10)
        {
            BotLogger.Info($"[Confluence 3D] Pre-loaded candles missing or too short for {asset}/{primaryTimeframe} — falling back to 3-fetch mode.");
            return await Evaluate4DMatrixAsync(asset, primaryTimeframe, isForex, binanceSymbol);
        }

        var (microTf, primaryTf, macroTf) = Resolve3DTimeframes(primaryTimeframe);

        try
        {
            // FIX: Только 1 fetch вместо 3 — только microTF получаем по HTTP.
            // Primary и macro уже загружены Orchestrator'ом.
            // Используем limit=50 (синхронизировано с основным запросом, было 40 — разный ключ кэша).
            var (microPricesRaw, microVolumesRaw) = await fetcher.FetchBinanceWithFallback(
                binanceSymbol, microTf, asset, 50);

            string dirMicro   = ScoreDirection(microPricesRaw, microVolumesRaw, microTf, asset);

            // FIX PRIORITY-4: Используем реальный OHLC вместо синтетического avgDiff±0.5
            string dirPrimary = ScoreDirectionFromCandles(
                primaryCandles, primaryPrices, primaryVolumes ?? Array.Empty<double>(), primaryTf, asset);
            string dirMacro   = ScoreDirectionFromCandles(
                macroCandles, macroPrices, macroVolumes ?? Array.Empty<double>(), macroTf, asset);


            var tfDirs = new Dictionary<string, string>
            {
                [microTf.ToUpper()]   = dirMicro,
                [primaryTf.ToUpper()] = dirPrimary,
                [macroTf.ToUpper()]   = dirMacro,
            };

            var counts    = tfDirs.Values.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
            int buyCount  = counts.GetValueOrDefault("BUY", 0);
            int putCount  = counts.GetValueOrDefault("PUT", 0);
            int maxAgree  = Math.Max(buyCount, putCount);

            double confluenceRatio = Math.Round(maxAgree / 3.0, 2);
            string dominantDir     = buyCount == putCount ? "NEUTRAL"
                                   : buyCount > putCount ? "BUY" : "PUT";
            bool isGoldenSetup     = confluenceRatio >= 0.99;

            int boost = confluenceRatio switch
            {
                >= 0.99 => 12,
                >= 0.65 => 6,
                _       => 0
            };

            string label = confluenceRatio switch
            {
                >= 0.99 => "\u2b50 ⭐ ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "\u26a1 ⚡ СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "\ud83d\udcca СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };

            string summary = $"\u2022 \U0001f3af 3D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label} [1-fetch]";

            BotLogger.Info($"[Confluence 3D] {asset}/{primaryTimeframe} | Ratio: {confluenceRatio * 100}% ({maxAgree}/3 {dominantDir}) | Boost: +{boost}% | Golden: {isGoldenSetup} | Saved 2 API calls");

            return new ConfluenceMatrixResult(
                ConfluenceRatio:      confluenceRatio,
                IsGoldenSetup:        isGoldenSetup,
                ProbabilityBoost:     boost,
                ConfluenceLabel:      label,
                SummaryReasoning:     summary,
                TimeframeDirections:  tfDirs,
                DominantDirection:    dominantDir
            );
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[Confluence 3D] Error in 1-fetch mode for {asset}", ex);
            // Откат на 3-fetch режим при ошибке
            return await Evaluate4DMatrixAsync(asset, primaryTimeframe, isForex, binanceSymbol);
        }
    }

    // в”Ђв”Ђ Unified Matrix Evaluation в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    /// <summary>
    /// Merges TA, SMC, Orderflow, ML, and Multi-Timeframe into a final decision.
    /// </summary>
    public async Task<ConsensusDecision> EvaluateMatrixAsync(
        string asset,
        string timeframe,
        bool isSubMinute,
        double conflictPenalty,
        TaSignal taSignal,
        SmcSignal smcSignal,
        OrderflowSignal ofSignal,
        MlSignal mlSignal,
        StateSignal stateSignal,
        ConfluenceMatrixResult mtfResult, int consecutiveLosses = 0, double volRatio = 1.0)
    {
        double totalScore      = 0.0;
        double totalConfidence = 0.0;
        double totalWeight     = 0.0;

        // 1. Technical Analysis (Lagging — Оценка Индикаторов)
        double taScoreOverride = taSignal.Score;
        
        // Умный фильтр тренда (Trend Filter): блокировка RSI в трендовых пробоях
        if (stateSignal.Regime == "HYPER_ACCELERATING_UP" && taScoreOverride < 0)
        {
            BotLogger.Warn($"[TrendFilter] Blocking TA SHORT signal (Score {taScoreOverride}) because market is HYPER_ACCELERATING_UP.");
            taScoreOverride = 0.0;
        }
        else if (stateSignal.Regime == "HYPER_ACCELERATING_DOWN" && taScoreOverride > 0)
        {
            BotLogger.Warn($"[TrendFilter] Blocking TA BUY signal (Score {taScoreOverride}) because market is HYPER_ACCELERATING_DOWN.");
            taScoreOverride = 0.0;
        }

        double taWeight  = await SignalTracker.GetSignalWeightAsync("INDICATORS", 0.8);
        totalScore      += taScoreOverride * taWeight;
        totalConfidence += taSignal.Confidence * taWeight;
        totalWeight     += taWeight;

        // 1b. Order Flow — only applied when not OTC (OTC tick volume ≠ real market pressure)
        // If disabled (OTC), ScoreContribution=0 — skip adding to totalWeight to avoid diluting other signals.
        double ofWeight  = await SignalTracker.GetSignalWeightAsync("ORDERFLOW", 1.2);
        if (Math.Abs(ofSignal.ScoreContribution) > 0)
        {
            totalScore      += ofSignal.ScoreContribution * ofWeight;
            totalConfidence += 65.0 * ofWeight;
            totalWeight     += ofWeight;
        }

        // 2. Velocity / Continuous State (Leading — микро-ускорение цены)
        // AUDIT FIX: только включаем в totalWeight если contribution ненулевой (>= 0.03).
        // При STABLE (contribution=0) добавление stateWeight в знаменатель лишь разбавляет TA и OF.
        double stateWeight  = await SignalTracker.GetSignalWeightAsync("VelocityState", 1.0);
        if (Math.Abs(stateSignal.MomentumContribution) >= 0.03)
        {
            totalScore         += stateSignal.MomentumContribution * stateWeight;
            totalConfidence    += 55.0 * stateWeight;
            totalWeight        += stateWeight;
        }
        else
        {
            BotLogger.Info($"[State] MomentumContribution={stateSignal.MomentumContribution:F3} < 0.03 (STABLE) — skipping VelocityState weight.");
        }

        // 3. SMC (Smart Money Concepts) - adaptive weights based on ADX regime
        double smcTrendScore     = 0.0;
        double smcReversionScore = 0.0;

        // FIX BUG-1: StatefulSmc returns "BULLISH_BOS"/"BEARISH_BOS", not "BULLISH"/"BEARISH".
        // Previously this comparison NEVER matched -> BOS contributed 0 points always.
        if (smcSignal.BosDirection == "BULLISH_BOS")  smcTrendScore += 2.0;
        if (smcSignal.BosDirection == "BEARISH_BOS")  smcTrendScore -= 2.0;
        if (!string.IsNullOrEmpty(smcSignal.OrderBlockType) && smcSignal.OrderBlockType != "NONE")
            smcTrendScore += smcSignal.OrderBlockType.Contains("BULL") ? 1.0 : -1.0;
        if (!string.IsNullOrEmpty(smcSignal.FvgType) && smcSignal.FvgType != "NONE")
            smcTrendScore += smcSignal.FvgType.Contains("BULL") ? 1.0 : -1.0;
        if (smcSignal.SweepDirection == "BULLISH_SWEEP") smcReversionScore += 2.0;
        if (smcSignal.SweepDirection == "BEARISH_SWEEP") smcReversionScore -= 2.0;

        double trendWeight     = 1.0;
        double reversionWeight = 1.0;

        // FIX: Continuous SMC Regime Blending
        double smcTrendMultiplier = Math.Clamp((taSignal.Adx - 18.0) / 10.0, 0.0, 1.0); // 18->0%, 28->100%
        double smcRangeMultiplier = 1.0 - smcTrendMultiplier;
        
        trendWeight     = 1.5 * smcTrendMultiplier;
        reversionWeight = 2.0 * smcRangeMultiplier;

        double finalSmcScore = (smcTrendScore * trendWeight) + (smcReversionScore * reversionWeight);

        // FIX: Consecutive losses should ONLY reduce the final SMC confidence/score.
        // It must NOT invert the trading style (forcing reversion in a trend), 
        // which was causing the "death spiral" loss clusters.
        if (consecutiveLosses >= 2)
        {
            double lossPenalty = Math.Max(0.5, 1.0 - (consecutiveLosses - 1) * 0.15);
            BotLogger.Warn($"[SMC Penalty] {asset}/{timeframe}: {consecutiveLosses} losses. Suppressing SMC score by {lossPenalty:F2}x");
            finalSmcScore *= lossPenalty;
        }

        if (Math.Abs(finalSmcScore) > 0.1 && !isSubMinute)
        {
            // FIX W-20: dynamic normalization — max score depends on active weights
            // AUDIT FIX: SMC полностью отключён на sub-minute (s5/s10/s15/s30).
            // BOS, FVG, OrderBlock — институциональные концепции для H1/H4/D1.
            // На 5-секундных свечах это статистический шум, загрязняющий скоринг.
            double maxPossibleSmc = (trendWeight * 4.0) + (reversionWeight * 2.0);
            double normSmcScore   = maxPossibleSmc > 0 ? finalSmcScore / maxPossibleSmc : 0;

            double smcWeight   = await SignalTracker.GetSignalWeightAsync("SMC", 1.5);
            totalScore        += normSmcScore * smcWeight;
            totalConfidence   += 60.0 * smcWeight;
            totalWeight       += smcWeight;
        }
        else if (isSubMinute)
        {
            BotLogger.Info($"[SMC] Sub-minute timeframe — SMC scoring disabled (institutional concepts not valid on {timeframe}).");
        }

        // Normalize internal base scores
        if (totalWeight > 0)
        {
            totalScore      /= totalWeight;
            totalConfidence /= totalWeight;
        }

        // Apply conflict penalty globally to the normalized score
        totalScore *= conflictPenalty;

        // FIX PRIORITY-5: AutoCalibrationEngine мультипликатор применяется ТОЛЬКО к TA-компоненту.
        // Ранее он применялся ко всему totalScore ПОСЛЕ нормализации — это создавало feedback loop:
        // серия потерь → мультипликатор < 1 → весь score сжимается → больше NEUTRAL → нет данных
        // для восстановления → мультипликатор не растёт → замкнутый круг.
        // Теперь: мы масштабируем только вклад TA (taScoreOverride уже добавлен в totalScore через
        // taWeight, поэтому корректируем постфактум как добавочный delta-term).
        if (TradeOutcomeTracker.CalibrationEngine is AutoCalibrationEngine calibEngine)
        {
            var regime = calibEngine.DetectMarketRegime(taSignal.Adx, volRatio, taSignal.Rsi);
            // Мультипликатор для TA-источника (не ENSEMBLE — чтобы изолировать влияние)
            double regimeMultiplier = calibEngine.GetCalibratedRegimeWeight("SKENDER_MATH", asset, timeframe, regime);
            
            // Упрощение: масштабируем в узком диапазоне [0.8, 1.2] — не инвертирует сигнал
            double scaledMultiplier = Math.Clamp(regimeMultiplier, 0.8, 1.2);
            // Применяем только к TA-части (пропорционально её весу в финальном score)
            double taFraction = totalWeight > 0 ? (taWeight / totalWeight) : 0.5;
            totalScore = totalScore * (1.0 + (scaledMultiplier - 1.0) * taFraction);
            
            BotLogger.Info($"[AutoCalib] Regime={regime}, Multiplier={regimeMultiplier:F2}x → scaled={scaledMultiplier:F2}x, taFraction={taFraction:F2}, adjustedScore={totalScore:F3}");
        }

        // AUDIT FIX: FearGreed — добавляем контрарный вклад для крипто-пар.
        // FearGreedService существовал, но нигде не вызывался в матрице решений.
        // Только для крипто (isForex=false); для forex возвращает contribution=0.0.
        // Максимальный вклад ±0.08 (масштабированный с оригинального ±0.12).
        bool isForexAsset = !asset.Contains("BTC") && !asset.Contains("ETH") && !asset.Contains("SOL")
                         && !asset.Contains("XRP") && !asset.Contains("BNB");
        try
        {
            var fg = await ValutaBot.App.MiniApp.Services.FearGreedService.GetAsync(isForexAsset);
            if (Math.Abs(fg.ScoreContribution) > 0.01)
            {
                // Масштабируем до ±0.08 максимум чтобы не доминировать над основными сигналами
                double fgContrib = Math.Clamp(fg.ScoreContribution * 0.67, -0.08, 0.08);
                totalScore += fgContrib;
                BotLogger.Info($"[FearGreed] Zone={fg.Zone}, Contrib={fg.ScoreContribution:+0.00;-0.00} → applied={fgContrib:+0.00;-0.00}");
            }
        }
        catch (Exception fgEx)
        {
            BotLogger.Info($"[FearGreed] Skipped: {fgEx.Message}");
        }

        // 4. ML / Mathematical Consensus Matrix Layer (META-LABELING OVERRIDE)
        // FIX C-13: totalScore is already normalized to [-1, 1] after /totalWeight.
        // Old code was Clamp(-2.5, 2.5)/2.5 — Clamp never triggered (dead code),
        // and dividing by 2.5 made math weight effectively ~23% instead of 40%.
        double scoreMath = Math.Clamp(totalScore, -1.0, 1.0);

        bool   isMlActive           = (mlSignal.Direction == "BUY" || mlSignal.Direction == "PUT");
        double finalConfidenceScore  = scoreMath;
        string candidateDir          = "NEUTRAL";

        if (isMlActive)
        {
            // True Ensemble: both scoreMath and mlScore are now in [-1, 1]
            // so the declared mlWeight/mathWeight ratio is actually honoured.
            double normLgbm = Math.Max(0, (mlSignal.Confidence - 0.5) * 2.0);
            double mlScore  = mlSignal.Direction == "BUY" ? normLgbm : -normLgbm;

            double mlWeight   = options?.Value.MlWeight   ?? 0.5;
            double mathWeight = options?.Value.MathWeight ?? 0.5;

            // FIX C-12 (Revised): Dynamic contradiction resolution.
            if (Math.Sign(mlScore) != Math.Sign(scoreMath))
            {
                if (mlSignal.Confidence >= 0.75)
                {
                    // ML is highly confident (>75%). It usually detects a breakout that Math 
                    // interprets purely as "overbought/oversold" in a range. Protect ML.
                    mlWeight   *= 1.2;
                    mathWeight *= 0.8;
                }
                else if (Math.Abs(scoreMath) > 0.3)
                {
                    // Standard contradiction: ML is uncertain, Math has a clear structure.
                    mlWeight   *= 0.6;
                    mathWeight *= 1.4;
                }
            }

            finalConfidenceScore = (mlScore * mlWeight) + (scoreMath * mathWeight);
        }

        // Dead-zone: near-zero (±0.01). Bot always gives a directional signal.
        // NEUTRAL only when score is truly zero (no market data bias at all).
        // User decides whether to act on low-confidence signals.
        candidateDir = finalConfidenceScore > 0.01 ? "BUY" : finalConfidenceScore < -0.01 ? "PUT" : "NEUTRAL";

        // 5. Final Decision & Market Session Awareness
        double absWeightedScore = Math.Abs(finalConfidenceScore);
        
        // Внедрение интеллекта сессий (Market Session Modifier)
        // Бот осознает время суток и снижает вероятность в тихие/опасные периоды, 
        // тем самым отсекая выдачу ложных "Golden Setups", когда ликвидности нет.
        double sessionMultiplier = 1.0;
        string sessionName = "DEFAULT";
        bool isOtcAsset = asset.Contains("OTC", StringComparison.OrdinalIgnoreCase);
        if (!asset.Contains("BTC") && !asset.Contains("ETH") && !asset.Contains("SOL") && !isOtcAsset)
        {
            // Session modifier applies only to live weekday forex/crypto.
            // OTC pairs trade from historical DB — no real sessions, no dead zones.
            int h = DateTime.UtcNow.Hour;
            if (h >= 21 || h < 2) { sessionMultiplier = 0.75; sessionName = "DEAD_ZONE"; } // Поздний вечер (расширение спредов, мертвый рынок)
            else if (h >= 2 && h < 8) { sessionMultiplier = 0.85; sessionName = "ASIAN"; } // Азия (низкая волатильность, пила)
            else if (h >= 8 && h < 13) { sessionMultiplier = 1.0; sessionName = "LONDON_MORNING"; } // Лондон
            else if (h >= 13 && h < 17) { sessionMultiplier = 1.1; sessionName = "LONDON_NY_OVERLAP"; } // Макс. ликвидность (супер-тренды)
            else if (h >= 17 && h < 21) { sessionMultiplier = 1.0; sessionName = "NY_AFTERNOON"; } // Нью-Йорк вечер
        }
        else if (isOtcAsset)
        {
            sessionName = "OTC_WEEKEND";
        }
        
        absWeightedScore *= sessionMultiplier;
        
        int probability = isSubMinute
            ? Math.Clamp(50 + (int)Math.Round(absWeightedScore * 40), 50, 91)
            : Math.Clamp(50 + (int)Math.Round(absWeightedScore * 45), 50, 95);

        if (sessionMultiplier < 1.0)
        {
            BotLogger.Info($"[MarketSession] {sessionName} detected. Multiplier={sessionMultiplier}. Lowering probability.");
        }
        else if (sessionMultiplier > 1.0)
        {
            BotLogger.Info($"[MarketSession] {sessionName} detected. High liquidity! Multiplier={sessionMultiplier}.");
        }

        // MTF Golden Boost — only when 4D dominant direction EXPLICITLY matches candidateDir.
        // FIX W-16: removed || "NEUTRAL" condition — neutral MTF must not boost confidence.
        if (candidateDir != "NEUTRAL"
            && mtfResult.ProbabilityBoost > 0
            && mtfResult.DominantDirection == candidateDir)
        {
            probability = Math.Clamp(probability + mtfResult.ProbabilityBoost, 55, 95);
        }

        // Probability filter removed: bot always gives a signal.
        // User sees the probability % and decides whether to trade.
        // Low probability signals are shown as-is with their confidence level.

        // 6. Reasoning text
        string modelAccText = mlSignal.Accuracy.HasValue
            ? $" [Точность: {Math.Round(mlSignal.Accuracy.Value * 100, 1)}%]"
            : "";

        string smcText = !string.IsNullOrEmpty(smcSignal.Reasoning)
            ? $"\u2022 \U0001f6e1\ufe0f SMC Структура: {smcSignal.Reasoning}"
            : "\u2022 \U0001f6e1\ufe0f SMC Структура: недостаточно данных";

        string flowText = !string.IsNullOrEmpty(ofSignal.Description)
            ? $"\u2022 \U0001f30a Order Flow & CVD: {ofSignal.Description}"
            : "\u2022 \U0001f30a Order Flow & CVD: нет выраженных объемов";

        string lgbmText = !string.IsNullOrEmpty(mlSignal.Direction) && mlSignal.Direction != "NEUTRAL"
            ? $"\u2022 \u26a1 Нейросеть (LightGBM): {(mlSignal.Direction == "BUY" ? "ВВЕРХ \u2b06" : "ВНИЗ \u2b07")} ({Math.Round(mlSignal.Confidence * 100)}% уверенности){modelAccText}"
            : (mlSignal.ModelVersion == "disabled"
                ? $"\u2022 \u26a1 Нейросеть (LightGBM): Отключена пользователем"
                : mlSignal.ModelVersion == "forex-only"
                    ? $"\u2022 \u26a1 Нейросеть (LightGBM): Недоступно для крипто"
                    : mlSignal.ModelVersion == "not-trained"
                        ? $"\u2022 \u26a1 Нейросеть (LightGBM): Модель обучается (зайдет через пару минут)"
                        : mlSignal.ModelVersion == "offline"
                            ? $"\u2022 \u26a1 Нейросеть (LightGBM): Сервис недоступен (Оффлайн)"
                            : $"\u2022 \u26a1 Нейросеть (LightGBM): НЕЙТРАЛЬНО (0% уверенности){modelAccText}");

        string combinedReasoning = $"{smcText}\n{flowText}\n{lgbmText}";

        return new ConsensusDecision(candidateDir, candidateDir, probability, combinedReasoning, totalScore);
    }

}




