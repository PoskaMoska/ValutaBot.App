const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

const uiJsPath = path.resolve(__dirname, '../ui.js');
let uiJs = fs.readFileSync(uiJsPath, 'utf8');
uiJs = uiJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const dom = new JSDOM(`<!DOCTYPE html><html><body>
<div id="durChart"></div>
<div id="aiChartContainer"><canvas id="aiChartCanvas"></canvas></div>
<div id="errorDisplay"></div>
</body></html>`, { runScripts: "dangerously" });
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
console.log("TEST SUITE: Audit 6 Frontend Fixes & Copilot Removal Verification");
console.log("=======================================================");

// 1. Removed AI Copilot & session psychometrics symbols are absent
assert(typeof window.updateAiCopilot === 'undefined', "updateAiCopilot is removed from ui.js");
assert(typeof window.recordAnalysisPsychometrics === 'undefined' && typeof window.getSessionPsychometrics === 'undefined', "Session psychometrics helpers are removed from ui.js");

// 2. HTML template no longer contains AI Copilot card
const rawHtml = fs.readFileSync(path.resolve(__dirname, '../../index.html'), 'utf8');
assert(!rawHtml.includes('aiCopilotCard') && !rawHtml.includes('copilotActionBtn'), "index.html does not contain AI Copilot markup");

// 3. UI helpers work safely without Copilot DOM elements
window.renderExpiryCandles('durChart', [{ o: 1.1, h: 1.102, l: 1.099, c: 1.101 }], 1, 'BUY');
const bodyEl = window.document.querySelector('#durChart .exp-body');
assert(bodyEl !== null && bodyEl.classList.contains('exp-bull'), "renderExpiryCandles renders cleanly without Copilot DOM");

console.log("=======================================================");
console.log(`AUDIT 6 TESTS: ${total} | PASSED: ${passed} | FAILED: ${total - passed}`);
console.log("=======================================================\n");

if (passed !== total) process.exit(1);
process.exit(0);
