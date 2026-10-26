const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

const uiJsPath = path.resolve(__dirname, '../ui.js');
let uiJs = fs.readFileSync(uiJsPath, 'utf8');
uiJs = uiJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const dom = new JSDOM(`<!DOCTYPE html><html><body><div id="aiChartContainer"><canvas id="aiChartCanvas"></canvas></div><div id="errorDisplay"></div></body></html>`, { runScripts: "dangerously" });
const { window } = dom;

window.requestAnimationFrame = (cb) => setTimeout(cb, 16);
window.cancelAnimationFrame = (id) => clearTimeout(id);

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
console.log("TEST SUITE: Audit Fixes & Security Verification");
console.log("=======================================================");

// 1. Test XSS escaping in parseMd
const maliciousPayload = `<script>alert('pwned')</script>**Bold text**<img src=x onerror="steal(Telegram.WebApp.initData)">`;
const parsed = window.parseMd(maliciousPayload);

assert(!parsed.includes('<script>'), "Scripts are HTML escaped in parseMd", parsed);
assert(!parsed.includes('<img src='), "Images with onerror are HTML escaped", parsed);
assert(parsed.includes('<b>Bold text</b>'), "Markdown bold formatting still works", parsed);
assert(parsed.includes('&lt;script&gt;'), "Sanitized entity encoding is present", parsed);

// 2. Test Error Rendering for Railway 502 / HTML
window.renderError("Сервер вернул статус 502 (Bad Gateway)");
const errEl = window.document.getElementById('errorDisplay');
assert(errEl.innerHTML.includes('Сервер перезагружается'), "Railway 502 produces user-friendly reload message");

// 3. Test Circuit Breaker prioritization in error rendering
window.renderError("CIRCUIT_BREAKER_ACTIVE: Daily loss exceeded");
assert(errEl.innerHTML.includes('ТОРГИ ПРИОСТАНОВЛЕНЫ'), "Circuit Breaker message renders dedicated header");

// 4. Test Canvas Show & Hide lifecycle
window.showAiChart('EUR/USD OTC', 'M1');
const chartContainer = window.document.getElementById('aiChartContainer');
assert(chartContainer.classList.contains('active'), "Chart container becomes active on show");

window.hideAiChart();
assert(!chartContainer.classList.contains('active'), "Chart container removes active class immediately");

console.log("=======================================================");
console.log(`AUDIT TESTS: ${total} | PASSED: ${passed} | FAILED: ${total - passed}`);
console.log("=======================================================\n");

process.exit(passed === total ? 0 : 1);
