using System;
using System.Linq;

namespace ValutaBot.MiniApp;

public record ContinuousStateResult(
    double VelocityBpsPerSec,      // 1st Derivative dp/dt (basis points / sec)
    double AccelerationBpsPerSec2, // 2nd Derivative d2p/dt2 (basis points / sec^2)
    double KalmanFilteredState,
    string VelocityRegime,         // "HYPER_ACCELERATING_UP" | "HYPER_ACCELERATING_DOWN" | "DECELERATING" | "STABLE"
    double MomentumContribution,
    string Description
);

/// <summary>
/// Continuous Latent State Engine (Wall Street HFT Standard).
/// Eliminates discrete candle boundaries (M1/M5) by treating market price as a continuous 
/// physical state vector with instantaneous velocity (dp/dt) and acceleration (d2p/dt2).
/// </summary>
public static class ContinuousStateEngine
{

    /// <summary>
    /// Computes continuous physical velocity, acceleration, and Kalman state vector.
    /// </summary>
    public static ContinuousStateResult EvaluateContinuousState(ReadOnlySpan<double> prices, string asset = "GLOBAL", string timeframe = "m1")
    {
        // Drop the live unclosed candle to maintain uniform dt for Savitzky-Golay and Kalman
        if (prices.Length > 10)
        {
            prices = prices.Slice(0, prices.Length - 1);
        }

        if (prices.Length < 10)
        {
            return new ContinuousStateResult(0, 0, 0, "UNKNOWN", 0, "Недостаточно данных для непрерывного анализа.");
        }

        foreach (var p in prices)
        {
            if (double.IsNaN(p) || double.IsInfinity(p))
            {
                return new ContinuousStateResult(0, 0, 0, "UNKNOWN", 0, "Обнаружено повреждение данных (NaN/Infinity).");
            }
        }

        int n = prices.Length;
        double currentPrice = prices[^1];

        int tfSeconds = timeframe.ToLower() switch
        {
            "s5"  => 5, "s10" => 10, "s15" => 15, "s30" => 30,
            "m1"  => 60, "m3"  => 180, "m5"  => 300, "m15" => 900, "m30" => 1800,
            _     => 60
        };
        bool isSubMinute = tfSeconds < 60;

        // 1. Calculate Dynamic Volatility (Proxy for ATR / Measurement Noise)
        double sumDiff = 0, sumSqDiff = 0;
        for (int i = 1; i < n; i++)
        {
            double diff = prices[i] - prices[i - 1];
            sumDiff += diff;
            sumSqDiff += diff * diff;
        }
        double meanDiff = sumDiff / (n - 1);
        double variance = (sumSqDiff / (n - 1)) - (meanDiff * meanDiff);
        double dynamicAtr = Math.Sqrt(Math.Max(1e-12, variance));

        // 2. Historical SG Derivatives for Z-Score Normalization
        int velCount = n - 4; // SG filter needs 5 points window
        double sumVel = 0, sumSqVel = 0, sumAccel = 0, sumSqAccel = 0;
        
        for (int i = 4; i < n; i++)
        {
            double sgV = (-2.0 * prices[i-4] - 1.0 * prices[i-3] + 0.0 * prices[i-2] + 1.0 * prices[i-1] + 2.0 * prices[i]) / 10.0;
            double instV = (sgV / Math.Max(1e-8, prices[i-2])) * 10_000.0;
            sumVel += instV; sumSqVel += instV * instV;

            double sgA = (2.0 * prices[i-4] - 1.0 * prices[i-3] - 2.0 * prices[i-2] - 1.0 * prices[i-1] + 2.0 * prices[i]) / 7.0;
            double instA = (sgA / Math.Max(1e-8, prices[i-2])) * 10_000.0;
            sumAccel += instA; sumSqAccel += instA * instA;
        }

        double meanVel = sumVel / velCount;
        double stdDevVel = Math.Sqrt(Math.Max(1e-12, (sumSqVel / velCount) - (meanVel * meanVel)));
        double minStdDevV = isSubMinute ? 0.05 : 0.5; // Prevent infinity in completely dead markets
        stdDevVel = Math.Max(minStdDevV, stdDevVel);

        double meanAccel = sumAccel / velCount;
        double stdDevAccel = Math.Sqrt(Math.Max(1e-12, (sumSqAccel / velCount) - (meanAccel * meanAccel)));
        double minStdDevA = isSubMinute ? 0.01 : 0.1;
        stdDevAccel = Math.Max(minStdDevA, stdDevAccel);

        // 3. Current Instantaneous State
        double sgVelocity = (-2.0 * prices[^5] - 1.0 * prices[^4] + 0.0 * prices[^3] + 1.0 * prices[^2] + 2.0 * prices[^1]) / 10.0;
        double instantVelocity = (sgVelocity / Math.Max(1e-8, prices[^3])) * 10_000.0;
        double zScoreVel = (instantVelocity - meanVel) / stdDevVel;

        double sgAccel = (2.0 * prices[^5] - 1.0 * prices[^4] - 2.0 * prices[^3] - 1.0 * prices[^2] + 2.0 * prices[^1]) / 7.0;
        double instantAcceleration = (sgAccel / Math.Max(1e-8, prices[^3])) * 10_000.0;
        double zScoreAccel = (instantAcceleration - meanAccel) / stdDevAccel;

        // 4. 4th-Order Adaptive Continuous Kalman State Filtering
        double kalmanState = FilterKalmanContinuous(prices, dynamicAtr, tfSeconds);

        string regime;
        double momentumContribution = 0;
        string desc;

        // Adaptive Z-Score thresholds instead of hardcoded numbers
        double zVelThreshold = 1.6;  // ~1.6 Sigma = top ~5% of movements
        double zAccelThreshold = 1.0; 
        double zDecelThreshold = 1.2;

        if (zScoreVel > zVelThreshold && zScoreAccel > zAccelThreshold)
        {
            regime = "HYPER_ACCELERATING_UP";
            momentumContribution = 0.45;
            desc = $"Адаптивный вектор: Гипер-ускорение ВВЕРХ (Z-Vel: +{zScoreVel:F1}σ, Z-Acc: +{zScoreAccel:F1}σ).";
        }
        else if (zScoreVel < -zVelThreshold && zScoreAccel < -zAccelThreshold)
        {
            regime = "HYPER_ACCELERATING_DOWN";
            momentumContribution = -0.45;
            desc = $"Адаптивный вектор: Гипер-ускорение ВНИЗ (Z-Vel: {zScoreVel:F1}σ, Z-Acc: {zScoreAccel:F1}σ).";
        }
        else if (Math.Sign(instantVelocity) != Math.Sign(instantAcceleration) && Math.Abs(zScoreVel) > zDecelThreshold)
        {
            regime = "DECELERATING";
            momentumContribution = -Math.Sign(instantVelocity) * 0.20;
            desc = $"Адаптивный вектор: Замедление импульса перед разворотом (Deceleration).";
        }
        else
        {
            regime = "STABLE";
            momentumContribution = 0;
            desc = $"Адаптивный вектор: Стабильное движение (в пределах нормы).";
        }

        // Kalman deviation contribution
        double kalmanDevBps = currentPrice > 1e-8 ? ((currentPrice - kalmanState) / currentPrice) * 10_000.0 : 0;
        double kalmanContribution = Math.Clamp(kalmanDevBps / 10.0, -0.15, 0.15);
        momentumContribution = Math.Clamp(momentumContribution + kalmanContribution, -0.60, 0.60);

        return new ContinuousStateResult(
            VelocityBpsPerSec: Math.Round(instantVelocity, 2),
            AccelerationBpsPerSec2: Math.Round(instantAcceleration, 2),
            KalmanFilteredState: Math.Round(kalmanState, 5),
            VelocityRegime: regime,
            MomentumContribution: momentumContribution,
            Description: desc
        );
    }

    private static double FilterKalmanContinuous(ReadOnlySpan<double> prices, double dynamicAtr, int tfSeconds)
    {
        double currentPrice = prices[^1];
        
        // Time-scaled Process Noise (Brownian motion rule: scales with sqrt of time)
        double baseProcessNoise = Math.Max(1e-8, currentPrice * 0.00001); 
        double processNoise = baseProcessNoise * Math.Sqrt(tfSeconds);

        // Adaptive Measurement Noise based on actual historical standard deviation
        double measurementNoise = Math.Max(1e-8, dynamicAtr);

        double est = prices[0];
        double err = measurementNoise;
        
        for (int i = 0; i < prices.Length; i++) 
        { 
            double pPrice = prices[i];
            double k = err / (err + measurementNoise);
            est = est + k * (pPrice - est);
            err = (1.0 - k) * err + processNoise;
        }

        return est;
    }
}

