using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Time-based Risk Engine (Trade Timeout Engine).
/// Calculates the optimal number of candles to hold a position before it becomes statistically disadvantageous (Stagnant Trade).
/// If a trade does not reach structural targets (TP/SL) within this timeout, it is forced to close to protect margin.
/// </summary>
public class TradeTimeoutEngine : ITradeTimeoutEngine
{
    public record TimeoutResult(
        int TimeoutCandles,
        string TimeoutText,
        string Reasoning
    );

    public TimeoutResult CalculateTimeout(
        string asset,
        string timeframe,
        double atr,
        double volRatio,
        SmcEngine.SmcAnalysisResult smc,
        double currentPrice)
    {
        int baseCandles = 15;
        string dynamicReason = "Base timeout applied (15 candles).";

        // B14-FIX: Use normalized ATR (% of price) instead of absolute value.
        // Previously: `atr < 0.00001` was wrong for all assets:
        //   - SHIB (price ~0.00001): ATR ≈ price → always triggered "dead market" → forced 5-candle timeout
        //   - BTC  (price ~$60,000): ATR ~$500 → never triggered even on fully frozen market
        // Fix: compare ATR/price ratio. Threshold 0.0005 = 0.05% of price (scale-invariant).
        double lastPrice = currentPrice > 0 ? currentPrice : 1.0;
        double normalizedAtr = atr / lastPrice;
        bool isDeadMarket = atr > 0 && normalizedAtr < 0.0005; // < 0.05% of price = frozen market
        bool isZeroAtr = atr <= 0; // completely missing ATR data
        
        // FIX W-18: isZeroAtr was declared but never included in the condition below.
        // ATR=0 means no volatility data at all — should get the shortest timeout (max caution).
        if (isZeroAtr || isDeadMarket || volRatio < 0.3)
        {
            baseCandles = 5;
            dynamicReason = isZeroAtr
                ? "ATR=0: no volatility data. Minimum timeout applied (5 candles)."
                : "Dead market detected (VolRatio < 0.3 or frozen ATR). Extreme fast timeout applied (5 candles).";
        }
        else if (volRatio > 1.5)
        {
            // High volatility -> price should reach target faster. Less patience for stagnation.
            baseCandles = 10;
            dynamicReason = "High Volatility Regime (VolRatio > 1.5). Price should reach target faster. Reduced timeout (10 candles).";
        }
        else if (volRatio < 0.8)
        {
            // Low volatility -> market is slow, needs more time to traverse ATR distance.
            baseCandles = 25;
            dynamicReason = "Low Volatility Regime (VolRatio < 0.8). Market is slow, extended patience required (25 candles).";
        }

        // Structural modification
        if (smc.HasOrderBlock || smc.HasFvg)
        {
            // If entering an OB, the reaction must be sharp and immediate. 
            // Lingering in an OB means it is likely failing.
            baseCandles = (int)(baseCandles * 0.6);
            if (baseCandles < 3) baseCandles = 3; // Reduced minimum from 5 to 3 for ultra-fast scalps
            dynamicReason += " | SMC Alert: Entered at OrderBlock/FVG. Reaction must be immediate. Timeout cut by 40%.";
        }

        string timeoutText = $"{baseCandles} свечей";
        string reasoning = $"Timeout: {timeoutText}. {dynamicReason}";

        return new TimeoutResult(baseCandles, timeoutText, reasoning);
    }
}

