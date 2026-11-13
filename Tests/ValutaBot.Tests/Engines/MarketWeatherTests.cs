using System;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class MarketWeatherTests
{
    private static MiniAppController.OhlcCandle[] BuildLinearTrendCandles(int count, double startPrice, double stepPerBar, double wickHalfSpread = 0.00003)
    {
        var candles = new MiniAppController.OhlcCandle[count];
        var t0 = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
        double px = startPrice;
        for (int i = 0; i < count; i++)
        {
            double o = px;
            double c = px + stepPerBar;
            double hi = Math.Max(o, c) + wickHalfSpread;
            double lo = Math.Min(o, c) - wickHalfSpread;
            candles[i] = new MiniAppController.OhlcCandle(o, hi, lo, c, 100, t0.AddSeconds(i * 10));
            px = c;
        }
        return candles;
    }

    private static MiniAppController.OhlcCandle[] BuildWhipsawCandles(int count, double basePrice, double swing)
    {
        var candles = new MiniAppController.OhlcCandle[count];
        var t0 = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
        double px = basePrice;
        for (int i = 0; i < count; i++)
        {
            double next = basePrice + ((i % 2 == 0) ? swing : -swing);
            double hi = Math.Max(px, next) + swing * 0.5;
            double lo = Math.Min(px, next) - swing * 0.5;
            candles[i] = new MiniAppController.OhlcCandle(px, hi, lo, next, 150, t0.AddMinutes(i));
            px = next;
        }
        return candles;
    }

    [Fact]
    public void EvaluateMarketWeather_SessionDetectsOtcWeekendCryptoClosedAndActiveSessionsTruthfully()
    {
        var candles = BuildLinearTrendCandles(20, 1.1300, 0.00005);
        var normalNoise = new SubminuteNoiseGatekeeper.NoiseAuditResult(true, 1.1, 1.8, 1.0, "Импульс подтвержден");

        var otc = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: true, isAssetOpen: true, isOtcFeed: true,
            utcNow: new DateTime(2026, 11, 13, 14, 30, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.4, mainAdx: 22.0, taScore: 0.3, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.8, closedCandles: candles);
        Assert.Equal("Внебиржевая (OTC)", otc.Session);

        var weekend = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: false, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.4, mainAdx: 22.0, taScore: 0.3, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.8, closedCandles: candles);
        Assert.Equal("Внебиржевая (OTC)", weekend.Session);

        var crypto = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: false, isSubminute: false, isAssetOpen: true, isOtcFeed: true,
            utcNow: new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.4, mainAdx: 22.0, taScore: 0.3, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.8, closedCandles: candles);
        Assert.Equal("КРИПТО (24/7)", crypto.Session);

        var closed = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: false, isAssetOpen: false, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.4, mainAdx: 22.0, taScore: 0.3, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.8, closedCandles: candles);
        Assert.Equal("Рынок закрыт", closed.Session);

        var nyVolume = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: false, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.4, mainAdx: 22.0, taScore: 0.3, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.8, closedCandles: candles);
        Assert.Equal("Нью-Йорк (Объемы)", nyVolume.Session);

        var nyEvening = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: false, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 18, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.4, mainAdx: 22.0, taScore: 0.3, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.8, closedCandles: candles);
        Assert.Equal("Нью-Йорк (Вечер)", nyEvening.Session);
    }

    [Fact]
    public void EvaluateMarketWeather_CleanSubminuteMoveIsSafeNotFalseChaos_AndWhipsawIsChaos()
    {
        // Normal clean subminute upward trend (0.8 bps/bar velocity used to falsely trigger dangerVel=0.3!)
        var cleanTrend = BuildLinearTrendCandles(20, 1.1300, 0.00009, 0.00002);
        var cleanNoise = new SubminuteNoiseGatekeeper.NoiseAuditResult(true, 1.3, 1.6, 1.0, "Импульс подтвержден");

        var cleanWeather = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: true, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "HYPER_ACCELERATING_UP", velocityBpsPerBar: 0.80, mainAdx: 28.0, taScore: 0.60, rsiVal: 60.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: cleanNoise, atrBps: 1.6, closedCandles: cleanTrend);

        Assert.Equal("В норме (Безопасно)", cleanWeather.Entropy);
        Assert.False(cleanWeather.IsHighChaos);
        Assert.Equal("Бычий импульс", cleanWeather.Phase);

        // High-amplitude whipsaw (low Efficiency Ratio + high Z-score + wide swings)
        var whipsawCandles = BuildWhipsawCandles(20, 1.1300, 0.00025);
        var whipsawNoise = new SubminuteNoiseGatekeeper.NoiseAuditResult(false, 2.3, 4.0, 1.5, "Локальное затухание волатильности");

        var whipsawWeather = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: true, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.9, mainAdx: 14.0, taScore: 0.05, rsiVal: 50.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: whipsawNoise, atrBps: 4.0, closedCandles: whipsawCandles);

        Assert.Equal("ВЫСОКАЯ (Хаос / Опасно!)", whipsawWeather.Entropy);
        Assert.True(whipsawWeather.IsHighChaos);
    }

    [Fact]
    public void EvaluateMarketWeather_CoherentPhaseAndSubNoise_NoBounceOrDeadContradictions()
    {
        var downTrend = BuildLinearTrendCandles(20, 1.1350, -0.00008, 0.00002);
        var normalNoise = new SubminuteNoiseGatekeeper.NoiseAuditResult(true, 1.0, 1.5, 1.2, "Импульс подтвержден");

        // Short-term bounce (HYPER_ACCELERATING_UP on last 5 bars) inside structural bear trend
        var bounceInDowntrend = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: false, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "HYPER_ACCELERATING_UP", velocityBpsPerBar: 0.7, mainAdx: 27.0, taScore: -0.45, rsiVal: 41.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.5, closedCandles: downTrend);
        Assert.Equal("Отскок (Коррекция)", bounceInDowntrend.Phase);

        // Sub-noise choppy drift must warn "Повышенная (Шум)" and not claim "Бычий импульс"
        var whipsawLowAtr = BuildWhipsawCandles(20, 1.1300, 0.00004);
        var subNoise = new SubminuteNoiseGatekeeper.NoiseAuditResult(false, 0.3, 0.65, 1.2, "Шумовой флэт");
        var choppySubNoise = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: true, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.25, mainAdx: 13.0, taScore: 0.05, rsiVal: 55.0,
            bbSqueeze: false, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: subNoise, atrBps: 0.65, closedCandles: whipsawLowAtr);
        Assert.Equal("Повышенная (Шум)", choppySubNoise.Entropy);
        Assert.Contains("Флэт", choppySubNoise.Phase, StringComparison.OrdinalIgnoreCase);

        // Bollinger squeeze on low ADX in a tight horizontal compression range
        var squeezeCandles = BuildWhipsawCandles(20, 1.1300, 0.00004);
        var squeeze = MarketAnalysisOrchestrator.EvaluateMarketWeather(
            isForex: true, isSubminute: false, isAssetOpen: true, isOtcFeed: false,
            utcNow: new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc),
            velocityRegime: "STABLE", velocityBpsPerBar: 0.3, mainAdx: 15.0, taScore: 0.1, rsiVal: 51.0,
            bbSqueeze: true, isRiskBlocked: false, gatekeeperReason: "", noiseAudit: normalNoise, atrBps: 1.4, closedCandles: squeezeCandles);
        Assert.Equal("Сжатие (Ожидание)", squeeze.Phase);
    }
}
