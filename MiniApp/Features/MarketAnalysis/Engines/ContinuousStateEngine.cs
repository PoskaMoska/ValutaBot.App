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

        // SG 1st derivative coefficients: [-2, -1, 0, 1, 2] / 10
        double sgVelocity = (-2.0 * prices[^5] - 1.0 * prices[^4] + 0.0 * prices[^3] + 1.0 * prices[^2] + 2.0 * prices[^1]) / 10.0;
        double instantVelocity = (sgVelocity / Math.Max(1e-8, prices[^3])) * 10_000.0; // Bps relative to center point

        // SG 2nd derivative coefficients: [2, -1, -2, -1, 2] / 7
        double sgAccel = (2.0 * prices[^5] - 1.0 * prices[^4] - 2.0 * prices[^3] - 1.0 * prices[^2] + 2.0 * prices[^1]) / 7.0;
        double instantAcceleration = (sgAccel / Math.Max(1e-8, prices[^3])) * 10_000.0; // Bps

        // 3. 4th-Order Continuous Kalman State Filtering
        double kalmanState = FilterKalmanContinuous(prices);

        string regime;
        double momentumContribution = 0;
        string desc;

        bool isSubMinute = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase);
        // Scale thresholds based on the timeframe resolution.
        // Sub-minute candles represent fractions of a minute, so basis-point velocity per candle is much smaller.
        double velThreshold = isSubMinute ? 0.3 : 3.0;
        double accelThreshold = isSubMinute ? 0.05 : 0.5;
        double decelThreshold = isSubMinute ? 0.2 : 2.0;

        if (instantVelocity > velThreshold && instantAcceleration > accelThreshold)
        {
            regime = "HYPER_ACCELERATING_UP";
            momentumContribution = 0.45;
            desc = $"Непрерывный вектор: Гипер-ускорение ВВЕРХ (Velocity={instantVelocity:F2} bps/s, Accel={instantAcceleration:F3} bps/s²).";
        }
        else if (instantVelocity < -velThreshold && instantAcceleration < -accelThreshold)
        {
            regime = "HYPER_ACCELERATING_DOWN";
            momentumContribution = -0.45;
            desc = $"Непрерывный вектор: Гипер-ускорение ВНИЗ (Velocity={instantVelocity:F2} bps/s, Accel={instantAcceleration:F3} bps/s²).";
        }
        else if (Math.Sign(instantVelocity) != Math.Sign(instantAcceleration) && Math.Abs(instantVelocity) > decelThreshold)
        {
            regime = "DECELERATING";
            momentumContribution = -Math.Sign(instantVelocity) * 0.20;
            desc = $"Непрерывный вектор: Замедление импульса перед разворотом (Deceleration Phase).";
        }
        else
        {
            regime = "STABLE";
            momentumContribution = 0;
            desc = $"Непрерывный вектор: Стабильное ламинарное движение (Velocity={instantVelocity:F1} bps/s).";
        }

        // Интеграция Kalman-фильтра в скоринг:
        // Отклонение текущей цены от kalmanState в базисных пунктах — ведущий сигнал перегрева/недогрева.
        // Цена выше Калмана → импульс вверх; ниже → вниз. Ранее kalmanState вычислялся вхолостую.
        double kalmanDevBps = currentPrice > 1e-8
            ? ((currentPrice - kalmanState) / currentPrice) * 10_000.0
            : 0;
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

    private static double FilterKalmanContinuous(ReadOnlySpan<double> prices)
    {
        double currentPrice = prices[^1];
        // B9-FIX: Clamp noise values to minimum 1e-8 to prevent NaN when currentPrice≈0.
        // Previously: measurementNoise=currentPrice*0.001=0 when price=0 → k=0/(0+0)=NaN → poisons VelocityRegime and StateSignal.
        double processNoise = Math.Max(1e-8, currentPrice * 0.0001);
        double measurementNoise = Math.Max(1e-8, currentPrice * 0.001);

        double est = prices[0];
        double err = Math.Max(1e-8, currentPrice * 0.01);
        
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

