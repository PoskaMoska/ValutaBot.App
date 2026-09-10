using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Trade Timeout Engine.
/// Calculates optimal candle count based on volatility/SMC,
/// then converts to human-readable expiry time (e.g. "1:40", "2 мин").
/// </summary>
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
        string dynamicReason = "Базовая экспирация (3 свечи).";

        double lastPrice = currentPrice > 0 ? currentPrice : 1.0;
        double normalizedAtr = atr / lastPrice;

        // Dead-market threshold
        double baseDeadMarketThreshold = isForex ? 0.000030 : 0.0005;
        double deadMarketThreshold = baseDeadMarketThreshold * (tfSeconds / 60.0);

        bool isDeadMarket = atr > 0 && normalizedAtr < deadMarketThreshold;
        bool isZeroAtr = atr <= 0;

        if (smc.HasOrderBlock || smc.HasFvg || volRatio > 1.5)
        {
            baseCandles = 2;
            dynamicReason = smc.HasOrderBlock || smc.HasFvg 
                ? "SMC сигнал (OB/FVG) или высокий импульс. Быстрая экспирация (2 свечи)."
                : "Высокая волатильность. Быстрая экспирация (2 свечи).";
        }
        else if (isZeroAtr || isDeadMarket)
        {
            // PROACTIVE FIX: Extreme compression (Spike Trap). We cannot extend to 4 candles because time works against us.
            // Hit and Run approach to avoid random algorithmic spikes at the end of the trade.
            baseCandles = 2;
            dynamicReason = "Мертвый рынок (критическое сжатие). Риск спайка — быстрая экспирация (2 свечи).";
        }
        else if (volRatio < 0.8)
        {
            baseCandles = 4;
            dynamicReason = "Низкая волатильность (широкий флэт). Расширенная экспирация (4 свечи).";
        }

        // Sub-minute floor logic (Защита от тикового шума)
        int minCandles = timeframe.ToLower() switch
        {
            "s5"  => 4, // 20 секунд минимум
            "s10" => 3, // 30 секунд минимум
            "s15" => 3, // 45 секунд минимум
            "s30" => 2, // 60 секунд минимум
            _     => 2  // Для M1 и выше минимум 2 свечи
        };

        if (baseCandles < minCandles)
        {
            dynamicReason += $" | Floor: защита от шума, минимум {minCandles} свечей для {timeframe}.";
            baseCandles = minCandles;
        }

        int totalSeconds = baseCandles * tfSeconds;
        string timeoutText = FormatSeconds(totalSeconds);

        return new TimeoutResult(baseCandles, timeoutText, $"Экспирация: {timeoutText}. {dynamicReason}");
    }
}
