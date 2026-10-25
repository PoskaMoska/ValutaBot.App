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
    // 4D Matrix вЂ” fetch mode (always makes 3 HTTP requests)
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
                >= 0.99 => "рџ’Ћ РР”Р•РђР›Р¬РќР«Р™ РЎРР“РќРђР› (3 РўР¤ - 100%)",
                >= 0.65 => "рџ’Ћ РЎРР›Р¬РќР«Р™ РЎРР“РќРђР› (2 РўР¤ - 67%)",
                _       => "рџ’Ћ РЎР›РђР‘Р«Р™ РЎРР“РќРђР› (1 РўР¤ - 33%)"
            };

            string summary = $"РІР‚Сћ 4D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label}";

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
                ConfluenceLabel: "рџ’Ћ 3D Matrix Unavailable",
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
    // FIX PRIORITY-1: РџРµСЂРµРіСЂСѓР·РєР° РїСЂРёРЅРёРјР°СЋС‰Р°СЏ СѓР¶Рµ Р·Р°РіСЂСѓР¶РµРЅРЅС‹Рµ current+higher СЃРІРµС‡Рё РёР· Orchestrator'Р°.
        // This is required so the 1-fetch pre-loaded primaryCandles and macroCandles in Evaluate4DMatrixAsync
    // Р­С‚Рѕ СѓСЃС‚СЂР°РЅСЏРµС‚ РіР»Р°РІРЅСѓСЋ РїСЂРёС‡РёРЅСѓ РЅРµСЃС‚Р°Р±РёР»СЊРЅРѕСЃС‚Рё: rate limit Рё Doppelganger Bug.
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
            BotLogger.Info($"[Confluence 3D] Not enough real OHLC candles for {tf} ({prices?.Length ?? 0}) РІР‚вЂќ returning NEUTRAL.");
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
    // Р­С‚Рѕ СѓСЃС‚СЂР°РЅСЏРµС‚ РіР»Р°РІРЅСѓСЋ РїСЂРёС‡РёРЅСѓ РЅРµСЃС‚Р°Р±РёР»СЊРЅРѕСЃС‚Рё: rate limit Рё Doppelganger Bug.
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
                >= 0.99 => "рџ’Ћ РР”Р•РђР›Р¬РќР«Р™ РЎРР“РќРђР› (3 РўР¤ - 100%)",
                >= 0.65 => "рџ’Ћ РЎРР›Р¬РќР«Р™ РЎРР“РќРђР› (2 РўР¤ - 67%)",
                _       => "рџ’Ћ РЎР›РђР‘Р«Р™ РЎРР“РќРђР› (1 РўР¤ - 33%)"
            };

            string summary = $"РІР‚Сћ 4D Matrix ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label} [1-fetch smart]";

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
            public Task<ConsensusDecision> EvaluateMatrixAsync(
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
        return EvaluateMatrixAsync(asset, timeframe, isSubMinute, conflictPenalty, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, consecutiveLosses, volRatio);
    }

    public async Task<ConsensusDecision> EvaluateMatrixAsync(
        string asset,
        string timeframe,
        bool isSubMinute,
        double conflictPenalty,
        TaSignal taSignal,
        SmcSignal smcSignal,
        MlSignal mlSignal,
        StateSignal stateSignal,
        ConfluenceMatrixResult mtfResult, int consecutiveLosses = 0, double volRatio = 1.0)
    {
        await Task.CompletedTask;
        double taScore = taSignal.Score;

        // OF DISABLED: 35.8% empirical win-rate (5.5Пѓ anti-signal on live data). 
        // Order flow tick-volume is unreliable on subminute timeframes. Pending structural rework.
        double ofScore = 0.0;

        double smcScore = 0;
        if ((smcSignal.BosDirection ?? "").Contains("BULLISH")) smcScore += 0.5;
        if ((smcSignal.BosDirection ?? "").Contains("BEARISH")) smcScore -= 0.5;
        if ((smcSignal.SweepDirection ?? "").Contains("BULLISH")) smcScore += 0.5;
        if ((smcSignal.SweepDirection ?? "").Contains("BEARISH")) smcScore -= 0.5;
        if ((smcSignal.OrderBlockType ?? "").Contains("BULLISH")) smcScore += 0.25;
        if ((smcSignal.OrderBlockType ?? "").Contains("BEARISH")) smcScore -= 0.25;
        if ((smcSignal.FvgType ?? "").Contains("BULLISH")) smcScore += 0.25;
        if ((smcSignal.FvgType ?? "").Contains("BEARISH")) smcScore -= 0.25;
        smcScore = Math.Clamp(smcScore, -1.0, 1.0);

        // Properly signed P(BUY) in [0, 1]: > 0.5 for BUY, < 0.5 for PUT
        double rawProb;
        if (mlSignal.RawConfidence.HasValue && !double.IsNaN(mlSignal.RawConfidence.Value))
        {
            rawProb = Math.Clamp(mlSignal.RawConfidence.Value, 0.01, 0.99);
        }
        else
        {
            double directionalConf = Math.Clamp(mlSignal.Confidence, 0.5, 0.99);
            rawProb = mlSignal.Direction == "BUY" ? directionalConf : (mlSignal.Direction == "PUT" ? (1.0 - directionalConf) : 0.5);
    
    
        }



        double mlScore = (rawProb - 0.5) * 2.0; // Smooth signed scaling [-1.0, +1.0] (+ for BUY, - for PUT)

        bool isNeuralBrain = !string.IsNullOrEmpty(mlSignal.ModelVersion) &&
                             (mlSignal.ModelVersion.Contains("patch_tst", StringComparison.OrdinalIgnoreCase) ||
                              mlSignal.ModelVersion.Contains("seq_brain", StringComparison.OrdinalIgnoreCase) ||
                              mlSignal.ModelVersion.Contains("neural", StringComparison.OrdinalIgnoreCase));

        // ══════════════════════════════════════════════════════════════════════════
        // PURE NEURAL BRAIN ARCHITECTURE (Вариант 1)
        // ══════════════════════════════════════════════════════════════════════════
        // Решение и вероятность определяются на 100% нейросетью.
        // Старые индикаторы (TA, SMC) не разбавляют и не подавляют сигнал тренда,
        // а остаются в качестве справочной телеметрии для AI Co-Pilot.
        double normTa  = Math.Clamp(taScore, -1.0, 1.0);
        double normSmc = Math.Clamp(smcScore, -1.0, 1.0);
        double normMl  = Math.Clamp(mlScore, -1.0, 1.0);

        var safetyNotes = new List<string>();
        string finalDir;
        int finalProbPct;

        bool hasMlVerdict = mlSignal != null && mlSignal.Direction is "BUY" or "PUT";

        if (hasMlVerdict)
        {
            // Pure Neural Decision
            finalDir = mlSignal!.Direction;
            double neuralConf = mlSignal.Confidence > 0 ? mlSignal.Confidence : 0.50;
            finalProbPct = (int)Math.Clamp(Math.Round(neuralConf * 100.0), 50, 92);

            // S5 Micro-Timeframe Noise Calibration:
            // 5-second breakout spikes on Pocket Option OTC have strong negative autocorrelation (immediate mean-reversion).
            // Cap confidence to 57% so s5 does not trigger false certainty on single-tick bursts.
            if (timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase))
            {
                finalProbPct = Math.Min(57, finalProbPct);
            }

            // Circuit Breaker risk dampener (C# риск-менеджмент серий потерь)
            int mlLiveLosses = Math.Max(consecutiveLosses, autoCalib?.GetConsecutiveLosses("LIGHTGBM", asset, timeframe) ?? 0);
            if (mlLiveLosses >= 3)
            {
                finalProbPct = Math.Max(50, finalProbPct - 6);
                safetyNotes.Add($"⚠️ Circuit Breaker: {mlLiveLosses} убытка подряд на {asset}/{timeframe} (-6% уверенности)");
            }
            else if (mlLiveLosses == 2)
            {
                finalProbPct = Math.Max(50, finalProbPct - 3);
                safetyNotes.Add($"⚠️ Осторожность: 2 убытка подряд на {asset}/{timeframe} (-3% уверенности)");
            }

            // MTF Conflict penalty (если старший ТФ жестко противоречит)
            if (conflictPenalty < 1.0)
            {
                finalProbPct = Math.Max(50, (int)Math.Round(finalProbPct * conflictPenalty));
                safetyNotes.Add("⚠️ MTF Conflict: Противоречие со старшим таймфреймом");
            }
        }
        else
        {
            // Fallback (если ML временно недоступен или выдал NEUTRAL)
            finalDir = (stateSignal.VelocityBpsPerSec >= 0 || taSignal.Score >= 0) ? "BUY" : "PUT";
            finalProbPct = (int)Math.Clamp(Math.Round((0.50 + Math.Abs(taScore) * 0.25) * 100.0), 50, 92);
            safetyNotes.Add("ℹ️ Резервный режим: ML сигнал нейтрален или на калибровке (Fallback Engine)");
        }

        var coPilot = new System.Text.StringBuilder();
        coPilot.AppendLine($"--- Сигнальный анализ ({asset} {timeframe}) ---");
        if (hasMlVerdict)
        {
            coPilot.AppendLine($"[Нейромозг: {mlSignal!.ModelVersion} (Pure Neural Brain)]");
            coPilot.AppendLine($" - Вердикт нейросети: {finalDir} (Чистая уверенность: {finalProbPct}%)");
            coPilot.AppendLine($" - Архитектура: 100% прямое нейросетевое решение (без подавления старыми индикаторами)");
        }
        else
        {
            coPilot.AppendLine($"[Резервный режим: ML Offline / Neutral]");
            coPilot.AppendLine($" - Вердикт: {finalDir} ({finalProbPct}%)");
        }
        coPilot.AppendLine("[Показания датчиков C# (Телеметрия)]");
        coPilot.AppendLine($" - Датчик ТА (RSI={taSignal.Rsi:F1}, ADX={taSignal.Adx:F1}): {taScore:F2} ({(taScore > 0.05 ? "BUY" : (taScore < -0.05 ? "PUT" : "NEUTRAL"))})");
        coPilot.AppendLine($" - Датчик старшего ТФ (MTF): {mtfResult.DominantDirection}{(mtfResult.IsGoldenSetup ? " [GOLDEN]" : "")}");
        coPilot.AppendLine($" - Датчик скорости (Velocity): {stateSignal.VelocityBpsPerSec:F2} bps/s");
        if (safetyNotes.Count > 0)
        {
            coPilot.AppendLine();
            coPilot.AppendLine("[Риск-контроль C#]");
            foreach (var note in safetyNotes) coPilot.AppendLine($" {note}");
        }
        coPilot.AppendLine($"-> Итог: {finalDir} {finalProbPct}%");

        string consensusReasoning = coPilot.ToString();
        if (!asset.StartsWith("CHAOS_"))
        {
            BotLogger.Info($"\n{consensusReasoning}");
        }

        return new ConsensusDecision(
            CandidateDirection: finalDir,
            FinalDirection: finalDir,
            Probability: finalProbPct,
            CombinedReasoningText: consensusReasoning,
            FinalTotalScore: (finalDir == "BUY" ? 1.0 : -1.0) * ((finalProbPct - 50.0) / 50.0),
            RecommendedExpiryText: "",
            TaScore: normTa,
            OfScore: ofScore,
            SmcScore: normSmc,
            MlProb: rawProb,
            MlScoreRaw: normMl
        );
    }
}

