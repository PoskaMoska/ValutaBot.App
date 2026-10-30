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
    private MiniAppController.OhlcCandle[] StripUnclosedCandle(MiniAppController.OhlcCandle[] candles, string tf)
    {
        if (candles == null || candles.Length <= 1) return candles ?? Array.Empty<MiniAppController.OhlcCandle>();
        int intervalSecs = fetcher.TimeframeSeconds(tf);
        DateTime refNow = (PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue)
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        bool isLastClosed = intervalSecs > 0 && candles[^1].Timestamp.AddSeconds(intervalSecs) <= refNow;
        return isLastClosed ? candles : candles.Take(candles.Length - 1).ToArray();
    }

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
            microCandles   = StripUnclosedCandle(microCandles, microTf);
            primaryCandles = StripUnclosedCandle(primaryCandles, primaryTf);
            macroCandles   = StripUnclosedCandle(macroCandles, macroTf);

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
                >= 0.99 => "💎 ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "💎 СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "💎 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };

            string summary = $"• MTF Alignment ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label}";

            BotLogger.Info($"[MTF Alignment] {asset} | Ratio: {confluenceRatio * 100}% ({maxAgree}/3 {dominantDir}) | Boost: +{boost}% | Golden: {isGoldenSetup}");

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
            BotLogger.Error($"[MTF Alignment] Error evaluating matrix for {asset}", ex);
            return new ConfluenceMatrixResult(
                ConfluenceRatio: 0.0,
                IsGoldenSetup: false,
                ProbabilityBoost: 0,
                ConfluenceLabel: "💎 MTF Alignment Unavailable",
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
    private string ScoreDirectionFromCandles(
        MiniAppController.OhlcCandle[] ohlcCandles,
        double[] prices,
        double[] volumes,
        string tf,
        string asset = "global")
    {
        if (prices == null || prices.Length < 10 || ohlcCandles == null || ohlcCandles.Length < 10)
        {
            BotLogger.Info($"[MTF Alignment] Not enough real OHLC candles for {tf} ({prices?.Length ?? 0}) — returning NEUTRAL.");
            return "NEUTRAL";
        }

        try
        {
            var (score, _, _, _, _, _) = marketAnalyzer.ScoreTimeframe(
                $"4dmatrix_{asset}_{tf}", tf, prices,
                volumes: volumes,
                candles: ohlcCandles.AsSpan()
            );

            return score > 0.20 ? "BUY" : score < -0.20 ? "PUT" : "NEUTRAL";
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MTF Alignment] ScoreDirectionFromCandles failed for {tf}: {ex.Message}");
            return "NEUTRAL";
        }
    }

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
            BotLogger.Info($"[MTF Alignment] Pre-loaded candles missing or too short for {asset}/{primaryTimeframe} — falling back to 3-fetch mode.");
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
                    var candlesRaw = await fetcher.FetchOhlcWithFallbackAsync(binanceSymbol, targetTf, asset, 50);
                    candlesRaw = StripUnclosedCandle(candlesRaw, targetTf);
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
                >= 0.99 => "💎 ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "💎 СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "💎 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };

            string summary = $"• MTF Alignment ({microTf.ToUpper()}+{primaryTf.ToUpper()}+{macroTf.ToUpper()}): {label} [1-fetch smart]";

            BotLogger.Info($"[MTF Alignment] {asset}/{primaryTimeframe} | Ratio: {confluenceRatio * 100}% ({maxAgree}/3 {dominantDir}) | Boost: +{boost}% | Golden: {isGoldenSetup} | Smart 1-fetch");

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
            BotLogger.Error($"[MTF Alignment] Error in 1-fetch mode for {asset}", ex);
            return await Evaluate4DMatrixFetchAsync(asset, primaryTimeframe, isForex, binanceSymbol);
        }
    }

    // Unified Matrix Evaluation
    
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
        ConfluenceMatrixResult mtfResult,
        int consecutiveLosses = 0,
        double volRatio = 1.0,
        ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult? noiseAudit = null)
    {
        return EvaluateMatrixAsync(asset, timeframe, isSubMinute, conflictPenalty, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, consecutiveLosses, volRatio, noiseAudit);
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
        ConfluenceMatrixResult mtfResult,
        int consecutiveLosses = 0,
        double volRatio = 1.0,
        ValutaBot.MiniApp.Features.MarketAnalysis.SubminuteNoiseGatekeeper.NoiseAuditResult? noiseAudit = null)
    {
        await Task.CompletedTask;
        double taScore = taSignal.Score;
        if (double.IsNaN(taScore) || double.IsInfinity(taScore))
        {
            taScore = 0.0;
        }

        // OF DISABLED: 35.8% empirical win-rate (anti-signal on live data).
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
        if (mlSignal.RawConfidence.HasValue && !double.IsNaN(mlSignal.RawConfidence.Value) && !double.IsInfinity(mlSignal.RawConfidence.Value))
        {
            rawProb = Math.Clamp(mlSignal.RawConfidence.Value, 0.01, 0.99);
        }
        else
        {
            double conf = double.IsNaN(mlSignal.Confidence) || double.IsInfinity(mlSignal.Confidence) ? 0.5 : mlSignal.Confidence;
            double directionalConf = Math.Clamp(conf, 0.5, 0.99);
            rawProb = mlSignal.Direction == "BUY" ? directionalConf : (mlSignal.Direction == "PUT" ? (1.0 - directionalConf) : 0.5);
        }

        double mlScore = (rawProb - 0.5) * 2.0; // Smooth signed scaling [-1.0, +1.0] (+ for BUY, - for PUT)
        if (double.IsNaN(mlScore) || double.IsInfinity(mlScore)) mlScore = 0.0;

        bool isNeuralBrain = !string.IsNullOrEmpty(mlSignal.ModelVersion) &&
                             (mlSignal.ModelVersion.Contains("patch_tst", StringComparison.OrdinalIgnoreCase) ||
                              mlSignal.ModelVersion.Contains("seq_brain", StringComparison.OrdinalIgnoreCase) ||
                              mlSignal.ModelVersion.Contains("dual_stream", StringComparison.OrdinalIgnoreCase) ||
                              mlSignal.ModelVersion.Contains("neural", StringComparison.OrdinalIgnoreCase));

        // ══════════════════════════════════════════════════════════════════════════
        // TWO-STAGE HURDLE ARCHITECTURE
        // ══════════════════════════════════════════════════════════════════════════
        double normTa  = Math.Clamp(taScore, -1.0, 1.0);
        double normSmc = Math.Clamp(smcScore, -1.0, 1.0);
        double normMl  = Math.Clamp(mlScore, -1.0, 1.0);

        var safetyNotes = new List<string>();
        string finalDir;
        int finalProbPct;

        bool hasMlVerdict = mlSignal != null && mlSignal.Direction is "BUY" or "PUT";
        bool isMlExplicitHold = isNeuralBrain && mlSignal != null && mlSignal.Direction is "HOLD" or "NEUTRAL";

        // D-2 fix: явная проверка «все TF вернули NEUTRAL» до входа в логику.
        // Если MTF не дал ни одного BUY/PUT направления — это сигнал недостаточности данных (нет связи,
        // нет свечей). В этом случае принудительно блокируем торговлю уже здесь, не дожидаясь Orchestrator-fallback.
        bool allMtfNeutral = mtfResult != null
            && mtfResult.TimeframeDirections != null
            && mtfResult.TimeframeDirections.Count > 0
            && !mtfResult.TimeframeDirections.Values.Any(d => d is "BUY" or "PUT");

        int mlLiveLosses = Math.Max(consecutiveLosses, Math.Max(autoCalib?.GetConsecutiveLosses("NeuralBrain", asset, timeframe) ?? 0, autoCalib?.GetConsecutiveLosses("LIGHTGBM", asset, timeframe) ?? 0));

        if (hasMlVerdict)
        {
            // Pure Neural Decision
            finalDir = mlSignal!.Direction;
            double neuralConf = (mlSignal.Confidence > 0 && !double.IsNaN(mlSignal.Confidence) && !double.IsInfinity(mlSignal.Confidence))
                ? Math.Clamp(mlSignal.Confidence, 0.50, 0.99)
                : 0.50;
            finalProbPct = (int)Math.Clamp(Math.Round(neuralConf * 100.0), 50, 92);

            // S5 Micro-Timeframe Noise Calibration
            if (timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase))
            {
                finalProbPct = Math.Min(59, finalProbPct);
            }

            // Circuit Breaker risk dampener
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

            // MTF Conflict penalty
            if (conflictPenalty < 1.0)
            {
                finalProbPct = Math.Max(50, (int)Math.Round(finalProbPct * conflictPenalty));
                safetyNotes.Add("⚠️ MTF Conflict: Противоречие со старшим таймфреймом");
            }
        }
        else if (isMlExplicitHold)
        {
            // Neural Brain explicitly commanded HOLD: keep neutral
            finalDir = "NEUTRAL";
            finalProbPct = 50;
            safetyNotes.Add($"ℹ️ Neural Brain ({mlSignal!.ModelVersion}): HOLD (flat market, entry blocked)");
        }
        else
        {
            // Fallback (если ML временно недоступен или выдал NEUTRAL)
            // Архитектурный инвариант: при пустом стакане, нулевых тиках или |taScore| <= 0.15 — используем Компас Макро-Тренда
            if (double.IsNaN(taScore) || double.IsInfinity(taScore) || Math.Abs(taScore) <= 0.15)
            {
                string macroDir = "NEUTRAL";
                if (mtfResult != null)
                {
                    if (mtfResult.DominantDirection is "BUY" or "PUT")
                        macroDir = mtfResult.DominantDirection;
                    else if (mtfResult.TimeframeDirections != null)
                    {
                        string[] priorityTfs = new[] { "D1", "H4", "H1", "M30", "M15", "M5", "M3", "M2", "M1", "S30", "S15", "S10", "S5" };
                        foreach (var tfKey in priorityTfs)
                        {
                            if (mtfResult.TimeframeDirections.TryGetValue(tfKey, out var d) && d is "BUY" or "PUT")
                            {
                                macroDir = d;
                                break;
                            }
                        }
                    }
                }

                if (macroDir is "BUY" or "PUT")
                {
                    finalDir = macroDir;
                    finalProbPct = 52;
                    safetyNotes.Add($"🧭 Компас Макро-Тренда ({macroDir}): Базовое направление зафиксировано по старшему ТФ при слабом локальном импульсе (|TA| <= 0.15)");
                }
                else
                {
                    finalDir = "NEUTRAL";
                    finalProbPct = 50;
                    safetyNotes.Add("ℹ️ Консенсус нейтрален: |TA score| <= 0.15 (Нейтральный рынок, BUY-байас исключен)");
                }
            }
            else
            {
                finalDir = taScore > 0 ? "BUY" : "PUT";
                finalProbPct = (int)Math.Clamp(Math.Round((0.50 + Math.Abs(taScore) * 0.25) * 100.0), 50, 92);
                safetyNotes.Add("ℹ️ Резервный режим: ML сигнал нейтрален или на калибровке (Fallback Engine)");
                if (allMtfNeutral)
                {
                    finalProbPct = Math.Min(finalProbPct, 52);
                    safetyNotes.Add("⚠️ MTF без направления (нет данных/все ТФ NEUTRAL): уверенность ограничена 52%, реальный вход невозможен");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STAGE 2: SUBMINUTE NOISE & WICK REJECTION CALIBRATION
        // ══════════════════════════════════════════════════════════════════════════
        bool hasRiskDampener = (mlLiveLosses >= 2) || (conflictPenalty < 1.0);
        if (noiseAudit.HasValue && finalDir is "BUY" or "PUT" && isSubMinute)
        {
            var na = noiseAudit.Value;
            if (!na.IsTradeableImpulse)
            {
                // Затухание волатильности или шумовой флэт: не блокируем сигнал, но честно калибруем вероятность
                if (na.LocalAtrBps < na.NoiseFloorBps && finalProbPct > 52)
                {
                    finalProbPct = 52;
                    safetyNotes.Add($"⚠️ Шумовой флэт: размах свечей ({na.LocalAtrBps:F2} bps) ниже порога шума брокера ({na.NoiseFloorBps:F1} bps). Уверенность ограничена 52%.");
                }
            }
            else
            {
                // Здоровый импульс + Тень отскока (Wick Rejection) по тренду
                bool hasWick = (finalDir == "BUY" && na.HasBullishWick) || (finalDir == "PUT" && na.HasBearishWick);
                if (hasWick)
                {
                    string wickName = finalDir == "BUY" ? "нижняя тень подтверждает отскок по тренду BUY" : "верхняя тень подтверждает отскок по тренду PUT";
                    if (hasRiskDampener)
                    {
                        // D-3 fix: риск-демпфер активен (серия убытков или MTF конфликт).
                        // Wick-бонус НЕ должен стирать штраф Circuit Breaker.
                        // При >= 3 убытках (CB полный штраф -6%) бонус = 0: wick не отменяет CB.
                        // При 2 убытках (CB мягкий штраф -3%) даём минимальный бонус +1, но не поднимаем
                        // finalProbPct выше его значения ДО CB-штрафа (т.е. не компенсируем CB).
                        if (mlLiveLosses >= 3)
                        {
                            // CB hard stop: wick-бонус заблокирован полностью
                            safetyNotes.Add($"🎯 Снайперский откат (Wick Rejection): {wickName} (бонус 0%, демпфировано риск-контролем CB >= 3 убытков)");
                        }
                        else
                        {
                            // mlLiveLosses == 2 или conflictPenalty < 1: осторожный бонус +1
                            finalProbPct = Math.Clamp(finalProbPct + 1, 50, 60);
                            safetyNotes.Add($"🎯 Снайперский откат (Wick Rejection): {wickName} (+1%, демпфировано до {finalProbPct}% из-за риск-контроля)");
                        }
                    }
                    else
                    {
                        finalProbPct = Math.Clamp(Math.Max(finalProbPct, 61) + 4, 61, 68);
                        safetyNotes.Add($"🎯 Снайперский откат (Wick Rejection): {wickName} (+4%)");
                    }
                }

                // Детекция экстремального выброса цены (Mean Reversion на затухании)
                if (na.IsExtremeOverbought && taSignal.Rsi >= 78.0)
                {
                    safetyNotes.Add($"⚡ Mean Reversion: экстремальный вылет цены ({na.ZScore:F1}σ, RSI {taSignal.Rsi:F0}) — вероятность затухания и отскока вниз");
                }
                else if (na.IsExtremeOversold && taSignal.Rsi <= 22.0)
                {
                    safetyNotes.Add($"⚡ Mean Reversion: экстремальная перепроданность ({na.ZScore:F1}σ, RSI {taSignal.Rsi:F0}) — вероятность затухания и отскока вверх");
                }
            }
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
        coPilot.AppendLine($" - Датчик старшего ТФ (MTF): {mtfResult?.DominantDirection ?? "NEUTRAL"}{(mtfResult?.IsGoldenSetup == true ? " [GOLDEN]" : "")}");
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
            FinalTotalScore: finalDir == "NEUTRAL" ? 0.0 : (finalDir == "BUY" ? 1.0 : -1.0) * ((finalProbPct - 50.0) / 50.0),
            RecommendedExpiryText: "",
            TaScore: normTa,
            OfScore: ofScore,
            SmcScore: normSmc,
            MlProb: rawProb,
            MlScoreRaw: normMl
        );
    }
}
