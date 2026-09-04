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

            string dirMicro   = ScoreDirection(microPrices,   microVolumes, microTf);
            string dirPrimary = ScoreDirection(primaryPrices, primaryVolumes, primaryTf);
            string dirMacro   = ScoreDirection(macroPrices,   macroVolumes, macroTf);

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
    private string ScoreDirection(double[] prices, double[] volumes, string tf)
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
                    baseTime.AddMinutes(i)
                );
            }

            // AUDIT FIX: передаём реальный tf как asset-ключ (вместо "internal") чтобы IndicatorCache
            // корректно разделял кэши по таймфреймам матрицы (m1/m3/m5 и т.д.).
            var (score, _, _, _, _, _) = marketAnalyzer.ScoreTimeframe(
                $"4dmatrix_{tf}", tf, prices,
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

        // 1b. Order Flow (Leading — выделен в отдельный компонент, приоритетный сигнал для опционов)
        double ofWeight  = await SignalTracker.GetSignalWeightAsync("ORDERFLOW", 1.2);
        totalScore      += ofSignal.ScoreContribution * ofWeight;
        totalConfidence += 65.0 * ofWeight;
        totalWeight     += ofWeight;

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

        if (taSignal.Adx < 20.0)
        {
            trendWeight     = 0.0;
            reversionWeight = 2.0;
        }
        else if (taSignal.Adx > 25.0)
        {
            trendWeight     = 1.5;
            reversionWeight = 0.5;
        }

        // FIX BUG-3: graduated penalty instead of binary taWeight flip (0.1x/2.0x).
        // Binary caused instability: 2 random losses -> radical weight change -> more errors.
        if (consecutiveLosses >= 2)
        {
            double lossPenalty = Math.Max(0.5, 1.0 - (consecutiveLosses - 1) * 0.15);
            BotLogger.Warn($"[Regime Switch] {asset}/{timeframe}: {consecutiveLosses} losses. Graduated penalty={lossPenalty:F2}x");
            if (volRatio < 1.0)
            {
                trendWeight     *= lossPenalty;
                reversionWeight *= (2.0 - lossPenalty);
                BotLogger.Info("[Regime Switch] Low vol + losses -> boosting reversion.");
            }
            else
            {
                trendWeight     *= (2.0 - lossPenalty);
                reversionWeight *= lossPenalty;
                BotLogger.Info("[Regime Switch] High vol + losses -> boosting trend-following.");
            }
        }
        double finalSmcScore = (smcTrendScore * trendWeight) + (smcReversionScore * reversionWeight);

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

        // AUDIT FIX: AutoCalibrationEngine — применяем режимный мультипликатор.
        // AutoCalibrationEngine был написан, но никогда не вызывался в решении.
        // Теперь детектируем режим рынка (Trending / Ranging / Chaos) и корректируем score.
        if (TradeOutcomeTracker.CalibrationEngine is AutoCalibrationEngine calibEngine)
        {
            var regime = calibEngine.DetectMarketRegime(taSignal.Adx, volRatio, taSignal.Rsi);
            // Получаем мультипликатор для ENSEMBLE-источника: отражает общую историческую точность
            double regimeMultiplier = calibEngine.GetCalibratedRegimeWeight("SKENDER_MATH", asset, timeframe, regime);
            // Применяем как мягкий скейлинг (clamp чтобы не инвертировать знак)
            double scaledMultiplier = Math.Clamp(regimeMultiplier, 0.5, 1.5);
            totalScore *= scaledMultiplier;
            BotLogger.Info($"[AutoCalib] Regime={regime}, Multiplier={regimeMultiplier:F2}x → scaled={scaledMultiplier:F2}x, adjustedScore={totalScore:F3}");
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

            // FIX C-12: if ML and Math clearly contradict, reduce ML dominance
            if (Math.Sign(mlScore) != Math.Sign(scoreMath) && Math.Abs(scoreMath) > 0.3)
            {
                mlWeight   *= 0.6;
                mathWeight *= 1.4;
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
        if (!asset.Contains("BTC") && !asset.Contains("ETH") && !asset.Contains("SOL"))
        {
            int h = DateTime.UtcNow.Hour;
            if (h >= 21 || h < 2) { sessionMultiplier = 0.75; sessionName = "DEAD_ZONE"; } // Поздний вечер (расширение спредов, мертвый рынок)
            else if (h >= 2 && h < 8) { sessionMultiplier = 0.85; sessionName = "ASIAN"; } // Азия (низкая волатильность, пила)
            else if (h >= 8 && h < 13) { sessionMultiplier = 1.0; sessionName = "LONDON_MORNING"; } // Лондон
            else if (h >= 13 && h < 17) { sessionMultiplier = 1.1; sessionName = "LONDON_NY_OVERLAP"; } // Макс. ликвидность (супер-тренды)
            else if (h >= 17 && h < 21) { sessionMultiplier = 1.0; sessionName = "NY_AFTERNOON"; } // Нью-Йорк вечер
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
                : $"\u2022 \u26a1 Нейросеть (LightGBM): НЕЙТРАЛЬНО (0% уверенности){modelAccText}");

        string combinedReasoning = $"{smcText}\n{flowText}\n{lgbmText}";

        return new ConsensusDecision(candidateDir, candidateDir, probability, combinedReasoning, totalScore);
    }

}




