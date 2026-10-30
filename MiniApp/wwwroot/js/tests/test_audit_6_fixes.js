const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

const uiJsPath = path.resolve(__dirname, '../ui.js');
let uiJs = fs.readFileSync(uiJsPath, 'utf8');
uiJs = uiJs.replace(/import\s+[\s\S]*?from\s+['"][^'"]+['"];?/g, '/* mocked import */');

const dom = new JSDOM(`<!DOCTYPE html><html><body>
<div id="aiCopilotCard" style="display:none;">
    <div id="copilotFocusDot"></div>
    <div id="copilotFocusText"></div>
    <div id="copilotHeadline"></div>
    <div id="copilotMessage"></div>
    <div id="copilotActionWrap" style="display:none;">
        <button id="copilotActionBtn">
            <span id="copilotActionLabel"></span>
        </button>
    </div>
</div>
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
console.log("TEST SUITE: Audit 6 Frontend Fixes Verification");
console.log("=======================================================");

// 1. Test getSessionPsychometrics idempotency (D13)
const initial = window.getSessionPsychometrics();
const second = window.getSessionPsychometrics();
assert(initial.speedPenalty === second.speedPenalty, "getSessionPsychometrics is idempotent across multiple reads");

// 2. Test recordAnalysisPsychometrics records analysis accurately
const rec1 = window.recordAnalysisPsychometrics();
assert(typeof rec1.speedPenalty === 'number', "recordAnalysisPsychometrics computes speed penalty");

// 3. Test copilotAdvice as stringified JSON (F13)
const dataJsonString = {
    copilotAdvice: JSON.stringify({
        status: "GOLDEN",
        title: "Снайперский откат",
        message: "Идеальная точка входа",
        recommendedAsset: "EUR/USD OTC",
        recommendedTf: "s5"
    })
};
window.updateAiCopilot(dataJsonString, null);
const card = window.document.getElementById('aiCopilotCard');
const headline = window.document.getElementById('copilotHeadline');
const actionLabel = window.document.getElementById('copilotActionLabel');

assert(card.style.display === 'block', "Copilot card visible with stringified JSON advice");
assert(headline.innerText === "Снайперский откат", "Copilot headline parsed from stringified advice");
assert(actionLabel.innerText.includes('EUR/USD OTC (S5)'), "Copilot action uppercase TF formatted safely");

// 4. Test copilotAdvice as plain string
const dataPlainString = {
    copilotAdvice: "Внимание, повышенная волатильность"
};
window.updateAiCopilot(dataPlainString, null);
const msg = window.document.getElementById('copilotMessage');
assert(msg.innerText === "Внимание, повышенная волатильность", "Copilot handles plain string advice safely");

console.log("=======================================================");
console.log(`AUDIT 6 TESTS: ${total} | PASSED: ${passed} | FAILED: ${total - passed}`);
console.log("=======================================================\n");

if (passed !== total) process.exit(1);
process.exit(0);
