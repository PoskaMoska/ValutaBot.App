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

            string dirMicro   = ScoreDirection(microPrices,   microVolumes);
            string dirPrimary = ScoreDirection(primaryPrices, primaryVolumes);
            string dirMacro   = ScoreDirection(macroPrices,   macroVolumes);

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
                >= 0.99 => "\u2b50 GOLDEN SETUP (3D 100%)",
                >= 0.65 => "\u26a1 STRONG CONFLUENCE (2D 67%)",
                _       => "\ud83d\udcca STANDARD ANALYSIS (33%)"
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
            throw new Exception($"OTKAZ API: 3D Matrix error for {asset}. {ex.Message}");
        }
    }

        private static (string micro, string primary, string macro)
        Resolve3DTimeframes(string tf) =>
        tf.ToLower() switch
        {
            "s3" or "s5" or "s10" or "s15" or "s30" => ("m1",  "m3",  "m5"),
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
    private string ScoreDirection(double[] prices, double[] volumes)
    {
        if (prices == null || prices.Length < 10) 
        {
            throw new Exception($"РћРўРљРђР— API: РџРѕР»СѓС‡РµРЅРѕ {(prices == null ? 0 : prices.Length)} СЃРІРµС‡РµР№ РґР»СЏ РјР°С‚СЂРёС†С‹ (РЅСѓР¶РЅРѕ РјРёРЅ 10).");
        }

        double avgDiff = 0;
        if (prices.Length > 1) {
            for (int k = 1; k < prices.Length; k++) avgDiff += Math.Abs(prices[k] - prices[k - 1]);
            avgDiff /= (prices.Length - 1);
        }
        if (avgDiff == 0) avgDiff = prices[0] * 0.0001;

        // ArrayPool: РІРјРµСЃС‚Рѕ new OhlcCandle[n] (4 Р°Р»Р»РѕРєР°С†РёРё РЅР° Р·Р°РїСЂРѕСЃ) Р±РµСЂС‘Рј Р±СѓС„РµСЂ РёР· РїСѓР»Р°.
        var candles = ArrayPool<MiniAppController.OhlcCandle>.Shared.Rent(prices.Length);
        try
        {
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
                    DateTime.UtcNow.AddMinutes(i - prices.Length)
                );
            }

            var (score, _, _, _, _, _) = marketAnalyzer.ScoreTimeframe(
                "internal", "internal", prices,
                volumes: volumes,
                candles: candles.AsSpan(0, prices.Length)
            );

            // РџРѕСЂРѕРі РїРѕРґРЅСЏС‚ СЃ В±0.10 РґРѕ В±0.20: РїСЂРё С€РєР°Р»Рµ [-1, +1] РїСЂРµР¶РЅРёР№ РїРѕСЂРѕРі 0.10
            // РєР»Р°СЃСЃРёС„РёС†РёСЂРѕРІР°Р» ~80% С€СѓРјРѕРІРѕРіРѕ СЂС‹РЅРєР° РєР°Рє РЅР°РїСЂР°РІР»РµРЅРЅС‹Р№ СЃРёРіРЅР°Р» (BUY/PUT).
            return score > 0.20 ? "BUY" : score < -0.20 ? "PUT" : "NEUTRAL";
        }
        finally
        {
            ArrayPool<MiniAppController.OhlcCandle>.Shared.Return(candles);
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

        // 1. Technical Analysis (Lagging вЂ” СЂРѕР»СЊ С„РѕРЅРѕРІРѕРіРѕ С„РёР»СЊС‚СЂР°, РІРµСЃ СЃРЅРёР¶РµРЅ РґРѕ 0.5)
        double taWeight  = await SignalTracker.GetSignalWeightAsync("INDICATORS", 0.5);
        totalScore      += taSignal.Score * taWeight;
        totalConfidence += taSignal.Confidence * taWeight;
        totalWeight     += taWeight;

        // 1b. Order Flow (Leading вЂ” РІС‹РґРµР»РµРЅ РІ РѕС‚РґРµР»СЊРЅС‹Р№ РєРѕРјРїРѕРЅРµРЅС‚, РїСЂРёРѕСЂРёС‚РµС‚РЅС‹Р№ СЃРёРіРЅР°Р» РґР»СЏ РѕРїС†РёРѕРЅРѕРІ)
        double ofWeight  = await SignalTracker.GetSignalWeightAsync("ORDERFLOW", 1.8);
        totalScore      += ofSignal.ScoreContribution * ofWeight;
        totalConfidence += 65.0 * ofWeight;
        totalWeight     += ofWeight;

        // 2. Velocity / Continuous State (Leading вЂ” РјРёРєСЂРѕ-СѓСЃРєРѕСЂРµРЅРёРµ С†РµРЅС‹, РїРѕРІС‹С€РµРЅ РґРѕ 2.0)
        double stateWeight  = await SignalTracker.GetSignalWeightAsync("VelocityState", 2.0);
        totalScore         += stateSignal.MomentumContribution * stateWeight;
        totalConfidence    += 55.0 * stateWeight;
        totalWeight        += stateWeight;

        // 3. SMC (Smart Money Concepts) — adaptive weights based on ADX regime
        double smcTrendScore     = 0.0;
        double smcReversionScore = 0.0;

        if (smcSignal.BosDirection == "BULLISH")  smcTrendScore     += 2.0;
        if (smcSignal.BosDirection == "BEARISH")  smcTrendScore     -= 2.0;
        // FIX: "NONE" is not an empty string — guard against it explicitly
        if (!string.IsNullOrEmpty(smcSignal.OrderBlockType) && smcSignal.OrderBlockType != "NONE")
            smcTrendScore += smcSignal.OrderBlockType.Contains("BULL") ? 1.0 : -1.0;
        if (!string.IsNullOrEmpty(smcSignal.FvgType) && smcSignal.FvgType != "NONE")
            smcTrendScore += smcSignal.FvgType.Contains("BULL") ? 1.0 : -1.0;
        if (smcSignal.SweepDirection == "BULLISH_SWEEP") smcReversionScore += 2.0;
        if (smcSignal.SweepDirection == "BEARISH_SWEEP") smcReversionScore -= 2.0;

        double trendWeight     = 1.0;
        double reversionWeight = 1.0;

        if (taSignal.Adx < 20.0)
        {
            // Choppy / Flat Market: Nerf BOS, Boost Sweeps
            trendWeight     = 0.0;
            reversionWeight = 2.0;
        }
        else if (taSignal.Adx > 25.0)
        {
            // Trending Market: Boost BOS, Nerf Sweeps
            trendWeight     = 1.5;
            reversionWeight = 0.5;
        }

                if (consecutiveLosses >= 2)
        {
            BotLogger.Warn($"[Regime Switch] {asset}/{timeframe} Hit {consecutiveLosses} losses in a row. Activating Dynamic Penalty.");
            if (volRatio < 1.0)
            {
                BotLogger.Info("[Regime Switch] Low Volume detected. Switching to Counter-Trend (Reversion) Mode.");
                trendWeight = 0.0;
                reversionWeight = 3.0; 
                taWeight *= 0.1; 
            }
            else
            {
                BotLogger.Info("[Regime Switch] High Volume detected. Switching to Trend-Following Mode.");
                trendWeight = 3.0;
                reversionWeight = 0.0; 
                taWeight *= 2.0; 
            }
        }

        double finalSmcScore = (smcTrendScore * trendWeight) + (smcReversionScore * reversionWeight);

        if (Math.Abs(finalSmcScore) > 0.1)
        {
            // FIX W-20: dynamic normalization — max score depends on active weights
            double maxPossibleSmc = (trendWeight * 4.0) + (reversionWeight * 2.0);
            double normSmcScore   = maxPossibleSmc > 0 ? finalSmcScore / maxPossibleSmc : 0;

            double smcWeight   = await SignalTracker.GetSignalWeightAsync("SMC", 1.5);
            totalScore        += normSmcScore * smcWeight;
            totalConfidence   += 60.0 * smcWeight;
            totalWeight       += smcWeight;
        }

        // Normalize internal base scores
        if (totalWeight > 0)
        {
            totalScore      /= totalWeight;
            totalConfidence /= totalWeight;
        }

        // Apply conflict penalty globally to the normalized score
        totalScore *= conflictPenalty;

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

            // FIX C-12: if ML and Math clearly contradict, reduce ML dominance
            if (Math.Sign(mlScore) != Math.Sign(scoreMath) && Math.Abs(scoreMath) > 0.3)
            {
                mlWeight   *= 0.6;
                mathWeight *= 1.4;
            }

            finalConfidenceScore = (mlScore * mlWeight) + (scoreMath * mathWeight);
        }

        // FIX W-21: wider neutral dead-zone (was ±0.0001 → ±0.05)
        // ±0.0001 treated almost any non-zero value as a signal, generating noise.
        candidateDir = finalConfidenceScore > 0.01 ? "BUY" : finalConfidenceScore < -0.01 ? "PUT" : "NEUTRAL";

        // 5. Final Decision
        double absWeightedScore = Math.Abs(finalConfidenceScore);
        int probability = isSubMinute
            ? Math.Clamp(50 + (int)Math.Round(absWeightedScore * 40), 50, 91)
            : Math.Clamp(50 + (int)Math.Round(absWeightedScore * 45), 50, 95);

        // MTF Golden Boost — only when 4D dominant direction EXPLICITLY matches candidateDir.
        // FIX W-16: removed || "NEUTRAL" condition — neutral MTF must not boost confidence.
        if (candidateDir != "NEUTRAL"
            && mtfResult.ProbabilityBoost > 0
            && mtfResult.DominantDirection == candidateDir)
        {
            probability = Math.Clamp(probability + mtfResult.ProbabilityBoost, 55, 95);
        }

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
                : $"\u2022 \u26a1 Нейросеть (LightGBM): НЕЙТРАЛЬНО (0% уверенности){modelAccText}");

        string combinedReasoning = $"{smcText}\n{flowText}\n{lgbmText}";

        return new ConsensusDecision(candidateDir, candidateDir, probability, combinedReasoning, totalScore);
    }

}




