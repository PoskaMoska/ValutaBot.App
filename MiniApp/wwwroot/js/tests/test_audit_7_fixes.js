const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

const uiJsPath = path.resolve(__dirname, '../ui.js');
let uiJs = fs.readFileSync(uiJsPath, 'utf8');
uiJs = uiJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const stateJsPath = path.resolve(__dirname, '../state.js');
let stateJs = fs.readFileSync(stateJsPath, 'utf8');
stateJs = stateJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const dom = new JSDOM(`<!DOCTYPE html><html><body>
<div id="aiChartContainer"><canvas id="aiChartCanvas"></canvas></div>
<div id="errorDisplay"></div>
<div id="trafficLight"></div>
<div id="assetGrid"></div>
<input type="text" id="assetSearchInput" />
<div id="livePriceValue" class="live-price-value">--</div>
</body></html>`, { runScripts: "dangerously", url: "https://valutabot.app/?custom_user_id=123&custom_user_sign=abc&auth_date=1000000000" });

const { window } = dom;
const hapticCalls = [];
let rafCalls = 0;
let lastRafCb = null;

window.Telegram = {
    WebApp: {
        initData: "fresh_mock_init_data",
        HapticFeedback: {
            impactOccurred: (style) => hapticCalls.push({ type: 'impact', style }),
            notificationOccurred: (type) => hapticCalls.push({ type: 'notification', notificationType: type }),
            selectionChanged: () => hapticCalls.push({ type: 'selection' })
        }
    }
};

window.requestAnimationFrame = (cb) => {
    rafCalls++;
    lastRafCb = cb;
    return 100;
};
window.cancelAnimationFrame = (id) => {
    lastRafCb = null;
};

let transformedState = stateJs
    .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
    .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
    .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =');
window.eval(transformedState);

let transformedUi = uiJs
    .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
    .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
    .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =');
window.eval(transformedUi);

let passed = 0;
let total = 0;

function assert(cond, name, details = '') {
    total++;
    if (cond) {
        console.log(`  [PASS] ${name}`);
        passed++;
    } else {
        console.error(`  [FAIL] ${name} - ${details}`);
    }
}

console.log("\n=======================================================");
console.log("TEST SUITE: Audit 7 Deep Audit Fixes Verification");
console.log("=======================================================");

// 1. Context Loss & Background Hibernation
window.initCanvasContextLossHandlers();
const canvas = window.document.getElementById('aiChartCanvas');
let defaultPrevented = false;
const fakeContextLostEvent = {
    type: 'contextlost',
    preventDefault: () => { defaultPrevented = true; }
};

// Dispatch contextlost
canvas.dispatchEvent = function(event) {
    if (this._listeners && this._listeners[event.type]) {
        this._listeners[event.type].forEach(fn => fn(event));
    }
};
// Add manual dispatch simulation since jsdom Canvas doesn't implement webgl context loss directly
assert(typeof window.initCanvasContextLossHandlers === 'function', "initCanvasContextLossHandlers is exported and initialized");

// 2. Haptic Feedback Feature Detection & Invocation
hapticCalls.length = 0;
window.hapticImpact('heavy');
assert(hapticCalls.length === 1 && hapticCalls[0].style === 'heavy', "hapticImpact forwards correctly to Telegram WebApp");

window.hapticNotification('success');
assert(hapticCalls.length === 2 && hapticCalls[1].notificationType === 'success', "hapticNotification forwards success correctly");

window.hapticSelection();
assert(hapticCalls.length === 3 && hapticCalls[2].type === 'selection', "hapticSelection forwards selectionChanged correctly");

// 3. Traffic Light Action triggers light haptic impact
hapticCalls.length = 0;
window.updateTrafficLight('action');
assert(hapticCalls.some(c => c.type === 'impact' && c.style === 'light'), "updateTrafficLight('action') triggers tactical tactile haptic alert");

// 4. Seamless Session Expiration & URL Stale Param Eviction
assert(window.location.search.includes('custom_user_id'), "Initial URL has query parameters");
window.clearExpiredAuthParams();
assert(!window.location.search.includes('custom_user_id') && !window.location.search.includes('auth_date'), "clearExpiredAuthParams purges stale auth params from URL");

// 5. Auth Error Rendering includes seamless refresh button
window.renderError("Unauthorized (HTTP 401)");
const errBox = window.document.getElementById('errorDisplay');
assert(errBox.innerHTML.includes('btnRefreshSession'), "renderError renders seamless session refresh button on 401");
assert(errBox.innerHTML.includes('Сессия авторизации устарела'), "renderError displays seamless auth renewal text instead of telling user to exit to bot");

// 6. Dynamic Auth Header reflects active Telegram WebApp
const authHdr = window.getAuthHeader();
assert(authHdr === "fresh_mock_init_data", "getAuthHeader reads dynamic Telegram WebApp initData without stale closure");

// 7. Tick Storm Throttling & rAF Coalescing
rafCalls = 0;
lastRafCb = null;
const priceEl = window.document.getElementById('livePriceValue');

// Simulate burst of 5 rapid ticks from WebSocket
window.updateLivePriceUI(1.08500);
window.updateLivePriceUI(1.08510);
window.updateLivePriceUI(1.08520);
window.updateLivePriceUI(1.08505);
window.updateLivePriceUI(1.08550);

assert(rafCalls === 1, "updateLivePriceUI coalesces rapid tick burst into exactly 1 rAF schedule");

// Execute the scheduled rAF
if (typeof lastRafCb === 'function') lastRafCb();
assert(priceEl.textContent === '1.08550', "updateLivePriceUI renders the latest coalesced tick in textContent");

// Verify next tick compares against last rendered price
rafCalls = 0;
window.updateLivePriceUI(1.08540); // price down
if (typeof lastRafCb === 'function') lastRafCb();
assert(priceEl.textContent === '1.08540' && priceEl.className.includes('down'), "updateLivePriceUI marks downwards tick direction");

rafCalls = 0;
window.updateLivePriceUI(1.08560); // price up
if (typeof lastRafCb === 'function') lastRafCb();
assert(priceEl.textContent === '1.08560' && priceEl.className.includes('up'), "updateLivePriceUI marks upwards tick direction");

// Verify resetLivePriceFlash cancels pending rAF
rafCalls = 0;
window.updateLivePriceUI(1.08600);
window.resetLivePriceFlash();
assert(lastRafCb === null && priceEl.className === 'live-price-value', "resetLivePriceFlash cancels pending rAF and restores clean class");

// 8. Candle timing thresholds & green-zone boundary wait helpers (state.js)
assert(window.getTfSeconds('S5') === 5 && window.getTfSeconds('M1') === 60, "getTfSeconds parses S5=5 and M1=60");
const s5Thresh = window.getTrafficLightThresholds(5);
const m1Thresh = window.getTrafficLightThresholds(60);
assert(s5Thresh.greenThresh === 0 && s5Thresh.yellowThresh === 1, "S5 traffic light thresholds preserve green=0, yellow=1");
assert(m1Thresh.greenThresh === 2 && m1Thresh.yellowThresh === 8, "M1 traffic light thresholds preserve green=2, yellow=8");

// At 4.200s into S5 candle (remaining=0 -> green zone), should wait 800 + 120 = 920ms until next bar opens
assert(window.getCandleBoundaryWaitMs('s5', 4200) === 920, "getCandleBoundaryWaitMs('s5', 4200) waits 920ms for next 5s bar");
// At 3.500s into S5 candle (remaining=1 -> yellow zone), should not delay
assert(window.getCandleBoundaryWaitMs('s5', 3500) === 0, "getCandleBoundaryWaitMs('s5', 3500) returns 0 outside green zone");
// At 0.200s into fresh S5 candle (remaining=4 -> red zone), sends immediately for ExpiryGuard
assert(window.getCandleBoundaryWaitMs('s5', 200) === 0, "getCandleBoundaryWaitMs('s5', 200) returns 0 at start of candle");
// At 58.000s into M1 candle (remaining=1 -> green zone), waits 2000 + 120 = 2120ms
assert(window.getCandleBoundaryWaitMs('m1', 58000) === 2120, "getCandleBoundaryWaitMs('m1', 58000) waits 2120ms for M1 close");
// Mid-candle M1 (30.000s) returns 0
assert(window.getCandleBoundaryWaitMs('m1', 30000) === 0, "getCandleBoundaryWaitMs('m1', 30000) returns 0 mid-candle");

// 9. Async boundary defer + cancelAnalysis behavior in api.js when triggered in green zone
(async () => {
    const apiJsPath = path.resolve(__dirname, '../api.js');
    let apiJs = fs.readFileSync(apiJsPath, 'utf8')
        .replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */')
        .replace(/export\s+(async\s+)?function\s+([a-zA-Z0-9_]+)\s*\(/g, 'window.$2 = $1function(')
        .replace(/export\s+const\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =')
        .replace(/export\s+let\s+([a-zA-Z0-9_]+)\s*=/g, 'window.$1 =');

    window.currentAsset = 'EUR/USD OTC';
    window.currentTf = 's5';
    window.tg = window.Telegram.WebApp;
    window.getCustomInitData = () => '';
    window.isNetworkOnline = true;
    window.setNetworkOnlineState = (v) => { window.isNetworkOnline = Boolean(v); };
    window.setCircuitBreakerState = () => {};
    window.setPriceSocket = () => {};
    window.priceSocket = null;
    window.setLastPriceVal = () => {};
    window.showAiChart = () => {};
    window.hideAiChart = () => {};
    window.updateAiChartData = () => {};
    window.clearResults = () => {};
    window.startStatusBar = () => {};
    window.stopStatusBar = () => {};
    window.flashResults = () => {};
    window.renderDirSvg = () => {};
    window.renderMiniChart = () => {};
    window.renderSparklinePrediction = () => {};
    window.renderExpiryCandles = () => {};
    window.switchResultTab = () => {};
    window.parseMd = (s) => s;
    window.pricesToBars = () => [];
    window.normalizeDirection = (d) => d || 'NEUTRAL';

    const btn = window.document.createElement('button');
    btn.id = 'btnGet';
    window.document.body.appendChild(btn);

    window.eval(apiJs);

    let analyzeCalls = 0;
    window.fetch = async (url) => {
        if (String(url).includes('/api/analyze')) analyzeCalls++;
        return {
            ok: true,
            headers: { get: () => 'application/json' },
            text: async () => JSON.stringify({ result: { direction: 'BUY', probability: 62, duration: '5 сек', chartData: [1, 1.01] }, config: {} })
        };
    };

    // Shift offset so Date.now() + timeOffset sits 4.65s into a 5s candle (~470ms total wait with +120ms buffer)
    const nowReal = Date.now();
    const targetVirtual = Math.floor(nowReal / 5000) * 5000 + 4650;
    window.setTimeOffset(targetVirtual - nowReal);
    window.executeAnalysis();
    await new Promise(r => setTimeout(r, 80));
    assert(analyzeCalls === 0, "executeAnalysis in S5 green zone waits for candle close before firing /api/analyze");
    await new Promise(r => setTimeout(r, 520));
    assert(analyzeCalls === 1, "executeAnalysis fires /api/analyze immediately after candle opens");

    // Verify cancelAnalysis aborts green-zone wait without sending /api/analyze
    const beforeCancel = analyzeCalls;
    const nowReal2 = Date.now();
    const targetVirtual2 = Math.floor(nowReal2 / 5000) * 5000 + 4550;
    window.setTimeOffset(targetVirtual2 - nowReal2);
    window.executeAnalysis();
    await new Promise(r => setTimeout(r, 50));
    window.cancelAnalysis();
    await new Promise(r => setTimeout(r, 650));
    assert(analyzeCalls === beforeCancel, "cancelAnalysis cancels pending green-zone boundary wait cleanly");

    console.log("=======================================================");
    console.log(`AUDIT 7 TESTS: ${total} | PASSED: ${passed} | FAILED: ${total - passed}`);
    console.log("=======================================================\n");

    if (passed !== total) process.exit(1);
    process.exit(0);
})();
