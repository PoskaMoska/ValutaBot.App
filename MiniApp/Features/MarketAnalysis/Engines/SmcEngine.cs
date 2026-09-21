using System;
using ValutaBot.MiniApp.Indicators;

namespace ValutaBot.MiniApp;

/// <summary>
/// Smart Money Concepts (SMC) & Institutional Liquidity Engine.
/// Stateless API delegating structural tracking to IndicatorCache & StatefulSmc.
/// </summary>
public static class SmcEngine
{
    public readonly record struct SmcAnalysisResult(
        bool HasLiquiditySweep,
        string SweepDirection, 
        bool HasFvg,
        string FvgType, 
        double FvgTop,
        double FvgBottom,
        double FvgGapSize,
        bool HasOrderBlock,
        string OrderBlockType, 
        double OrderBlockLevel,
        bool IsUnmitigatedOb, 
        bool HasBos,
        string BosDirection
    );

    public readonly record struct MtfSmcValidationResult(
        bool IsAlignedWithHtf,
        double ConfluenceMultiplier,
        string AlignmentStatus
    );

    public static SmcAnalysisResult AnalyzeSmcStructure(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice)
    {
        if (candles.Length < 10)
        {
            return new SmcAnalysisResult(false, "NONE", false, "NONE", 0, 0, 0, false, "NONE", 0, false, false, "NONE");
        }

        // Delegate to IndicatorCache which manages StatefulSmc instances per asset/timeframe
        var smc = TechnicalAnalysisEngine.Instance.GetSmcState(asset, timeframe, candles, currentPrice);

        var (hasBullFvg, hasBearFvg, nearestFvg) = smc.GetNearestFvg(currentPrice);
        var (hasBullOb, hasBearOb, nearestOb) = smc.GetNearestOb(currentPrice);

        string fvgType = hasBullFvg ? "BULLISH_FVG" : hasBearFvg ? "BEARISH_FVG" : "NONE";
        string obType = hasBullOb ? "BULLISH_OB" : hasBearOb ? "BEARISH_OB" : "NONE";

        return new SmcAnalysisResult(
            smc.HasLiquiditySweep,
            smc.SweepDirection,
            hasBullFvg || hasBearFvg,
            fvgType,
            nearestFvg?.Top ?? 0,
            nearestFvg?.Bottom ?? 0,
            nearestFvg.HasValue ? Math.Abs(nearestFvg.Value.Top - nearestFvg.Value.Bottom) : 0,
            hasBullOb || hasBearOb,
            obType,
            hasBullOb ? (nearestOb?.Top ?? 0) : (hasBearOb ? (nearestOb?.Bottom ?? 0) : 0),
            nearestOb.HasValue,
            smc.HasBos,
            smc.BosDirection
        );
    }

    public static MtfSmcValidationResult ValidateMtfSmcAlignment(SmcAnalysisResult mainSmc, SmcAnalysisResult htfSmc)
    {
        // Compute HTF net directional score (+ for Bullish, - for Bearish)
        int htfScore = 0;
        if (htfSmc.SweepDirection == "BULLISH_SWEEP") htfScore += 2;
        else if (htfSmc.SweepDirection == "BEARISH_SWEEP") htfScore -= 2;
        if (htfSmc.BosDirection == "BULLISH_BOS") htfScore += 2;
        else if (htfSmc.BosDirection == "BEARISH_BOS") htfScore -= 2;
        if (htfSmc.OrderBlockType == "BULLISH_OB") htfScore += 1;
        else if (htfSmc.OrderBlockType == "BEARISH_OB") htfScore -= 1;
        if (htfSmc.FvgType == "BULLISH_FVG") htfScore += 1;
        else if (htfSmc.FvgType == "BEARISH_FVG") htfScore -= 1;

        // Compute local net directional score
        int mainScore = 0;
        if (mainSmc.SweepDirection == "BULLISH_SWEEP") mainScore += 2;
        else if (mainSmc.SweepDirection == "BEARISH_SWEEP") mainScore -= 2;
        if (mainSmc.BosDirection == "BULLISH_BOS") mainScore += 2;
        else if (mainSmc.BosDirection == "BEARISH_BOS") mainScore -= 2;
        if (mainSmc.OrderBlockType == "BULLISH_OB") mainScore += 1;
        else if (mainSmc.OrderBlockType == "BEARISH_OB") mainScore -= 1;
        if (mainSmc.FvgType == "BULLISH_FVG") mainScore += 1;
        else if (mainSmc.FvgType == "BEARISH_FVG") mainScore -= 1;

        bool htfBullish = htfScore > 0;
        bool htfBearish = htfScore < 0;
        bool mainBullish = mainScore > 0;
        bool mainBearish = mainScore < 0;

        if (mainBullish && htfBearish)
        {
            BotLogger.Warn("[MTF SMC Filter] Counter-Trend Conflict! Local BUY opposes HTF BEARISH structure.");
            return new MtfSmcValidationResult(false, 0.30, "COUNTER_TREND_CONFLICT");
        }
        if (mainBearish && htfBullish)
        {
            BotLogger.Warn("[MTF SMC Filter] Counter-Trend Conflict! Local PUT opposes HTF BULLISH structure.");
            return new MtfSmcValidationResult(false, 0.30, "COUNTER_TREND_CONFLICT");
        }

        if ((mainBullish && htfBullish) || (mainBearish && htfBearish))
        {
            return new MtfSmcValidationResult(true, 1.40, "ALIGNED");
        }

        return new MtfSmcValidationResult(true, 1.0, "NEUTRAL");
    }
}

