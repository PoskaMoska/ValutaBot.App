const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

// 1. Read the frontend scripts
let apiJsCode = fs.readFileSync(path.join(__dirname, '../api.js'), 'utf8');
// Strip out all ES module imports (even multi-line ones)
apiJsCode = apiJsCode.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const uiJsCode = fs.readFileSync(path.join(__dirname, '../ui.js'), 'utf8');

// We will test by overriding document methods slightly or just running JSDOM
const html = `
<!DOCTYPE html>
<html>
<body>
    <div id="resDir"></div>
    <div id="resProb"></div>
    <div id="resProbFact"></div>
    <div id="resDur"></div>
    <div id="resRsi"></div>
    <div id="resEma"></div>
    <div id="resVol"></div>
    <div id="weatherPhase"></div>
    <div id="weatherEntropy"></div>
    <div id="weatherTitle"></div>
    <div id="mlModelBadge"></div>
    <div id="mlEnsembleDir"></div>
    <div id="mlEnsembleConf"></div>
    <div id="mlEnsembleReport"></div>
    <div id="winRateAsset"></div>
    <div id="winRateOverall"></div>
    <div id="signalCount"></div>
    <div id="confluenceCard"></div>
    <div id="confluenceLabel"></div>
    <div id="goldenSetupBadge"></div>
    <div id="goldenSetupBadgeMain"></div>
    <div id="radarTa"></div>
    <div id="radarSmc"></div>
    <div id="radarOf"></div>
    <div id="radarMl"></div>
    <div id="consensusRadarCard">
        <!-- The elements above are actually inside here in real UI, but for tests just having it exist is enough -->
    </div>
    <button id="btnGet"></button>
    <div id="mainSphere"></div>
    <div id="errorDisplay"></div>
</body>
</html>
`;

const dom = new JSDOM(html, { runScripts: "dangerously", url: "http://localhost" });
const window = dom.window;
const document = window.document;

// Mock fetch
const mockBackendResponse = {
    result: {
        direction: "BUY",
        probability: 82, 
        winRateAsset: 64, 
        winRateOverall: 58, 
        signalsVerifiedAsset: 12,
        ema: 1.08234, 
        uiMarketPhase: "Замедление (Разворот)",
        uiMarketEntropy: "ВЫСОКАЯ (Хаос / Опасно!)",
        lgbmDirection: "BUY",
        lgbmConfidence: 85,
        confluenceLabel: "Strong Buy",
        goldenSetup: true,
        chartData: [1.081, 1.082, 1.08234],
        expiryCandles: 5,
        duration: "5 мин",
        atr: 0.001,
        llmReport: "Отличный сигнал",
        taDirection: "BUY",
        taConfidence: 80,
        smcDirection: "PUT",
        smcConfidence: 65,
        ofDirection: "NEUTRAL",
        ofConfidence: 0
    },
    config: { ml: true, smc: true, of: true }
};

window.fetch = async (url) => {
    return {
        ok: true,
        json: async () => mockBackendResponse
    };
};

// Mock dependencies of api.js
window.tg = null;
window.getCustomInitData = () => "";
window.currentAsset = "EUR/USD OTC";
window.currentTf = "m1";
window.switchResultTab = () => {};
window.updateTrafficLight = () => {};
window.pricesToBars = () => [];
window.renderSparklinePrediction = () => {};
window.renderDirSvg = () => {};
window.renderExpiryCandles = () => {};
window.renderMiniChart = () => {};
window.flashResults = () => {};
window.stopStatusBar = () => {};
window.hideAiChart = () => {};
window.parseMd = (s) => s;
// Added missing mocks
window.renderError = (msg, catchMsg) => { console.error("renderError called:", msg, catchMsg); };
window.clearResults = () => {};
window.startStatusBar = () => {};
window.showAiChart = () => {};
window.updateAiChartData = () => {};
window.updateLivePriceUI = () => {};
window.requestAnimationFrame = (cb) => {
    return setTimeout(cb, 16);
};
window.cancelAnimationFrame = (id) => {
    clearTimeout(id);
};

// Load the api.js code but export executeAnalysis
let executeAnalysis;
try {
    const transformedCode = apiJsCode
        .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
        .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
        .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =');
    
    window.eval(transformedCode);
    executeAnalysis = window.executeAnalysis;
} catch (e) {
    console.error("Setup error:", e);
    process.exit(1);
}

async function runTest() {
    let passed = 0;
    let total = 16; // 14 for success, 2 for error

    function assertEq(name, actual, expected) {
        if (actual === expected) {
            console.log(`[PASS] ${name}`);
            passed++;
        } else {
            console.log(`[FAIL] ${name} - Expected: '${expected}', Got: '${actual}'`);
        }
    }

    function getVal(id) {
        const el = document.getElementById(id);
        return el ? (el.innerText || el.textContent || el.innerHTML || '') : 'null';
    }

    // --- SCENARIO 1: Success ---
    console.log("Running SCENARIO 1: Success Signal...");
    executeAnalysis();
    
    // Wait for the Math.max(0, 2000 - elapsed) delay + extra buffer
    await new Promise(r => setTimeout(r, 3500));
    
    assertEq("Probability formatting", getVal('resProb'), '82%');
    assertEq("WinRateAsset formatting", getVal('winRateAsset'), '64%');
    assertEq("WinRateOverall formatting", getVal('winRateOverall'), '58%');
    assertEq("LGBM Confidence formatting", getVal('mlEnsembleConf'), '85%');
    assertEq("EMA precision (5 decimals)", String(getVal('resEma')).trim(), '1.08234');
    
    const phaseColor = document.getElementById('weatherPhase').style.color;
    assertEq("Phase Regex (Yellow)", phaseColor === 'rgb(245, 158, 11)' || phaseColor === '#f59e0b', true);
    
    const entColor = document.getElementById('weatherEntropy').style.color;
    assertEq("Entropy Regex (Red)", entColor === 'rgb(239, 68, 68)' || entColor === '#ef4444', true);
    
    assertEq("Main Direction is BUY", getVal('resDir').includes('ВВЕРХ'), true);
    assertEq("Duration formatting", getVal('resDur'), '5 мин');
    assertEq("ML Radar is BUY", getVal('radarMl').includes('ВВЕРХ (85%)'), true);
    assertEq("TA Radar is BUY", getVal('radarTa').includes('ВВЕРХ (80%)'), true);
    assertEq("SMC Radar is PUT", getVal('radarSmc').includes('ВНИЗ (65%)'), true);
    assertEq("OrderFlow Radar is NEUTRAL", getVal('radarOf').includes('НЕЙТРАЛЬНО'), true);
    assertEq("Confluence Card display", document.getElementById('confluenceCard').style.display, 'block');


    // --- SCENARIO 2: Error 500 ---
    console.log("\nRunning SCENARIO 2: Backend 500 Error...");
    // Mock fetch to simulate network drop / 500 error
    window.fetch = async (url) => {
        throw new Error("500 Internal Server Error");
    };
    
    // In ui.js, renderError displays the errorDisplay element. We'll mock that behavior.
    window.renderError = (msg, catchMsg) => { 
        const ed = document.getElementById('errorDisplay');
        if(ed) ed.style.display = 'block';
    };

    executeAnalysis();
    
    // Error is caught instantly, so short wait is enough
    await new Promise(r => setTimeout(r, 500));
    
    assertEq("ErrorDisplay becomes visible", document.getElementById('errorDisplay').style.display, 'block');
    assertEq("Main Sphere removes analyzing class", document.getElementById('mainSphere').classList.contains('analyzing'), false);


    console.log(`\nTests completed: ${passed}/${total} passed.`);
    if (passed !== total) process.exit(1);
}

// Intercept window errors
window.addEventListener('error', (evt) => {
    console.error("Window Error:", evt.error);
});

runTest();
