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
        bool isForex = false)
    {
        int tfSeconds = TimeframeToSeconds(timeframe);
        bool isSubMinute = tfSeconds < 60;

        int baseCandles = 3;
        string dynamicReason = "Стандартный рынок (3 свечи).";

        double lastPrice = currentPrice > 0 ? currentPrice : 1.0;
        double normalizedAtr = atr / lastPrice;

        // Dead-market threshold
        double baseDeadMarketThreshold = isForex ? 0.000030 : 0.0005;
        double deadMarketThreshold = baseDeadMarketThreshold * (tfSeconds / 60.0);

        bool isDeadMarket = atr > 0 && normalizedAtr < deadMarketThreshold;
        bool isZeroAtr = atr <= 0;

        if (volRatio > 1.5)
        {
            baseCandles = 2;
            dynamicReason = "Высокая волатильность -> Ускорение (2 свечи).";
        }
        else if (isZeroAtr || isDeadMarket)
        {
            baseCandles = 4;
            dynamicReason = "Мертвый рынок -> Замедление (4 свечи).";
        }
        else if (smc.HasOrderBlock || smc.HasFvg)
        {
            baseCandles = 3;
            dynamicReason = "SMC паттерн (OB/FVG) -> Стандарт (3 свечи).";
        }
        else if (volRatio < 0.8)
        {
            baseCandles = 3;
            dynamicReason = "Низкая волатильность -> Стандарт (3 свечи).";
        }

        // Sub-minute floor logic
        if (isSubMinute)
        {
            int minCandles = timeframe.ToLower() switch
            {
                "s5"  => 4, // 20 sec min
                "s10" => 3, // 30 sec min
                "s15" => 3, // 45 sec min
                "s30" => 2, // 60 sec min
                _     => 2
            };

            if (baseCandles < minCandles)
            {
                dynamicReason += $" | Floor: минимум {minCandles} свечи для {timeframe}.";
                baseCandles = minCandles;
            }
        }

        int totalSeconds = baseCandles * tfSeconds;
        string timeoutText = FormatSeconds(totalSeconds);

        return new TimeoutResult(baseCandles, timeoutText, $"Экспирация: {timeoutText}. {dynamicReason}");
    }
}
