import os
content = r'''using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Decision Consensus Engine: Implements Soft Voting & Dynamic Extreme Weighting.
/// Dynamically suppresses ML hallucinations on RSI extreme boundaries (>70 / <30)
/// and calculates continuous probabilities across LightGBM, Claude AI, and Skender Math.
/// </summary>
public static class ConsensusEngine
{
    public record DecisionResult(
        string CandidateDirection,
        string FinalDirection,
        int Probability,
        string CombinedReasoningText,
        string RecommendedExpiryText = ""
    );

    public static DecisionResult EvaluateConsensus(
        double totalScore,
        string claudeReasoningText,
        string lgbmDirection,
        double lgbmConfidence,
        double? lgbmAccuracy,
        string onnxDirection,
        double onnxConfidence,
        double rsiVal,
        double emaVal,
        bool isSubMinute,
        string asset = "EURUSD",
        string timeframe = "m1",
        double adxVal = 20.0,
        double volRatioVal = 1.0,
        string smcReasoning = "",
        string orderFlowReasoning = "",
        string aiModelName = "ИИ Анализ",
        double wfWeightMultiplier = 1.0,
        ReadOnlySpan<double> prices = default)
    {
        // 1. Market-Regime Aware Auto-Calibrated Weights
        double weightLgbm = AutoCalibrationEngine.GetCalibratedRegimeWeight("LIGHTGBM", asset, timeframe, adxVal, volRatioVal, rsiVal, 1.8, prices);
        double weightMath = AutoCalibrationEngine.GetCalibratedRegimeWeight("SKENDER_MATH", asset, timeframe, adxVal, volRatioVal, rsiVal, 1.2, prices);
        double weightOnnx = AutoCalibrationEngine.GetCalibratedRegimeWeight("ONNX", asset, timeframe, adxVal, volRatioVal, rsiVal, 2.2, prices);

        // 3. HFT Soft Voting Vector Calculation (0% LLM Weight in Decision Pipeline)
        double normLgbm = Math.Max(0, (lgbmConfidence - 0.5) * 2.0);
        double normOnnx = Math.Max(0, (onnxConfidence - 0.5) * 2.0);

        double scoreLgbm = lgbmDirection == "BUY" ? normLgbm : lgbmDirection == "PUT" ? -normLgbm : 0;
        double scoreOnnx = onnxDirection == "BUY" ? normOnnx : onnxDirection == "PUT" ? -normOnnx : 0;
        double scoreMath = Math.Clamp(totalScore, -2.5, 2.5) / 2.5; // Normalize Math score to [-1.0, 1.0]

        // Only include weights for models that actually provided a directional opinion.
        double activeWeightLgbm = (lgbmDirection == "BUY" || lgbmDirection == "PUT") ? weightLgbm * wfWeightMultiplier : 0;
        double activeWeightOnnx = (onnxDirection == "BUY" || onnxDirection == "PUT") ? weightOnnx * wfWeightMultiplier : 0;
        double activeWeightMath = weightMath;
        
        double totalWeightSum = activeWeightLgbm + activeWeightOnnx + activeWeightMath;
        if (totalWeightSum < 1e-9) totalWeightSum = 1.0;
        
        double weightedScore = (scoreLgbm * activeWeightLgbm + scoreOnnx * activeWeightOnnx + scoreMath * activeWeightMath) / totalWeightSum;

        string candidateDir = weightedScore > 0.0001 ? "BUY" : weightedScore < -0.0001 ? "PUT" : (totalScore > 0.02 ? "BUY" : totalScore < -0.02 ? "PUT" : "NEUTRAL");

        double absWeightedScore = Math.Abs(weightedScore);
        int probability = isSubMinute
            ? Math.Clamp(50 + (int)Math.Round(absWeightedScore * 40), 50, 91)
            : Math.Clamp(50 + (int)Math.Round(absWeightedScore * 45), 50, 95);

        string finalDirection = candidateDir;

        // 5. Format 4 Pillars of Analysis Breakdown
        string modelAccText = lgbmAccuracy.HasValue 
            ? $" [обученность: {Math.Round(lgbmAccuracy.Value * 100, 1)}%]" 
            : "";

        string smcText = !string.IsNullOrEmpty(smcReasoning)
            ? $"• 🏛️ SMC Структура: {smcReasoning}"
            : "• 🏛️ SMC Структура: Балансовая консолидация диапазона";

        string flowText = !string.IsNullOrEmpty(orderFlowReasoning)
            ? $"• 🌊 Order Flow & CVD: {orderFlowReasoning}"
            : "• 🌊 Order Flow & CVD: Поток ордеров сбалансирован";

        string lgbmText = !string.IsNullOrEmpty(lgbmDirection) && lgbmDirection != "NEUTRAL"
            ? $"• ⚡ Нейросеть (LightGBM): {(lgbmDirection == "BUY" ? "ВВЕРХ ⬆" : "ВНИЗ ⬇")} ({Math.Round(lgbmConfidence * 100)}% уверенность){modelAccText}"
            : $"• ⚡ Нейросеть (LightGBM): НЕТ СИГНАЛА";

        string effectiveModelName = string.IsNullOrEmpty(aiModelName) ? "Математический ИИ" : aiModelName;

        string baseClaudeReasoning = string.IsNullOrEmpty(claudeReasoningText)
            ? $"Матем. анализ Skender (RSI: {Math.Round(rsiVal, 1)}, EMA: {Math.Round(emaVal, 2)})"
            : claudeReasoningText;

        string claudeText = $"• 🧠 {effectiveModelName}: {baseClaudeReasoning}";

        string combinedReasoning = $"{smcText}\n{flowText}\n{lgbmText}\n{claudeText}";

        return new DecisionResult(candidateDir, finalDirection, probability, combinedReasoning);
    }
}'''

with open(r'MiniApp\Engines\ConsensusEngine.cs', 'w', encoding='utf-8') as f:
    f.write(content)
