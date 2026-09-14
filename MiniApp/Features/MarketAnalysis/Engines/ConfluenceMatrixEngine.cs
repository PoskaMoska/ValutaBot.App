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
    IMarketAnalyzer marketAnalyzer) : IConfluenceMatrixEngine
{
    // 4D Matrix
    
    public async Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null)
    {
        var (microTf, primaryTf, macroTf) = Resolve3DTimeframes(primaryTimeframe);

        try
        {
            var microTask   = fetcher.FetchOhlcWithFallbackAsync(binanceSymbol, microTf,   asset, 40);
            var primaryTask = fetcher.FetchOhlcWithFallbackAsync(binanceSymbol, primaryTf, asset, 40);
            var macroTask   = fetcher.FetchOhlcWithFallbackAsync(binanceSymbol, macroTf,   asset, 40);

            await Task.WhenAll(microTask, primaryTask, macroTask);

            var microCandles   = await microTask;
            var primaryCandles = await primaryTask;
            var macroCandles   = await macroTask;
            
            // Drop unclosed candles to prevent Train-Serve Skew
            if (microCandles.Length > 1) microCandles = microCandles.Take(microCandles.Length - 1).ToArray();
            if (primaryCandles.Length > 1) primaryCandles = primaryCandles.Take(primaryCandles.Length - 1).ToArray();
            if (macroCandles.Length > 1) macroCandles = macroCandles.Take(macroCandles.Length - 1).ToArray();

            string dirMicro   = ScoreDirectionFromCandles(microCandles, microCandles.Select(c => c.Close).ToArray(), microCandles.Select(c => c.Volume).ToArray(), microTf, asset);
            string dirPrimary = ScoreDirectionFromCandles(primaryCandles, primaryCandles.Select(c => c.Close).ToArray(), primaryCandles.Select(c => c.Volume).ToArray(), primaryTf, asset);
            string dirMacro   = ScoreDirectionFromCandles(macroCandles, macroCandles.Select(c => c.Close).ToArray(), macroCandles.Select(c => c.Volume).ToArray(), macroTf, asset);

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
                >= 0.99 => 15,
                >= 0.65 => 7,
                _       => 0
            };

            string label = confluenceRatio switch
            {
                >= 0.99 => "⭐ ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "⚡ СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "📉 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };

            string summary = $"• 4D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label}";

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
            // FIX PRIORITY-1: Align the 3D Timeframe Matrix with MarketDataFetcher.HigherTf()
            // This is required so the 1-fetch pre-loaded primaryCandles and macroCandles in Evaluate4DMatrixAsync
            // exactly match the primaryTf and macroTf here. Otherwise, the Doppelganger Bug occurs, evaluating e.g. s5 twice.
            "s5"                            => ("s5",  "s10", "m1"),
            "s10"                           => ("s5",  "s10", "m1"),
            "s15"                           => ("s5",  "s15", "m1"),
            "s30"                           => ("s15", "s30", "m1"),
            "m1"                            => ("s30", "m1",  "m5"),
            "m2" or "m3"                    => ("m1",  "m3",  "m15"),
            "m5"                            => ("m1",  "m5",  "m15"),
            "m15"                           => ("m5",  "m15", "h1"),
            "m30"                           => ("m15", "m30", "h1"),
            "h1"                            => ("m30", "h1",  "h4"),
            "h4"                            => ("h1",  "h4",  "d1"),
            "d1"                            => ("h4",  "d1",  "w1"),
            _                               => ("s30", "m1",  "m5")
        };

    /// <summary>
    /// Scores directional bias for a single timeframe using the full
    /// TechnicalAnalysisEngine pipeline (HMA, ConnorsRSI, ADX, Volume).
    ///
    /// FIX: Previously passed candles=null to ScoreTimeframe, which caused
    /// candles.Length == 0 < 14 always return score=0.0 always "NEUTRAL".
    /// Now constructs a real OhlcCandle[] from price/volume arrays.
    /// </summary>
    // В отличие от ScoreDirection (который строго OHLC-синтетичный и дает avgDiff±0.5),
    // этот метод передаёт реальные High/Low свечи → ATR/ADX корректны → нет шума ±12%.
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


    // FIX PRIORITY-1: Перегрузка принимающая уже загруженные current+higher свечи из Orchestrator'а.
    // Умно маппит их на слоты (micro/primary/macro) и делает 1 HTTP-запрос для недостающего таймфрейма.
    // Это устраняет главную причину нестабильности: TwelveData rate limit (7 req/min) и Doppelganger Bug.
    public async Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null,
        MiniAppController.OhlcCandle[]? currentCandles = null,
        double[]? currentPrices = null,
        double[]? currentVolumes = null,
        MiniAppController.OhlcCandle[]? higherCandles = null,
        double[]? higherPrices = null,
        double[]? higherVolumes = null)
    {
        if (currentCandles == null || currentPrices == null ||
            currentCandles.Length < 10 || currentPrices.Length < 10 ||
            higherCandles == null || higherPrices == null ||
            higherCandles.Length < 10 || higherPrices.Length < 10)
        {
            BotLogger.Info($"[Confluence 3D] Pre-loaded candles missing or too short for {asset}/{primaryTimeframe} — falling back to 3-fetch mode.");
            return await Evaluate4DMatrixAsync(asset, primaryTimeframe, isForex, binanceSymbol);
        }

        var (microTf, primaryTf, macroTf) = Resolve3DTimeframes(primaryTimeframe);
        string currentTf = primaryTimeframe.ToLower();
        string hTf = fetcher.HigherTf(currentTf)?.ToLower() ?? "";

        try
        {
            string dirMicro = "NEUTRAL";
            string dirPrimary = "NEUTRAL";
            string dirMacro = "NEUTRAL";

            // Helper to process a TF: either use pre-loaded, or fetch via HTTP
            async Task<string> ProcessTf(string targetTf)
            {
                if (targetTf == currentTf)
                {
                    return ScoreDirectionFromCandles(currentCandles, currentPrices, currentVolumes ?? Array.Empty<double>(), targetTf, asset);
                }
                else if (targetTf == hTf)
                {
                    return ScoreDirectionFromCandles(higherCandles, higherPrices, higherVolumes ?? Array.Empty<double>(), targetTf, asset);
                }
                else
                {
                    // Fetch missing TF
                    var candlesRaw = await fetcher.FetchOhlcWithFallbackAsync(binanceSymbol, targetTf, asset, 50);
                    // Drop the unclosed live candle, just like TA and ML do, to prevent massive indicators skew.
                    if (candlesRaw.Length > 1) {
                        candlesRaw = candlesRaw.Take(candlesRaw.Length - 1).ToArray();
                    }
                    var pricesRaw = candlesRaw.Select(c => c.Close).ToArray();
                    var volsRaw = candlesRaw.Select(c => c.Volume).ToArray();
                    return ScoreDirectionFromCandles(candlesRaw, pricesRaw, volsRaw, targetTf, asset);
                }
            }

            dirMicro = await ProcessTf(microTf);
            dirPrimary = await ProcessTf(primaryTf);
            dirMacro = await ProcessTf(macroTf);

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
                >= 0.99 => 15,
                >= 0.65 => 7,
                _       => 0
            };

            string label = confluenceRatio switch
            {
                >= 0.99 => "⭐ ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "⚡ СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "📉 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };

            string summary = $"• 4D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label} [1-fetch smart]";

            BotLogger.Info($"[Confluence 3D] {asset}/{primaryTimeframe} | Ratio: {confluenceRatio * 100}% ({maxAgree}/3 {dominantDir}) | Boost: +{boost}% | Golden: {isGoldenSetup} | Smart 1-fetch");

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
            return await Evaluate4DMatrixAsync(asset, primaryTimeframe, isForex, binanceSymbol);
        }
    }

    // Unified Matrix Evaluation
    
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

        // 2. Velocity / Continuous State (Leading — микро-ускорения цены)
        // AUDIT FIX: только включаем в totalWeight если contribution ненулевой (>= 0.03).
        // При STABLE (contribution=0) добавление stateWeight в знаменатель лишь разбавляет TA и OF.
        double stateWeight  = await SignalTracker.GetSignalWeightAsync("VelocityState", 1.0);
        if (Math.Abs(stateSignal.MomentumContribution) >= 0.03)
        {
            totalScore        += stateSignal.MomentumContribution * stateWeight;
            totalConfidence   += 55.0 * stateWeight;
            totalWeight       += stateWeight;
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
            // AUDIT FIX: SMC полностью отключен на sub-minute (s5/s10/s15/s30).
            // BOS, FVG, OrderBlock — институциональные концепции для H1/H4.
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

        // FIX PRIORITY-5: AutoCalibrationEngine мультипликатор применялся к TA-компоненту.
        // Ранее он применялся ко всему totalScore ПОСЛЕ нормализации — это создавало feedback loop:
        // серия потерь -> мультипликатор < 1 -> весь score сжимается -> больше NEUTRAL -> нет данных
        // для восстановления -> мультипликатор не растёт -> замкнутый круг.
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

        // AUDIT FIX: FearGreed — добавлена контрарная вкладка для крипто-пар.
        // FearGreedService существовал, но нигде не вызывался в матрице решений.
        // Только для крипто (isForex=false); для forex возвращает contribution=0.0.
        // Максимальный вклад ±0.08 (масштабированный с оригинального ±0.12).
        bool isForexAsset = AssetSanitizer.IsForexAsset(AssetSanitizer.Sanitize(asset));
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

        // 4. ML / Mathematical Consensus Matrix Layer (META-LEARNER OVERRIDE)
        double scoreMath = Math.Clamp(totalScore, -1.0, 1.0);
        
        // Extract raw scores for ML
        double taScoreRaw = taSignal.Score;
        double ofScoreRaw = ofSignal.ScoreContribution;
        double smcScoreRaw = finalSmcScore;
        double mlProbRaw = (mlSignal.Direction == "BUY") ? mlSignal.Confidence : (mlSignal.Direction == "PUT" ? -mlSignal.Confidence : 0);

        // Query the Logistic Regression Meta-Learner
        double metaProb = OnlineMetaLearner.Predict(asset, timeframe, taScoreRaw, ofScoreRaw, smcScoreRaw, mlProbRaw);
        
        string candidateDir = "NEUTRAL";
        double finalConfidenceScore = 0.0;
        
        // 5. Final Decision & Market Session Awareness
        
        // Convert scoreMath [-1.0, 1.0] to a probability [0.0, 1.0]
        double mathProb = (scoreMath + 1.0) / 2.0;
        
        // FIX PRIORITY-1: Rebalanced ML vs Math blend. 
        // Previously (70% ML / 30% Math) gave ML absolute veto power, which is dangerous in Binary Options 
        // if order flow or momentum is strongly against it. Now we enforce a strict 50/50 consensus.
        double blendedProb = (metaProb * 0.50) + (mathProb * 0.50);
        
        // FIX PRIORITY-2: Hardened entry threshold. 
        // 0.52 was only 52%, which is precisely the breakeven line for 92% payout (1 / 1.92 = 52.08%).
        // We raised it to 55% to provide a definitive 3% EV buffer above market noise.
        if (blendedProb >= 0.55)
        {
            candidateDir = "BUY";
            finalConfidenceScore = (blendedProb - 0.5) * 2.0;
        }
        else if (blendedProb <= 0.45)
        {
            candidateDir = "PUT";
            finalConfidenceScore = (0.5 - blendedProb) * 2.0;
        }
        else
        {
            candidateDir = "NEUTRAL";
            finalConfidenceScore = 0.0;
        }

        double absWeightedScore = finalConfidenceScore;
        
        // Apply conflict penalty globally to the final confidence (so MTF conflict actually lowers probability)
        absWeightedScore *= conflictPenalty;
        
        // Внедрение интеллектуального сессионного множителя (Market Session Modifier)
        double sessionMultiplier = 1.0;
        string sessionName = "DEFAULT";
        bool isOtcAsset = asset.Contains("OTC", StringComparison.OrdinalIgnoreCase);
        if (!asset.Contains("BTC") && !asset.Contains("ETH") && !asset.Contains("SOL") && !isOtcAsset)
        {
            int h = DateTime.UtcNow.Hour;
            if (h >= 21 || h < 2) { sessionMultiplier = 0.75; sessionName = "DEAD_ZONE"; }
            else if (h >= 2 && h < 8) { sessionMultiplier = 0.85; sessionName = "ASIAN"; }
            else if (h >= 8 && h < 13) { sessionMultiplier = 1.0; sessionName = "LONDON_MORNING"; }
            else if (h >= 13 && h < 17) { sessionMultiplier = 1.1; sessionName = "LONDON_NY_OVERLAP"; }
            else if (h >= 17 && h < 21) { sessionMultiplier = 1.0; sessionName = "NY_AFTERNOON"; }
        }
        else if (isOtcAsset)
        {
            sessionName = "OTC_WEEKEND";
        }
        
        absWeightedScore *= sessionMultiplier;
        
        int probability = (int)Math.Round(50 + (absWeightedScore * 50));
        if (isSubMinute) probability = Math.Clamp(probability, 50, 91);
        else probability = Math.Clamp(probability, 50, 95);

        if (sessionMultiplier < 1.0)
        {
            BotLogger.Info($"[MarketSession] {sessionName} detected. Multiplier={sessionMultiplier}. Lowering probability.");
        }
        else if (sessionMultiplier > 1.0)
        {
            BotLogger.Info($"[MarketSession] {sessionName} detected. High liquidity! Multiplier={sessionMultiplier}.");
        }

        // MTF Golden Boost — only when 4D dominant direction EXPLICITLY matches candidateDir.
        if (candidateDir != "NEUTRAL"
            && mtfResult.ProbabilityBoost > 0
            && mtfResult.DominantDirection == candidateDir)
        {
            // FIX PRIORITY-3: Bayesian Conditional Probability Shift.
            // Previously, a flat arithmetic +12% artificially inflated weak noise (e.g. 52% + 12% = 64%).
            // Now, we scale the boost by the remaining uncertainty. 
            // Example: Base prob = 55%, Boost = 15%. Shift = (100 - 55) * 0.15 = 6.75%. New Prob = 61.75%.
            double remainingUncertainty = 100.0 - probability;
            double bayesianShift = remainingUncertainty * (mtfResult.ProbabilityBoost / 100.0);
            
            probability = (int)Math.Clamp(Math.Round(probability + bayesianShift), 55, 95);
        }

        // 6. Reasoning text
        string modelAccText = mlSignal.Accuracy.HasValue
            ? $" [Точность: {Math.Round(mlSignal.Accuracy.Value * 100, 1)}%]"
            : "";

        string smcText = !string.IsNullOrEmpty(smcSignal.Reasoning)
            ? $"• 🏛️ SMC Структура: {smcSignal.Reasoning}"
            : "• 🏛️ SMC Структура: недостаточно данных";

        string flowText = !string.IsNullOrEmpty(ofSignal.Description)
            ? $"• 🌊 Order Flow & CVD: {ofSignal.Description}"
            : "• 🌊 Order Flow & CVD: нет выраженных объемов";

        string lgbmText = !string.IsNullOrEmpty(mlSignal.Direction) && mlSignal.Direction != "NEUTRAL"
            ? $"• ⚡ Нейросеть (LightGBM): {(mlSignal.Direction == "BUY" ? "ВВЕРХ ⬆" : "ВНИЗ ⬇")} ({Math.Round(mlSignal.Confidence * 100)}% уверенности){modelAccText}"
            : (mlSignal.ModelVersion == "disabled"
                ? $"• ⚡ Нейросеть (LightGBM): Отключена пользователем"
                : mlSignal.ModelVersion == "forex-only"
                    ? $"• ⚡ Нейросеть (LightGBM): Недоступна для крипты"
                    : mlSignal.ModelVersion == "not-trained"
                        ? $"• ⚡ Нейросеть (LightGBM): Модель обучается (зайдите через пару минут)"
                        : mlSignal.ModelVersion == "offline"
                            ? $"• ⚡ Нейросеть (LightGBM): Сервер недоступен (Оффлайн)"
                            : $"• ⚡ Нейросеть (LightGBM): НЕЙТРАЛЬНО (0% уверенности){modelAccText}");

        string combinedReasoning = $"{smcText}\n{flowText}\n{lgbmText}";

        return new ConsensusDecision(
            candidateDir, candidateDir, probability, combinedReasoning, totalScore, "",
            TaScore: taSignal.Score,
            OfScore: ofSignal.ScoreContribution,
            SmcScore: finalSmcScore,
            MlProb: mlSignal.Direction == "BUY" ? mlSignal.Confidence : (mlSignal.Direction == "PUT" ? -mlSignal.Confidence : 0)
        );
    }

}