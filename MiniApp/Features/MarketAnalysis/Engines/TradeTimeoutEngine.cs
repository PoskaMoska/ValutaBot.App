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

        // Expiration is always 1 candle = 1 timeframe period.
        // The ML model (TARGET_HORIZON_CANDLES=1) predicts exactly 1 candle ahead,
        // which matches PocketOption's real usage:
        //   - 1m chart -> 1m expiry
        //   - s5 chart -> 5s expiry
        // The old dynamic 1-4 candle logic was calibrated for the old H=5 model and is no longer valid.
        const int baseCandles = 1;
        string dynamicReason = "Горизонт модели = 1 свеча → экспирация 1 таймфрейм.";

        int totalSeconds = baseCandles * tfSeconds;
        string timeoutText = FormatSeconds(totalSeconds);

        return new TimeoutResult(baseCandles, timeoutText, $"Экспирация: {timeoutText}. {dynamicReason}");
    }
}
