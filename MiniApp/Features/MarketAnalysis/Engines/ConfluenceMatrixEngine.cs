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
            BotLogger.Info($"[Confluence 3D] Pre-loaded candles missing or too short for {asset}/{primaryTimeframe} вЂ” falling back to 3-fetch mode.");
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

        // 1. Technical Analysis (Lagging вЂ” РћС†РµРЅРєР° РРЅРґРёРєР°С‚РѕСЂРѕРІ)
        double taScoreOverride = taSignal.Score;
        
        // РЈРјРЅС‹Р№ С„РёР»СЊС‚СЂ С‚СЂРµРЅРґР° (Trend Filter): Р±Р»РѕРєРёСЂРѕРІРєР° RSI РІ С‚СЂРµРЅРґРѕРІС‹С… РїСЂРѕР±РѕСЏС…
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

        // 1b. Order Flow вЂ” ALWAYS APPLIED NOW
        double ofWeight  = await SignalTracker.GetSignalWeightAsync("ORDERFLOW", 1.2);
        if (Math.Abs(ofSignal.ScoreContribution) > 0)
        {
            totalScore      += ofSignal.ScoreContribution * ofWeight;
            totalConfidence += 65.0 * ofWeight;
            totalWeight     += ofWeight;
        }

        // 2. Velocity / Continuous State (Leading вЂ” РјРёРєСЂРѕРґРІРёР¶РµРЅРёСЏ С†РµРЅС‹)
        // AUDIT FIX: С‚РѕР»СЊРєРѕ РІРєР»СЋС‡Р°РµРј РІ totalWeight РµСЃР»Рё contribution РЅРµРЅСѓР»РµРІРѕР№ (>= 0.03).
        // РџСЂРё STABLE (contribution=0) РґРѕР±Р°РІР»РµРЅРёРµ stateWeight РІ Р·РЅР°РјРµРЅР°С‚РµР»СЊ Р»РёС€СЊ СЂР°Р·Р±Р°РІР»СЏР»Рѕ TA Рё OF.
        double stateWeight  = await SignalTracker.GetSignalWeightAsync("VelocityState", 1.0);
        if (Math.Abs(stateSignal.MomentumContribution) >= 0.03)
        {
            totalScore        += stateSignal.MomentumContribution * stateWeight;
            totalConfidence   += 55.0 * stateWeight;
            totalWeight       += stateWeight;
        }
        else
        {
            BotLogger.Info($"[State] MomentumContribution={stateSignal.MomentumContribution:F3} < 0.03 (STABLE) вЂ” skipping VelocityState weight.");
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
            // FIX W-20: dynamic normalization вЂ” max score depends on active weights
            // AUDIT FIX: SMC РїРѕР»РЅРѕСЃС‚СЊСЋ РѕС‚РєР»СЋС‡РµРЅ РЅР° СЃСѓР±-РјРёРЅСѓС‚Рµ (s5/s10/s15/s30).
            // BOS, FVG, OrderBlock вЂ” РёРЅСЃС‚РёС‚СѓС†РёРѕРЅР°Р»СЊРЅС‹Рµ РєРѕРЅС†РµРїС†РёРё РґР»СЏ H1 Рё +.
            // РќР° 5-СЃРµРєСѓРЅРґРЅС‹С… СЃРІРµС‡Р°С… СЌС‚Рѕ СЃС‚РѕС…Р°СЃС‚РёС‡РµСЃРєРёР№ С€СѓРј, Р·Р°РіСЂСЏР·РЅСЏСЋС‰РёР№ СЃРєРѕСЂРёРЅРі.
            double maxPossibleSmc = (trendWeight * 4.0) + (reversionWeight * 2.0);
            double normSmcScore   = maxPossibleSmc > 0 ? finalSmcScore / maxPossibleSmc : 0;

            double smcWeight   = await SignalTracker.GetSignalWeightAsync("SMC", 1.5);
            totalScore        += normSmcScore * smcWeight;
            totalConfidence   += 60.0 * smcWeight;
            totalWeight       += smcWeight;
        }
        else if (isSubMinute)
        {
            BotLogger.Info($"[SMC] Sub-minute timeframe вЂ” SMC scoring disabled (institutional concepts not valid on {timeframe}).");
        }

        // Normalize internal base scores
        if (totalWeight > 0)
        {
            totalScore      /= totalWeight;
            totalConfidence /= totalWeight;
        }

        // Apply conflict penalty globally to the normalized score
        totalScore *= conflictPenalty;

        // FIX PRIORITY-5: AutoCalibrationEngine РјСѓР»СЊС‚РёРїР»РёРєР°С‚РѕСЂ РїСЂРёРјРµРЅСЏР»СЃСЏ Рє TA-РєРѕРјРїРѕРЅРµРЅС‚Сѓ.
        // Р Р°РЅРµРµ РѕРЅ РїСЂРёРјРµРЅСЏР»СЃСЏ РєРѕ РІСЃРµРјСѓ totalScore РџРћРЎР›Р• РЅРѕСЂРјР°Р»РёР·Р°С†РёРё вЂ” СЌС‚Рѕ СЃРѕР·РґР°РІР°Р»Рѕ feedback loop:
        // СЃРµСЂРёСЏ РїРѕС‚РµСЂСЊ -> РјСѓР»СЊС‚РёРїР»РёРєР°С‚РѕСЂ < 1 -> РІРµСЃ score СЃРЅРёР¶Р°РµС‚СЃСЏ -> Р±РѕР»СЊС€Рµ NEUTRAL -> РЅРµС‚ РґР°РЅРЅС‹С…
        // РґР»СЏ РІРѕСЃСЃС‚Р°РЅРѕРІР»РµРЅРёСЏ -> РјСѓР»СЊС‚РёРїР»РёРєР°С‚РѕСЂ РЅРµ СЂР°СЃС‚РµС‚ -> Р·Р°РјРєРЅСѓС‚С‹Р№ РєСЂСѓРі.
        // РўРµРїРµСЂСЊ: РјС‹ РјР°СЃС€С‚Р°Р±РёСЂСѓРµРј С‚РѕР»СЊРєРѕ РІРєР»Р°Рґ TA (taScoreOverride СѓР¶Рµ РґРѕР±Р°РІР»РµРЅ РІ totalScore С‡РµСЂРµР·
        // taWeight, РїРѕСЌС‚РѕРјСѓ РєРѕСЂСЂРµРєС‚РёСЂСѓРµРј РїСЂРѕРїРѕСЂС†РёРѕРЅР°Р»СЊРЅРѕ РєР°Рє РґРѕР±Р°РІРѕС‡РЅС‹Р№ delta-term).
        if (TradeOutcomeTracker.CalibrationEngine is AutoCalibrationEngine calibEngine)
        {
            var regime = calibEngine.DetectMarketRegime(taSignal.Adx, volRatio, taSignal.Rsi);
            // РњСѓР»СЊС‚РёРїР»РёРєР°С‚РѕСЂ РґР»СЏ TA-РёСЃС‚РѕС‡РЅРёРєР° (РЅРµ ENSEMBLE вЂ” С‡С‚РѕР±С‹ РёР·РѕР»РёСЂРѕРІР°С‚СЊ РІР»РёСЏРЅРёРµ)
            double regimeMultiplier = calibEngine.GetCalibratedRegimeWeight("SKENDER_MATH", asset, timeframe, regime);
            
            // РЈРїСЂРѕС‰РµРЅРёРµ: РјР°СЃС€С‚Р°Р±РёСЂСѓРµРј РІ СѓР·РєРѕРј РґРёР°РїР°Р·РѕРЅРµ [0.8, 1.2] вЂ” РЅРµ РёРЅРІРµСЂС‚РёСЂСѓРµРј СЃРёРіРЅР°Р»
            double scaledMultiplier = Math.Clamp(regimeMultiplier, 0.8, 1.2);
            // РџСЂРёРјРµРЅСЏРµРј С‚РѕР»СЊРєРѕ Рє TA-С‡Р°СЃС‚Рё (РїСЂРѕРїРѕСЂС†РёРѕРЅР°Р»СЊРЅРѕ РµС‘ РІРµСЃСѓ РІ С„РёРЅР°Р»СЊРЅРѕРј score)
            double taFraction = totalWeight > 0 ? (taWeight / totalWeight) : 0.5;
            totalScore = totalScore * (1.0 + (scaledMultiplier - 1.0) * taFraction);
            
            BotLogger.Info($"[AutoCalib] Regime={regime}, Multiplier={regimeMultiplier:F2}x -> scaled={scaledMultiplier:F2}x, taFraction={taFraction:F2}, adjustedScore={totalScore:F3}");
        }

        // AUDIT FIX: FearGreed вЂ” РґРѕР±Р°РІР»РµРЅР° РєРѕРЅС‚СЂСЂР°РЅРЅР°СЏ РІРєР»Р°РґРєР° РґР»СЏ РєСЂРёРїС‚Рѕ-РїР°СЂ.
        // FearGreedService СЃСѓС‰РµСЃС‚РІСѓРµС‚, РЅРѕ РЅРёРіРґРµ РЅРµ РІС‹Р·С‹РІР°Р»СЃСЏ РІ РјР°С‚СЂРёС†Рµ СЂРµС€РµРЅРёР№.
        // РўРѕР»СЊРєРѕ РґР»СЏ РєСЂРёРїС‚Рѕ (isForex=false); РґР»СЏ forex РІРѕР·РІСЂР°С‰Р°РµС‚ contribution=0.0.
        // РњР°РєСЃРёРјР°Р»СЊРЅС‹Р№ РІРєР»Р°Рґ В±0.08 (РјР°СЃС€С‚Р°Р±РёСЂРѕРІР°РЅРЅС‹Р№ СЃ РѕСЂРёРіРёРЅР°Р»СЊРЅРѕРіРѕ В±0.12).
        bool isForexAsset = AssetSanitizer.IsForexAsset(AssetSanitizer.Sanitize(asset));
        try
        {
            var fg = await ValutaBot.App.MiniApp.Services.FearGreedService.GetAsync(isForexAsset);
            if (Math.Abs(fg.ScoreContribution) > 0.01)
            {
                // РњР°СЃС€С‚Р°Р±РёСЂСѓРµРј РґРѕ В±0.08 РјР°РєСЃРёРјСѓРј С‡С‚РѕР±С‹ РЅРµ РґРѕРјРёРЅРёСЂРѕРІР°С‚СЊ РЅР°Рґ РѕСЃРЅРѕРІРЅС‹РјРё СЃРёРіРЅР°Р»Р°РјРё
                double fgContrib = Math.Clamp(fg.ScoreContribution * 0.67, -0.08, 0.08);
                totalScore += fgContrib;
                BotLogger.Info($"[FearGreed] Zone={fg.Zone}, Contrib={fg.ScoreContribution:+0.00;-0.00} -> applied={fgContrib:+0.00;-0.00}");
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
        
        // 5. Final Decision: ML-Dominant Universal Blend (For ALL Timeframes)
        
        // 1. РќРѕСЂРјР°Р»РёР·СѓРµРј СЃС‹СЂС‹Рµ СЃРёРіРЅР°Р»С‹ РІ РґРёР°РїР°Р·РѕРЅ [-1.0, 1.0]
        double ta  = Math.Clamp(taScoreRaw, -1.0, 1.0);
        double smc = Math.Clamp(finalSmcScore, -1.0, 1.0);
        double of  = Math.Clamp(ofScoreRaw, -1.0, 1.0);
        double ml  = Math.Clamp(mlProbRaw, -1.0, 1.0); // >0 BUY, <0 PUT
        double mom = Math.Clamp(stateSignal.MomentumContribution, -1.0, 1.0);

        // 2. Р–РµСЃС‚РєРёРµ РІРµСЃР° (РђСЂС…РёС‚РµРєС‚СѓСЂР°: РР СЂРµС€Р°РµС‚, С„РёР·РёРєР° РєРѕСЂСЂРµРєС‚РёСЂСѓРµС‚, РѕСЃС‚Р°Р»СЊРЅРѕРµ вЂ” С€СѓРј)
        double mlDominantWeight    = 0.60; // РќРµР№СЂРѕСЃРµС‚СЊ (Р±Р°Р·Р° 100k+ СЃРІРµС‡РµР№)
        double momDominantWeight   = 0.25; // РњРѕРјРµРЅС‚СѓРј (Р·Р°С‰РёС‚Р° РѕС‚ С‚РѕСЂРіРѕРІР»Рё РїСЂРѕС‚РёРІ СЂРµР·РєРёС… РёРјРїСѓР»СЊСЃРѕРІ)
        double taDominantWeight    = 0.10; // РўРµС…Р°РЅР°Р»РёР· (Р·Р°С‰РёС‚Р° РѕС‚ РІС…РѕРґРѕРІ РЅР° Р¶РµСЃС‚РєРёС… СЌРєСЃС‚СЂРµРјСѓРјР°С…)
        double smcOfDominantWeight = 0.05; // SMC Рё OrderFlow (РґР°СЋС‚ РјРёРєСЂРѕ-РІР»РёСЏРЅРёРµ, С‡С‚РѕР±С‹ СЂР°РґР°СЂ РЅРµ Р±С‹Р» РїСѓСЃС‚С‹Рј)

        // 3. Р’С‹С‡РёСЃР»СЏРµРј РёС‚РѕРіРѕРІС‹Р№ РІРµРєС‚РѕСЂ [-1.0, 1.0]
        double combinedScore = (ml * mlDominantWeight) 
                             + (mom * momDominantWeight) 
                             + (ta * taDominantWeight) 
                             + (((smc + of) / 2.0) * smcOfDominantWeight);

        // 4. РџРµСЂРµРІРѕРґРёРј РІРµРєС‚РѕСЂ РІ Р±Р°Р·РѕРІСѓСЋ РІРµСЂРѕСЏС‚РЅРѕСЃС‚СЊ [0.0, 1.0]
        double baseProb = (combinedScore + 1.0) / 2.0;

        // 5. Р’С‹РґР°РµРј СЃРёРіРЅР°Р» РІСЃРµРіРґР° (Р‘РµР· NEUTRAL Р·РѕРЅС‹ РїРѕ С‚СЂРµР±РѕРІР°РЅРёСЋ РїРѕР»СЊР·РѕРІР°С‚РµР»СЏ)
        if (baseProb >= 0.50) 
        {
            candidateDir = "BUY";
            finalConfidenceScore = (baseProb - 0.5) * 2.0; // РјР°СЃС€С‚Р°Р±РёСЂСѓРµРј РІ [0.0, 1.0]
        }
        else 
        {
            candidateDir = "PUT";
            finalConfidenceScore = (0.5 - baseProb) * 2.0; // РјР°СЃС€С‚Р°Р±РёСЂСѓРµРј РІ [0.0, 1.0]
        }

        // РњРёРЅРёРјР°Р»СЊРЅС‹Р№ РїРѕСЂРѕРі СѓРІРµСЂРµРЅРЅРѕСЃС‚Рё РґР»СЏ UI (С‡С‚РѕР±С‹ РІРёР·СѓР°Р»СЊРЅРѕ РЅРµ РїРѕРєР°Р·С‹РІР°С‚СЊ 0%)
        if (finalConfidenceScore < 0.05) finalConfidenceScore = 0.05;

        // РЁРђР“ 2: Р‘Р»РѕРєРёСЂРѕРІРєР° РєРѕРЅС„Р»РёРєС‚Р° РЈР”РђР›Р•РќРђ. 
        // РўРµРїРµСЂСЊ РµСЃР»Рё РР Рё РјР°С‚РµРјР°С‚РёРєР° СЃРїРѕСЂСЏС‚, РїРѕР±РµР¶РґР°РµС‚ С‚РѕС‚, Сѓ РєРѕРіРѕ СЃСѓРјРјР°СЂРЅС‹Р№ РїРµСЂРµРІРµСЃ С…РѕС‚СЏ Р±С‹ РЅР° 0.1%.

        // РЁРђР“ 3: Momentum Inversion РЈР”РђР›Р•Рќ.
        // РС‚РѕРіРѕРІС‹Р№ СЃРёРіРЅР°Р» РґРѕР»Р¶РµРЅ С‡РµСЃС‚РЅРѕ РѕС‚СЂР°Р¶Р°С‚СЊ РєРѕРјРїРѕРЅРµРЅС‚С‹ Р±РµР· РїСЂРёРЅСѓРґРёС‚РµР»СЊРЅС‹С… РїРµСЂРµРІРѕСЂРѕС‚РѕРІ.

        double absWeightedScore = finalConfidenceScore;
        
        // Apply conflict penalty globally to the final confidence (so MTF conflict actually lowers probability)
        absWeightedScore *= conflictPenalty;
        
        // Р’РЅРµРґСЂРµРЅРёРµ РёРЅС‚РµР»Р»РµРєС‚СѓР°Р»СЊРЅРѕРіРѕ СЃРµСЃСЃРёРѕРЅРЅРѕРіРѕ РјРЅРѕР¶РёС‚РµР»СЏ (Market Session Modifier)
        var (sessionMultiplier, sessionName) = MarketSessionEvaluator.GetSessionMultiplier(asset);
        
        absWeightedScore *= sessionMultiplier;
        
        int probability = (int)Math.Round(50 + (absWeightedScore * 50));
        if (isSubMinute) probability = Math.Clamp(probability, 50, 99);
        else probability = Math.Clamp(probability, 50, 95);

        if (sessionMultiplier < 1.0)
        {
            BotLogger.Info($"[MarketSession] {sessionName} detected. Multiplier={sessionMultiplier}. Lowering probability.");
        }
        else if (sessionMultiplier > 1.0)
        {
            BotLogger.Info($"[MarketSession] {sessionName} detected. High liquidity! Multiplier={sessionMultiplier}.");
        }

        // MTF Golden Boost вЂ” only when 4D dominant direction EXPLICITLY matches candidateDir.
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
        string combinedReasoning = ConfluenceFormattingService.BuildCombinedReasoningText(smcSignal, ofSignal, mlSignal);

        return new ConsensusDecision(
            candidateDir, candidateDir, probability, combinedReasoning, totalScore, "",
            TaScore: taSignal.Score,
            OfScore: ofSignal.ScoreContribution,
            SmcScore: finalSmcScore,
            MlProb: mlSignal.Direction == "BUY" ? mlSignal.Confidence : (mlSignal.Direction == "PUT" ? -mlSignal.Confidence : 0)
        );
    }

}

