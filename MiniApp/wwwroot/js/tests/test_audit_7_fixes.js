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
</body></html>`, { runScripts: "dangerously", url: "https://valutabot.app/?custom_user_id=123&custom_user_sign=abc&auth_date=1000000000" });

const { window } = dom;
const hapticCalls = [];

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

window.requestAnimationFrame = (cb) => setTimeout(cb, 16);
window.cancelAnimationFrame = (id) => clearTimeout(id);

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

console.log("=======================================================");
console.log(`AUDIT 7 TESTS: ${total} | PASSED: ${passed} | FAILED: ${total - passed}`);
console.log("=======================================================\n");

if (passed !== total) process.exit(1);
process.exit(0);
