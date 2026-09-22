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
    string DominantDirection     // "BUY" | "PUT" | "NEUTRAL"
);

public class ConfluenceMatrixEngine(
    MarketDataFetcher fetcher,
    IMarketAnalyzer marketAnalyzer,
    IAutoCalibrationEngine? autoCalib = null) : IConfluenceMatrixEngine
{
    // 4D Matrix — fetch mode (always makes 3 HTTP requests)
    // Named distinctly from the smart-reuse overload to make fallback behavior explicit.
    // Only called directly when no pre-loaded candles are available.
    private async Task<ConfluenceMatrixResult> Evaluate4DMatrixFetchAsync(
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
                >= 0.99 => "рџ’  РР”Р•РђР›Р¬РќР«Р™ РЎРР“РќРђР› (3 РўР¤ - 100%)",
                >= 0.65 => "рџ’  РЎРР›Р¬РќР«Р™ РЎРР“РќРђР› (2 РўР¤ - 67%)",
                _       => "рџ’  РЎР›РђР‘Р«Р™ РЎРР“РќРђР› (1 РўР¤ - 33%)"
            };

            string summary = $"вЂў 4D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label}";

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
                ConfluenceLabel: "рџ’  3D Matrix Unavailable",
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
        "s5"                      => ("s5",  "s10", "m1"),
        "s10"                     => ("s5",  "s10", "m1"),
        "s15"                     => ("s5",  "s15", "m1"),
        "s30"                     => ("s15", "s30", "m1"),
        "m1"                      => ("s30", "m1",  "m5"),
        "m2" or "m3"              => ("m1",  "m3",  "m15"),
        "m5"                      => ("m1",  "m5",  "m15"),
        "m15"                     => ("m5",  "m15", "h1"),
        "m30"                     => ("m15", "m30", "h1"),
        "h1"                      => ("m30", "h1",  "h4"),
        "h4"                      => ("h1",  "h4",  "d1"),
        "d1"                      => ("h4",  "d1",  "w1"),
        _                         => ("s30", "m1",  "m5")
    };

    /// <summary>
    /// Scores directional bias for a single timeframe using the full
    /// TechnicalAnalysisEngine pipeline (HMA, ConnorsRSI, ADX, Volume).
    /// </summary>
    /// <remarks>
    /// FIX: Previously passed candles=null to ScoreTimeframe, which caused
    /// candles.Length == 0 < 14 always return score=0.0 always "NEUTRAL".
    /// Now constructs a real OhlcCandle[] from price/volume arrays.
    /// </remarks>
    // Р’ РѕС‚Р»РёС‡РёРµ РѕС‚ ScoreDirection (РєРѕС‚РѕСЂРѕРјСѓ РЅСѓР¶РµРЅ С‚РѕР»СЊРєРѕ С†РµРЅС‹ Рё РґР°РµС‚ avgDiffВ±0.5),
    // СЌС‚РѕС‚ РјРµС‚РѕРґ РїРµСЂРµРґР°РµС‚ СЂРµР°Р»СЊРЅС‹Рµ High/Low СЃРІРµС‡Рё -> ATR/ADX РєРѕСЂСЂРµРєС‚РЅС‹ -> РЅРµС‚ С€СѓРјР° В±12%.
    private string ScoreDirectionFromCandles(
        MiniAppController.OhlcCandle[] ohlcCandles,
        double[] prices,
        double[] volumes,
        string tf,
        string asset = "global")
    {
        if (prices == null || prices.Length < 10 || ohlcCandles == null || ohlcCandles.Length < 10)
        {
            BotLogger.Info($"[Confluence 3D] Not enough real OHLC candles for {tf} ({prices?.Length ?? 0}) вЂ” returning NEUTRAL.");
            return "NEUTRAL";
        }

        try
        {
            // РџРµСЂРµРґР°РµРј СЂРµР°Р»СЊРЅС‹Рµ OhlcCandle[] (СЃ РЅР°СЃС‚РѕСЏС‰РёРјРё High/Low) РЅР°РїСЂСЏРјСѓСЋ РІ ScoreTimeframe
            // FIX ROOT CAUSE #3: Include asset in cache key for per-asset isolation
            var (score, _, _, _, _, _) = marketAnalyzer.ScoreTimeframe(
                $"4dmatrix_{asset}_{tf}", tf, prices,
                volumes: volumes,
                candles: ohlcCandles.AsSpan()
            );

            // РџРѕСЂРѕРі В±0.20: РїСЂРё С€РєР°Р»Рµ [-1, +1] РѕС‚СЃРµРєР°РµС‚ СЂС‹РЅРѕС‡РЅС‹Р№ С€СѓРј
            return score > 0.20 ? "BUY" : score < -0.20 ? "PUT" : "NEUTRAL";
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[Confluence 3D] ScoreDirectionFromCandles failed for {tf}: {ex.Message}");
            return "NEUTRAL";
        }
    }


    // FIX PRIORITY-1: РџРµСЂРµРіСЂСѓР·РєР° РїСЂРёРЅРёРјР°СЋС‰Р°СЏ СѓР¶Рµ Р·Р°РіСЂСѓР¶РµРЅРЅС‹Рµ current+higher СЃРІРµС‡Рё РёР· Orchestrator'Р°.
    // РЈРјРЅРѕ РјР°РїРїРёС‚ РёС… РЅР° СЃР»РѕС‚С‹ (micro/primary/macro) Рё РґРµР»Р°РµС‚ 1 HTTP-Р·Р°РїСЂРѕСЃ РґР»СЏ РЅРµРґРѕСЃС‚Р°СЋС‰РµРіРѕ С‚Р°Р№РјС„СЂРµР№РјР°.
    // Р­С‚Рѕ СѓСЃС‚СЂР°РЅСЏРµС‚ РіР»Р°РІРЅСѓСЋ РїСЂРёС‡РёРЅСѓ РЅРµСЃС‚Р°Р±РёР»СЊРЅРѕСЃС‚Рё: TwelveData rate limit (7 req/min) Рё Doppelganger Bug.
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
            return await Evaluate4DMatrixFetchAsync(asset, primaryTimeframe, isForex, binanceSymbol);
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
                >= 0.99 => "рџ’  РР”Р•РђР›Р¬РќР«Р™ РЎРР“РќРђР› (3 РўР¤ - 100%)",
                >= 0.65 => "рџ’  РЎРР›Р¬РќР«Р™ РЎРР“РќРђР› (2 РўР¤ - 67%)",
                _       => "рџ’  РЎР›РђР‘Р«Р™ РЎРР“РќРђР› (1 РўР¤ - 33%)"
            };

            string summary = $"вЂў 4D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label} [1-fetch smart]";

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
            return await Evaluate4DMatrixFetchAsync(asset, primaryTimeframe, isForex, binanceSymbol);
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
        double taScore = taSignal.Score;
        double ofScore = ofSignal.ScoreContribution;
        
        double smcScore = 0;
        if (smcSignal.BosDirection == "BULLISH_BOS") smcScore += 0.5;
        if (smcSignal.BosDirection == "BEARISH_BOS") smcScore -= 0.5;
        if (smcSignal.SweepDirection == "BULLISH_SWEEP") smcScore += 0.5;
        if (smcSignal.SweepDirection == "BEARISH_SWEEP") smcScore -= 0.5;

        double mlScore = mlSignal.Direction == "BUY" ? mlSignal.Confidence : (mlSignal.Direction == "PUT" ? -mlSignal.Confidence : 0);

        // ── AutoCalibration: Regime-Aware Signal Weights ──────────────────────────
        // Only activate for minute+ timeframes. Sub-minute markets have structurally
        // low ADX and high Shannon entropy, which would misclassify them as Chaos.
        if (!isSubMinute && autoCalib != null)
        {
            var regime = autoCalib.DetectMarketRegime(
                adx: taSignal.Adx,
                volRatio: volRatio,
                rsi: taSignal.Rsi);

            double wTa  = autoCalib.GetCalibratedRegimeWeight("TechAnalysis", asset, timeframe, regime);
            double wOf  = autoCalib.GetCalibratedRegimeWeight("OrderFlow",    asset, timeframe, regime);
            double wSmc = autoCalib.GetCalibratedRegimeWeight("SMC",          asset, timeframe, regime);
            double wMl  = autoCalib.GetCalibratedRegimeWeight("LIGHTGBM",     asset, timeframe, regime);

            taScore  *= wTa;
            ofScore  *= wOf;
            smcScore *= wSmc;
            mlScore  *= wMl;

            BotLogger.Info($"[AutoCalib] {asset}/{timeframe} Regime={regime} | wTA={wTa:F2} wOF={wOf:F2} wSMC={wSmc:F2} wML={wMl:F2}");
        }
        // ─────────────────────────────────────────────────────────────────────────

        bool tfConflict = mtfResult.DominantDirection != "NEUTRAL" && 
                         ((taScore > 0 && mtfResult.DominantDirection == "PUT") || 
                          (taScore < 0 && mtfResult.DominantDirection == "BUY"));

        double metaProb = 0.5;
        if (TradeOutcomeTracker.MetaLearner != null)
        {
            metaProb = TradeOutcomeTracker.MetaLearner.Predict(
                asset, timeframe, taScore, ofScore, smcScore, mlScore, tfConflict);
        }
        else
        {
            // ML-FALLBACK LOBOTOMY FIX: weight TA, SMC, OF, and ML when MetaLearner is offline.
            metaProb = Math.Clamp(0.5 + (taScore * 0.2) + (smcScore * 0.2) + (ofScore * 0.1) + (mlScore * 0.2), 0.0, 1.0);
        }

        // Пользовательское требование: Бот должен всегда давать сигнал (без NEUTRAL зоны)
        string finalDir = metaProb >= 0.5 ? "BUY" : "PUT";
        
        // Определение маржи уверенности (от 0.0 до 0.5)
        double margin = Math.Abs(metaProb - 0.5);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- Сигнальный анализ ({asset} {timeframe}) ---");
        sb.AppendLine($"- ML (LightGBM): {mlScore:F2} {(mlSignal.Direction != "NEUTRAL" ? mlSignal.Direction : "")}");
        sb.AppendLine($"- Tech Analysis: {taScore:F2} {(taScore > 0 ? "BUY" : (taScore < 0 ? "PUT" : "NEUTRAL"))}");
        sb.AppendLine($"- Smart Money: {smcScore:F2} {(smcScore > 0 ? "BUY" : (smcScore < 0 ? "PUT" : "NEUTRAL"))}");
        sb.AppendLine($"- OrderFlow: {ofScore:F2} {(ofScore > 0 ? "BUY" : (ofScore < 0 ? "PUT" : "NEUTRAL"))}");
        sb.AppendLine($"- Базовая уверенность: {(0.5 + margin)*100:F1}% {finalDir}");

        // 1. Штраф конфликта таймфреймов
        if (tfConflict) 
        {
            margin *= 0.8; 
            sb.AppendLine("- Конфликт таймфреймов: Снижение уверенности");
        }

        // 2. Критический конфликт ТехАнализа (Исправление бага с 25%)
        if (Math.Abs(taScore) > 0.8 && ((taScore > 0 && finalDir == "PUT") || (taScore < 0 && finalDir == "BUY")))
        {
            margin *= 0.5; // Срезаем только маржу, а не базовые 50%
            sb.AppendLine("- Критический разворот Теханализа: Сильное снижение уверенности");
        }

        // 3. Фаза рынка (RSI)
        if (taSignal.Rsi > 65 && finalDir == "BUY") 
        {
            margin *= 0.7;
            sb.AppendLine("- Фаза рынка: Перекупленность (риск лонга на пике)");
        }
        else if (taSignal.Rsi < 35 && finalDir == "PUT")
        {
            margin *= 0.7;
            sb.AppendLine("- Фаза рынка: Перепроданность (риск шорта на дне)");
        }
        else if (taSignal.Rsi > 65 && finalDir == "PUT")
        {
            margin = Math.Min(0.50, margin * 1.3);
            sb.AppendLine("- Фаза рынка: Подтверждение отката вниз (Перекупленность)");
        }
        else if (taSignal.Rsi < 35 && finalDir == "BUY")
        {
            margin = Math.Min(0.50, margin * 1.3);
            sb.AppendLine("- Фаза рынка: Подтверждение отката вверх (Перепроданность)");
        }

        // 4. Энтропия / Скорость рынка
        double absVel = Math.Abs(stateSignal.VelocityBpsPerSec);
        double dangerVel = isSubMinute ? 1.0 : 4.0; 
        if (absVel >= dangerVel)
        {
            margin *= 0.8;
            sb.AppendLine("- Энтропия: Экстремальная волатильность (Хаос), занижение уверенности");
        }

        double finalScore = 0.5 + margin;
        sb.AppendLine($"-> Итог: {finalDir} {(int)Math.Clamp(Math.Round(finalScore * 100), 50, 100)}%");

        string reasoningText = sb.ToString();
        BotLogger.Info($"\n{reasoningText}");

        return new ConsensusDecision(
            CandidateDirection: finalDir,
            FinalDirection: finalDir,
            Probability: (int)Math.Clamp(Math.Round(finalScore * 100), 50, 100),
            CombinedReasoningText: reasoningText,
            FinalTotalScore: (metaProb - 0.5) * 2.0,
            RecommendedExpiryText: "",
            TaScore: taScore,
            OfScore: ofScore,
            SmcScore: smcScore,
            MlProb: metaProb,
            MlScoreRaw: mlScore
        );
    }

}





