using System;

namespace ValutaBot.MiniApp;

public class TradeTimeoutEngine : ITradeTimeoutEngine
{
    public record TimeoutResult(
        int TimeoutCandles,
        string TimeoutText,
        string Reasoning
    );

    private static int TimeframeToSeconds(string timeframe) => timeframe.ToLower() switch
    {
        "s5"  => 5,
        "s10" => 10,
        "s15" => 15,
        "s30" => 30,
        "m1"  => 60,
        "m3"  => 180,
        "m5"  => 300,
        "m15" => 900,
        "m30" => 1800,
        _     => 60
    };

    private static string FormatSeconds(int totalSeconds)
    {
        if (totalSeconds < 60)
            return $"{totalSeconds} сек";
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return seconds == 0 ? $"{minutes} мин" : $"{minutes}:{seconds:D2}";
    }

    public TimeoutResult CalculateTimeout(
        string asset,
        string timeframe,
        double atr,
        double volRatio,
        SmcEngine.SmcAnalysisResult smc,
        double currentPrice,
        ContinuousStateResult state,
        bool isForex = false)
    {
        int tfSeconds = TimeframeToSeconds(timeframe);
        int baseCandles = 3;
        string dynamicReason = "";

        double lastPrice = currentPrice > 0 ? currentPrice : 1.0;

        // 1. Expected Distance to overcome noise/broker latency
        // A minimal target distance in price units. E.g., broker spread is around 1-3 pips.
        double brokerSafeDistance = isForex ? 0.00003 : lastPrice * 0.0005; 
        
        // 2. Velocity evaluation
        double velocityPerSecAbs = state != null ? Math.Abs(state.VelocityBpsPerSec) : 0; 
        double expectedPriceVelocityPerSec = (velocityPerSecAbs * 0.0001) * lastPrice;
        if (expectedPriceVelocityPerSec < 1e-9) expectedPriceVelocityPerSec = 1e-9;

        // 3. Expected Time to Reach Safe Distance (in seconds)
        double expectedSecondsToSafe = brokerSafeDistance / expectedPriceVelocityPerSec;
        
        // 4. Convert Expected Seconds to Candles
        double expectedCandles = expectedSecondsToSafe / tfSeconds;

        // Dynamic Expiration Logic [1..4]
        if (state != null && state.VelocityRegime != null && state.VelocityRegime.StartsWith("HYPER_ACCELERATING"))
        {
            baseCandles = 1;
            dynamicReason = "HYPER_ACCELERATING -> Снайперский пробой (1 свеча).";
        }
        else if (expectedCandles <= 2.0 && velocityPerSecAbs > 0.5)
        {
            baseCandles = 2;
            dynamicReason = $"Высокая скорость (цель за {expectedCandles:F1} св.) -> 2 свечи.";
        }
        else if (expectedCandles > 4.0 || (state != null && state.VelocityRegime == "STABLE"))
        {
            baseCandles = 4;
            dynamicReason = $"Вязкий рынок / STABLE (цель за {expectedCandles:F1} св.) -> 4 свечи (максимум).";
        }
        else 
        {
            if (smc.HasOrderBlock || smc.HasFvg)
            {
                baseCandles = 3;
                dynamicReason = "SMC паттерн (структурный отскок) -> 3 свечи.";
            }
            else
            {
                baseCandles = 3;
                dynamicReason = $"Стандартный тренд (цель за {expectedCandles:F1} св.) -> 3 свечи.";
            }
        }
        
        baseCandles = Math.Clamp(baseCandles, 1, 4);

        int totalSeconds = baseCandles * tfSeconds;
        string timeoutText = FormatSeconds(totalSeconds);

        return new TimeoutResult(baseCandles, timeoutText, $"Экспирация: {timeoutText}. {dynamicReason}");
    }
}
