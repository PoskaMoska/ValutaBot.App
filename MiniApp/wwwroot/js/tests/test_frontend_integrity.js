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
    window.applyCopilotRecommendation = () => {};

    const globalShim = `
var currentTf = window.currentTf || "s10";
var currentAsset = window.currentAsset || "EURUSD";
var tg = window.tg;
var getCustomInitData = window.getCustomInitData || function() { return ""; };
var applyCopilotRecommendation = window.applyCopilotRecommendation || function() {};
`;

    // Eval UI and API code inside window context
    try {
        let transformedUi = globalShim + '\n' + uiJs
            .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
            .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
            .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =');
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
            .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =');
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
    if (id === 'resDir') return String(el.innerHTML).trim();
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
            return { ok: true, json: async () => [] };
        }
        return {
            ok: true,
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
            llmReport: "Нейромозг PatchTST v3 подтверждает устойчивый восходящий тренд.",
            copilotAdvice: {
                status: "GOLDEN",
                title: "🎯 Системный тренд (BUY)",
                message: "Структура рынка устойчивая. Математическое ожидание подтверждено нейросетью (65%).",
                recommendedAsset: "EURUSD",
                recommendedTf: "S10",
                qualityScore: 90
            }
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

        // 5. Copilot Card
        assertEq("Copilot headline matches", getVal(document, 'copilotHeadline'), '🎯 Системный тренд (BUY)');
        assertEq("Copilot message matches", getVal(document, 'copilotMessage'), 'Структура рынка устойчивая. Математическое ожидание подтверждено нейросетью (65%).');
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
            llmReport: null,
            copilotAdvice: {
                status: "CAUTION",
                title: "⚡ Вход в консолидации (PUT)",
                message: "Рынок в стадии накопления. Ожидайте смену свечи для входа PUT (52%).",
                recommendedAsset: "USDJPY",
                recommendedTf: "M1",
                qualityScore: 65
            }
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

        // Copilot caution state
        assertEq("Copilot Title is Caution", getVal(document, 'copilotHeadline'), '⚡ Вход в консолидации (PUT)');
        assert(getVal(document, 'copilotMessage').includes('Рынок в стадии накопления'), "Copilot warning text is rendered");
    });

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
