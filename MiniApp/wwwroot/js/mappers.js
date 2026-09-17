// mappers.js - Maps raw C# Backend JSON into a strict, safe UI Model

export function mapBackendResponseToUI(rawData) {
    if (!rawData) {
        throw new Error('Empty response from server');
    }

    // Handle standard vs wrapped response formats seamlessly
    const data = rawData.result ? rawData.result : rawData;
    const config = rawData.config || null;
    const risk = rawData.risk_management || null;
    const flags = rawData.ui_flags || null;

    // Safe extraction with fallbacks
    const getNum = (val, fallback = 0) => typeof val === 'number' && !isNaN(val) ? val : fallback;
    const getStr = (val, fallback = '--') => typeof val === 'string' && val.trim() !== '' ? val : fallback;

    return {
        error: data.error || null,
        reason: data.reason || null,
        message: data.message || null,

        // Config Overrides
        showMl: config ? !!config.ml : true,
        showSmc: config ? !!config.smc : true,
        showOf: config ? !!config.of : true,

        // Core Signal Data
        direction: getStr(data.direction, 'NEUTRAL'),
        probability: getNum(data.probability, 50),
        tfConflict: !!data.tfConflict,
        expiryCandles: getNum(data.expiryCandles, 0),
        duration: getStr(data.duration, '--'),
        
        // Detailed Confidence Scores
        lgbmDirection: getStr(data.lgbmDirection, 'NEUTRAL'),
        lgbmConfidence: getNum(data.lgbmConfidence, 0),
        smcDirection: getStr(data.smcDirection, 'NEUTRAL'),
        smcConfidence: getNum(data.smcConfidence, 0),
        ofDirection: getStr(data.ofDirection, 'NEUTRAL'),
        ofConfidence: getNum(data.ofConfidence, 0),
        taDirection: getStr(data.taDirection, 'NEUTRAL'),
        taConfidence: getNum(data.taConfidence, 0),

        // Market Context
        atr: getNum(data.atr, 0),
        chartData: Array.isArray(data.chartData) ? data.chartData : [],
        chartOhlc: Array.isArray(data.chartOhlc) ? data.chartOhlc : [],
        uiMarketSession: getStr(data.uiMarketSession, '--'),
        uiMarketPhase: getStr(data.uiMarketPhase, '--'),
        uiMarketEntropy: getStr(data.uiMarketEntropy, '--'),
        adaptiveReasoning: getStr(data.adaptiveReasoning, ''),
        
        // Confluence
        goldenSetup: !!data.goldenSetup,
        confluenceLabel: getStr(data.confluenceLabel, ''),
        confluenceRatio: getNum(data.confluenceRatio, 0),

        // Statistics
        winRateAsset: data.winRateAsset !== undefined && data.winRateAsset !== null ? getNum(data.winRateAsset, 0) : null,
        winRateOverall: data.winRateOverall !== undefined && data.winRateOverall !== null ? getNum(data.winRateOverall, 0) : null,
        signalsVerified: getNum(data.signalsVerifiedAsset, 0),
        signalsPending: getNum(data.signalsPending, 0),

        // Risk & UI Flags 
        warningMessage: flags ? getStr(flags.warning_message, '') : '',
        isMlOverruled: flags ? !!flags.is_ml_overruled : false,
        latencyMs: rawData.latency_ms ? getNum(rawData.latency_ms, 0) : 0
    };
}
