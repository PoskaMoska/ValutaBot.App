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

        int baseCandles = 15;
        string dynamicReason = "Base timeout applied (15 candles).";

        double lastPrice = currentPrice > 0 ? currentPrice : 1.0;
        double normalizedAtr = atr / lastPrice;

        // Dead-market threshold calibrated for m1 (60s), then scaled linearly by timeframe.
        // EUR/USD m1 ATR ≈ 0.0002 → normalizedAtr ≈ 0.000182. Dead threshold = 0.000030 (< 16% of normal).
        // EUR/USD s5 ATR ≈ 0.000007 → normalizedAtr ≈ 0.0000064. Dead threshold = 0.0000025 (scales with TF).
        // Crypto BTC m1 ATR ≈ $30 → normalizedAtr ≈ 0.0005. Threshold = 0.0005, scales for sub-minute.
        double baseDeadMarketThreshold = isForex ? 0.000030 : 0.0005;
        double deadMarketThreshold = baseDeadMarketThreshold * (tfSeconds / 60.0);

        bool isDeadMarket = atr > 0 && normalizedAtr < deadMarketThreshold;
        bool isZeroAtr = atr <= 0;

        if (isZeroAtr || isDeadMarket || volRatio < 0.3)
        {
            baseCandles = 5;
            dynamicReason = isZeroAtr
                ? "ATR=0: no volatility data. Minimum timeout (5 candles)."
                : "Dead market detected. Fast timeout (5 candles).";
        }
        else if (volRatio > 1.5)
        {
            baseCandles = 10;
            dynamicReason = "High Volatility. Reduced timeout (10 candles).";
        }
        else if (volRatio < 0.8)
        {
            // On sub-minute TFs, "Low Volatility" extended timeout is capped at 15 candles:
            // 25 candles × 5s = 125 sec is too long for a scalping signal.
            baseCandles = isSubMinute ? 15 : 25;
            dynamicReason = "Low Volatility. Extended timeout " + (isSubMinute ? "(15 candles, sub-minute cap)." : "(25 candles).");
        }

        if (smc.HasOrderBlock || smc.HasFvg)
        {
            baseCandles = (int)(baseCandles * 0.6);
            if (baseCandles < 3) baseCandles = 3;
            dynamicReason += " | SMC: OrderBlock/FVG detected. Timeout cut by 40%.";
        }

        // Per-timeframe minimum candle floor.
        // s5: min 5 candles = 25s (PocketOption minimum expiry).
        // Other sub-minute TFs get proportional floors.
        int minCandles = timeframe.ToLower() switch
        {
            "s5"  => 5,
            "s10" => 3,
            "s15" => 3,
            "s30" => 3,
            _     => 3
        };
        if (baseCandles < minCandles)
        {
            dynamicReason += $" | Floor: minimum {minCandles} candles enforced for {timeframe}.";
            baseCandles = minCandles;
        }

        int totalSeconds = baseCandles * tfSeconds;
        string timeoutText = FormatSeconds(totalSeconds);

        return new TimeoutResult(baseCandles, timeoutText, $"Экспирация: {timeoutText}. {dynamicReason}");
    }
}
