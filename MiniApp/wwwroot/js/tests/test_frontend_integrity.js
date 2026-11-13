const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

// 1. Read frontend assets
const htmlPath = path.resolve(__dirname, '../../index.html');
const rawHtml = fs.readFileSync(htmlPath, 'utf8');

const apiJsPath = path.resolve(__dirname, '../api.js');
let apiJs = fs.readFileSync(apiJsPath, 'utf8');
apiJs = apiJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const uiJsPath = path.resolve(__dirname, '../ui.js');
let uiJs = fs.readFileSync(uiJsPath, 'utf8');
uiJs = uiJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

function setupDOM() {
    const dom = new JSDOM(rawHtml, { runScripts: "dangerously", url: "http://localhost" });
    const { window } = dom;
    const { document } = window;

    // Headless safe stubs for canvas and animation
    window.requestAnimationFrame = (cb) => { cb(); return 1; };
    window.cancelAnimationFrame = (id) => clearTimeout(id);
    window.showAiChart = () => {};
    window.hideAiChart = () => {};
    window.updateAiChartData = () => {};
    window.flashResults = () => {};

    // Mock TG WebApp environment
    window.tg = {
        initData: "mock_tg_token",
        platform: "tdesktop"
    };
    window.getCustomInitData = () => "mock_tg_token";
    window.currentAsset = "EURUSD";
    window.currentTf = "s10";

    const globalShim = `
var currentTf = window.currentTf || "s10";
var currentAsset = window.currentAsset || "EURUSD";
var tg = window.tg;
var getCustomInitData = window.getCustomInitData || function() { return ""; };
`;

    // Eval UI and API code inside window context
    try {
        let transformedUi = globalShim + '\n' + uiJs
            .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
            .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
            .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
            .replace(/export\s*\{[^}]*\};?/g, '/* mocked export block */');
        window.eval(transformedUi);

        // Headless safe stubs for canvas and animation (must be AFTER eval to avoid being overwritten)
        window.requestAnimationFrame = (cb) => setTimeout(cb, 5);
        window.cancelAnimationFrame = (id) => clearTimeout(id);
        window.showAiChart = () => {};
        window.hideAiChart = () => {};
        window.updateAiChartData = () => {};
        window.renderAiChartLoop = () => {};
        window.flashResults = () => {};

        let transformedApi = globalShim + '\n' + apiJs
            .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
            .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
            .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
            .replace(/export\s*\{[^}]*\};?/g, '/* mocked export block */');
        window.eval(transformedApi);
    } catch (e) {
        console.error("Evaluation error during setup:", e);
        throw e;
    }

    return { window, document };
}

let totalTests = 0;
let passedTests = 0;
let failedTests = 0;

function assert(condition, testName, details = '') {
    totalTests++;
    if (condition) {
        passedTests++;
        console.log(`  [PASS] ${testName}`);
    } else {
        failedTests++;
        console.error(`  [FAIL] ${testName} - ${details}`);
    }
}

function assertEq(testName, actual, expected) {
    assert(actual === expected, testName, `Expected: '${expected}', Got: '${actual}'`);
}

function getVal(document, id) {
    const el = document.getElementById(id);
    if (!el) return '';
    if (id === 'resDir') return String(el.innerText || el.innerHTML || '').trim();
    if (el.innerText !== undefined) return String(el.innerText).trim();
    return String(el.textContent || el.innerHTML || '').trim();
}

async function testScenario(name, mockPayload, assertionsFn) {
    console.log(`\n=======================================================`);
    console.log(`TEST SUITE: ${name}`);
    console.log(`=======================================================`);

    const { window, document } = setupDOM();

    window.fetch = async (url) => {
        await new Promise(r => setTimeout(r, 20));
        if (url.includes('/api/chart-ohlc')) {
            return { ok: true, text: async () => "[]", json: async () => [] };
        }
        return {
            ok: true,
            text: async () => JSON.stringify(mockPayload),
            json: async () => mockPayload
        };
    };

    window.executeAnalysis();

    // Wait for setTimeout in api.js (500ms max delay)
    await new Promise(r => setTimeout(r, 700));

    assertionsFn(document, window);
}

async function runAllTests() {
    // ----------------------------------------------------
    // Scenario 1: High Conviction Golden Signal (BUY 65%)
    // ----------------------------------------------------
    const payloadScenario1 = {
        result: {
            direction: "BUY",
            probability: 65,
            duration: "10 сек",
            expiryCandles: 1,
            adaptiveReasoning: "Strong bullish momentum confirmed by PatchTST neural brain.",
            winRateOverall: 58.4,
            winRateAsset: 63.8,
            signalsVerifiedAsset: 14,
            rsi: 61.5,
            ema: 1.12345,
            volumeStrength: 1.8,
            atr: 0.00035,
            chartData: [1.1231, 1.1232, 1.12345],
            chartOhlc: [{ o: 1.1231, h: 1.1233, l: 1.1230, c: 1.12345 }],
            uiMarketSession: "Европейская сессия",
            uiMarketPhase: "Бычий импульс",
            uiMarketEntropy: "НОРМАЛЬНАЯ (Безопасно)",
            lgbmDirection: "BUY",
            lgbmConfidence: 68,
            lgbmModelVersion: "patch_tst_v3",
            taDirection: "BUY",
            taConfidence: 65,
            smcDirection: "BUY",
            smcConfidence: 62,
            ofDirection: "NEUTRAL",
            ofConfidence: 0,
            goldenSetup: true,
            confluenceLabel: "💎 ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
            confluenceRatio: 1.0,
            llmReport: "Нейромозг PatchTST v3 подтверждает устойчивый восходящий тренд."
        },
        config: { ml: true, smc: true, of: false },
        risk_management: { kelly_percentage: 12.5, volatility_filter_passed: true }
    };

    await testScenario("Scenario 1: High Conviction BUY 65% (Golden Setup)", payloadScenario1, (document) => {
        // 1. Direct Data Integrity (No distortion)
        assertEq("Direction displays 'ВВЕРХ'", getVal(document, 'resDir'), 'ВВЕРХ');
        assertEq("Probability displays '65%'", getVal(document, 'resProb'), '65%');
        assertEq("Duration displays '10 сек'", getVal(document, 'resDur'), '10 сек');
        assertEq("RSI matches backend value 61.5", getVal(document, 'resRsi'), '61.5');
        assertEq("EMA matches backend value 1.12345", getVal(document, 'resEma'), '1.12345');
        assertEq("Volume displays '↑ 1.8x'", getVal(document, 'resVol'), '↑ 1.8x');
        assertEq("Market Session matches exactly", getVal(document, 'weatherSession'), 'Европейская сессия');
        assertEq("Market Phase matches exactly", getVal(document, 'weatherPhase'), 'Бычий импульс');
        assertEq("Market Entropy matches exactly", getVal(document, 'weatherEntropy'), 'НОРМАЛЬНАЯ (Безопасно)');
        assertEq("Scenario 1 Session color is green", document.getElementById('weatherSession').style.color, 'rgb(16, 185, 129)');
        assertEq("Scenario 1 Phase color is green", document.getElementById('weatherPhase').style.color, 'rgb(16, 185, 129)');
        assertEq("Scenario 1 Entropy color is green", document.getElementById('weatherEntropy').style.color, 'rgb(16, 185, 129)');
        assertEq("Scenario 1 Weather Title color is green", document.getElementById('weatherTitle').style.color, 'rgb(16, 185, 129)');
        
        // 2. Fact Measured Accuracy
        assert(getVal(document, 'resProbFact').includes('факт: 64%'), "Fact measured winrate displays 'факт: 64%'");

        // 3. ML & Consensus Radar Integrity
        assert(getVal(document, 'radarMl').includes('ВВЕРХ (68%)'), "Radar ML displays 'ВВЕРХ (68%)'");
        assertEq("Radar TA is removed (no indicator noise)", getVal(document, 'radarTa'), '');
        assertEq("Radar SMC is removed (no indicator noise)", getVal(document, 'radarSmc'), '');
        assertEq("Radar OF is disabled (empty, no ghost data)", getVal(document, 'radarOf'), '');

        // 4. Golden Setup Badge
        assertEq("Golden badge main is visible", document.getElementById('goldenSetupBadgeMain').style.display, 'inline-block');
        assertEq("Main sphere has buy-signal class", document.getElementById('mainSphere').classList.contains('buy-signal'), true);

        // 5. Copilot Card removed from UI
        assertEq("AI Copilot card is absent from DOM", document.getElementById('aiCopilotCard'), null);

        // 6. Expiry Candle matches final BUY direction and keeps bold body
        const buyBody = document.querySelector('#durChart .exp-body');
        assert(buyBody !== null && buyBody.classList.contains('exp-bull'), "Expiry candle uses green exp-bull class for BUY");
        assert(buyBody !== null && parseFloat(buyBody.style.height) >= 20, "Expiry candle keeps thick body height (>= 20px)");
    });

    // ----------------------------------------------------
    // Scenario 2: Weak Signal in Consolidation / Flat (PUT 52%)
    // ----------------------------------------------------
    const payloadScenario2 = {
        result: {
            direction: "PUT",
            probability: 52,
            duration: "1 мин",
            expiryCandles: 1,
            winRateOverall: null,
            winRateAsset: null,
            signalsVerifiedAsset: 0,
            rsi: 48.0,
            ema: 159.25,
            volumeStrength: -0.05, // Balance
            atr: 0.00008, // Low ATR
            chartData: [159.26, 159.255, 159.25],
            chartOhlc: [{ o: 159.26, h: 159.27, l: 159.24, c: 159.25 }],
            uiMarketSession: "Азиатский флэт (OTC)",
            uiMarketPhase: "Флэт / Консолидация",
            uiMarketEntropy: "ВЫСОКАЯ (Штиль / Брокерский шум)",
            lgbmDirection: "PUT",
            lgbmConfidence: 51,
            lgbmModelVersion: "patch_tst_v3",
            taDirection: "NEUTRAL",
            taConfidence: 50,
            smcDirection: "NEUTRAL",
            smcConfidence: 50,
            ofDirection: "NEUTRAL",
            ofConfidence: 0,
            goldenSetup: false,
            confluenceLabel: "💎 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)",
            confluenceRatio: 0.33,
            llmReport: null
        },
        config: { ml: true, smc: true, of: false },
        risk_management: { kelly_percentage: 0.0, volatility_filter_passed: false }
    };

    await testScenario("Scenario 2: Low-volatility PUT 52% in Consolidation", payloadScenario2, (document) => {
        assertEq("Direction displays 'ВНИЗ'", getVal(document, 'resDir'), 'ВНИЗ');
        assertEq("Probability displays '52%'", getVal(document, 'resProb'), '52%');
        assertEq("Duration displays '1 мин'", getVal(document, 'resDur'), '1 мин');
        assertEq("Volume displays 'Баланс' for near-zero volume", getVal(document, 'resVol'), 'Баланс');
        
        // Missing stats should display clean fallback without crashing or showing 'NaN%'
        assertEq("Fact measured winrate is blank when no samples", getVal(document, 'resProbFact'), '');
        
        // Golden Badge MUST be hidden
        assertEq("Main Golden badge is hidden", document.getElementById('goldenSetupBadgeMain').style.display, 'none');

        // Radar checks for neutral/put
        assert(getVal(document, 'radarMl').includes('ВНИЗ (51%)'), "Radar ML displays 'ВНИЗ (51%)'");
        assertEq("Radar TA is removed", getVal(document, 'radarTa'), '');
        assertEq("Radar SMC is removed", getVal(document, 'radarSmc'), '');

        // Market weather colors in noisy flat OTC consolidation
        assertEq("Scenario 2 OTC Session color is purple", document.getElementById('weatherSession').style.color, 'rgb(139, 92, 246)');
        assertEq("Scenario 2 Flat Phase color is amber", document.getElementById('weatherPhase').style.color, 'rgb(245, 158, 11)');
        assertEq("Scenario 2 High Noise Entropy color is red", document.getElementById('weatherEntropy').style.color, 'rgb(239, 68, 68)');
        assertEq("Scenario 2 Weather Title color reflects red danger", document.getElementById('weatherTitle').style.color, 'rgb(239, 68, 68)');

        // Expiry candle matches final PUT direction
        const putBody = document.querySelector('#durChart .exp-body');
        assert(putBody !== null && putBody.classList.contains('exp-bear'), "Expiry candle uses red exp-bear class for PUT");
        assert(putBody !== null && parseFloat(putBody.style.height) >= 20, "Expiry candle keeps thick body height (>= 20px) for PUT");
    });

    // ----------------------------------------------------
    // Scenario 3: Doji OHLC & Direction-Driven Expiry Candle
    // ----------------------------------------------------
    const payloadNeutral = {
        result: {
            direction: "NEUTRAL",
            probability: 50,
            duration: "1 мин",
            expiryCandles: 1,
            chartData: [1.1000, 1.1000, 1.1000],
            chartOhlc: [{ o: 1.1000, h: 1.1010, l: 1.0999, c: 1.1000 }],
            uiMarketSession: "Лондон (Начало)",
            uiMarketPhase: "Боковик (Штиль)",
            uiMarketEntropy: "Повышенная (Шум)"
        },
        config: { ml: true, smc: true, of: false }
    };

    await testScenario("Scenario 3: Doji OHLC with NEUTRAL direction & override checks", payloadNeutral, (document, window) => {
        const neutralBody = document.querySelector('#durChart .exp-body');
        const neutralWick = document.querySelector('#durChart .exp-wick');
        assert(neutralBody !== null && neutralBody.classList.contains('exp-neutral'), "Neutral final direction renders exp-neutral purple candle");
        assert(neutralWick !== null && neutralWick.classList.contains('exp-neutral'), "Neutral expiry wick matches exp-neutral class");
        assert(neutralBody !== null && parseFloat(neutralBody.style.height) >= 20, "Doji OHLC still renders thick readable body (>= 20px)");

        // Even if the historical OHLC candle closed green (c > o), final direction 'PUT' forces red exp-bear
        window.renderExpiryCandles('durChart', [{ o: 1.1000, h: 1.1010, l: 1.0999, c: 1.1008 }], 1, 'PUT');
        const forcedPutBody = document.querySelector('#durChart .exp-body');
        assert(forcedPutBody !== null && forcedPutBody.classList.contains('exp-bear'), "Bullish historical candle becomes exp-bear when final direction is PUT");

        // Flat gravestone doji with missing c>o becomes green exp-bull when final direction is BUY, and retains tall body
        window.renderExpiryCandles('durChart', [{ o: 1.1000, h: 1.1025, l: 1.0999, c: 1.1000 }], 1, 'BUY');
        const forcedBuyBody = document.querySelector('#durChart .exp-body');
        assert(forcedBuyBody !== null && forcedBuyBody.classList.contains('exp-bull'), "Gravestone doji becomes exp-bull when final direction is BUY");
        assert(forcedBuyBody !== null && parseFloat(forcedBuyBody.style.height) >= 20, "Gravestone doji body remains thick (>= 20px)");
    });

    // Ensure Market Weather stays 1x4 horizontally in style.css (never 1fr 1fr 2x2 grid)
    const styleCss = fs.readFileSync(path.resolve(__dirname, '../../css/style.css'), 'utf8');
    assert(!styleCss.includes('grid-template-columns: 1fr 1fr !important'), "Market Weather does not collapse to 2x2 on narrow viewports");
    assert(styleCss.includes('repeat(4, minmax(0, 1fr))'), "Market Weather maintains 4-column horizontal grid");

    console.log(`\n=======================================================`);
    console.log(`TOTAL TESTS: ${totalTests} | PASSED: ${passedTests} | FAILED: ${failedTests}`);
    console.log(`=======================================================`);

    if (failedTests > 0) {
        process.exit(1);
    } else {
        process.exit(0);
    }
}

runAllTests().catch(err => {
    console.error("Fatal test runner error:", err);
    process.exit(1);
});
