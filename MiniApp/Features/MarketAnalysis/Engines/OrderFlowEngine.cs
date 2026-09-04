using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Order Flow & Volume Delta Imbalance Engine for Forex & OTC market pairs.
/// Filters out HFT micro-noise, TWAP/VWAP algorithmic noise, and Spoofing traps.
/// Focuses exclusively on Institutional Block Trades, Volume Cluster Anomalies, and Real Momentum Progress.
/// </summary>
public static class OrderFlowEngine
{
    public readonly record struct OrderFlowResult(
        double BuyVolume,
        double SellVolume,
        double DeltaRatio,
        double CumulativeVolumeDelta,
        string OrderFlowState, // "STRONG_BULLISH_FLOW" | "STRONG_BEARISH_FLOW" | "BULLISH_ABSORPTION" | "BEARISH_ABSORPTION" | "Ловушка (Спуфинг)" | "БАЛАНС"
        double ScoreContribution,
        bool IsInstitutionalBlockTrade,
        string Description
    );

    public static OrderFlowResult AnalyzeOrderFlow(
        string asset,
        string timeframe,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles,
        double currentPrice)
    {
        if (candles.IsEmpty || candles.Length < 5)
        {
            return new OrderFlowResult(0, 0, 1.0, 0, "БАЛАНС", 0, false, "НЕДОСТАТОЧНО ДАННЫХ");
        }

        var statefulOf = IndicatorCache.GetOrderFlow(asset, timeframe, candles);
        statefulOf.Update(candles);

        double deltaRatio = statefulOf.DeltaRatio;
        double priceDelta = statefulOf.PriceDelta;
        double priceDeltaBps = (priceDelta / Math.Max(1e-8, currentPrice)) * 10000.0;
        double cvd = statefulOf.CumulativeVolumeDelta;
        double recentCvd = statefulOf.BuyVolume - statefulOf.SellVolume; // B12-FIX: Local CVD slope

        string state;
        double scoreContribution = 0;
        string desc = "БАЛАНС";

        // ─── 2. Spoofing & Absorption Detection (BPS Based) ───
        if (deltaRatio > 1.8 && recentCvd > 0 && priceDeltaBps < -0.1)
        {
            state = "BEARISH_ABSORPTION";
            scoreContribution = -0.30;
            desc = "Дивергенция (Скрытые продажи)";
        }
        else if (deltaRatio < 0.55 && recentCvd < 0 && priceDeltaBps > 0.1)
        {
            state = "BULLISH_ABSORPTION";
            scoreContribution = 0.30;
            desc = "Дивергенция (Скрытые покупки)";
        }
        else if (deltaRatio > 1.8 && Math.Abs(priceDeltaBps) < 0.05)
        {
            state = "Ловушка (Спуфинг)";
            scoreContribution = 0;
            desc = "Ловушка (Спуфинг)";
        }
        // ─── 3. Passive Limit Absorption Detection ───
        else if (deltaRatio > 1.8 && priceDeltaBps <= -0.5)
        {
            state = "BEARISH_ABSORPTION";
            scoreContribution = -0.35;
            desc = "Лимитное поглощение (Продажи)";
        }
        else if (deltaRatio < 0.55 && priceDeltaBps >= 0.5)
        {
            state = "BULLISH_ABSORPTION";
            scoreContribution = 0.35;
            desc = "Лимитное поглощение (Покупки)";
        }
        // ─── 4. Real Institutional Momentum Flow ───
        else if (deltaRatio >= 1.6 && priceDelta > 0)
        {
            state = "STRONG_BULLISH_FLOW";
            scoreContribution = statefulOf.HasInstitutionalBlockTrade ? 0.5 : 0.35;
            desc = statefulOf.HasInstitutionalBlockTrade ? "Крупный блок (Покупки)" : "Растущий объем (Покупки)";
        }
        else if (deltaRatio <= 0.62 && priceDelta < 0)
        {
            state = "STRONG_BEARISH_FLOW";
            scoreContribution = statefulOf.HasInstitutionalBlockTrade ? -0.5 : -0.35;
            desc = statefulOf.HasInstitutionalBlockTrade ? "Крупный блок (Продажи)" : "Падающий объем (Продажи)";
        }
        else
        {
            state = "БАЛАНС";
            scoreContribution = 0;
        }

        return new OrderFlowResult(
            BuyVolume: Math.Round(statefulOf.BuyVolume, 1),
            SellVolume: Math.Round(statefulOf.SellVolume, 1),
            DeltaRatio: Math.Round(deltaRatio, 2),
            CumulativeVolumeDelta: Math.Round(cvd, 2),
            OrderFlowState: state,
            ScoreContribution: Math.Round(scoreContribution, 2),
            IsInstitutionalBlockTrade: statefulOf.HasInstitutionalBlockTrade,
            Description: desc
        );
    }
}

