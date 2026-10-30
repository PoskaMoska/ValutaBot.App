import { initPriceWebSocket, closePriceWebSocket, resetWebSocketBackoff, syncTime, executeAnalysis, cancelAnalysis, resetSignalKey } from './api.js?v=20261029_audit_v6';
import { tg, getCustomInitData, currentAsset, currentTf, setCurrentAsset, setCurrentTf, timeOffset, onCopilotAction, setNetworkOnlineState } from './state.js?v=20261029_audit_v6';
export { tg, getCustomInitData, currentAsset, currentTf };
import { switchResultTab, updateTrafficLight, showNetworkBanner, hideNetworkBanner } from './ui.js?v=20261029_audit_v6';

// 1. Telegram WebApp Lifecycle & Viewport Stabilization
if (tg) {
    try {
        tg.ready();
        tg.expand();
        if (typeof tg.enableClosingConfirmation === 'function') {
            tg.enableClosingConfirmation(); // Prevent accidental swipe dismiss
        }
        if (window.Sentry && tg.initDataUnsafe?.user) {
            const u = tg.initDataUnsafe.user;
            Sentry.setUser({
                id: String(u.id || ''),
                username: u.username || '',
                first_name: u.first_name || ''
            });
            Sentry.setTag('tg_platform', tg.platform || 'unknown');
        }
    } catch(e) {}
}

onCopilotAction(applyCopilotRecommendation);

const assetsList = ['EUR/USD OTC', 'GBP/USD OTC', 'USD/JPY OTC', 'AUD/USD OTC', 'USD/CHF OTC', 'USD/CAD OTC'];

function getTopAssets() {
    try {
        const h = JSON.parse(localStorage.getItem('vhistory') || '[]');
        const freq = {};
        for (let i = 0; i < h.length; i++) { 
            const a = h[i]?.asset;
            if (a) freq[a] = (freq[a] || 0) + 1; 
        }
        return Object.keys(freq).sort((a,b) => freq[b] - freq[a]).slice(0, 8);
    } catch(e) { return []; }
}

function escapeAttr(str) {
    return String(str || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function renderAssets(arr) {
    const top = getTopAssets();
    const majors = ['EUR/USD OTC', 'GBP/USD OTC', 'USD/JPY OTC', 'AUD/USD OTC', 'USD/CHF OTC', 'USD/CAD OTC'];
    return arr.map(a => {
        const safeA = escapeAttr(a);
        const star = top.indexOf(a) !== -1 ? '<span class="top-star">★</span>' : '';
        const cls = majors.indexOf(a) !== -1 ? 'asset-item major' : 'asset-item';
        return `<div class="${cls}" data-asset="${safeA}">${safeA}${star}</div>`;
    }).join('');
}

function initAssetGrid() {
    const gridEl = document.getElementById('assetGrid');
    if (gridEl) {
        gridEl.innerHTML = `<div class='otc-scroll' style='grid-column:1/-1'><div class='asset-grid'>${renderAssets(assetsList)}</div></div>`;
    }
}

function toggleMenu(m) {
    document.querySelectorAll('.asset-menu, .tf-menu').forEach(menu => { 
        if (menu.id !== m) menu.classList.remove('show'); 
    });
    const menuEl = document.getElementById(m);
    if (menuEl) menuEl.classList.toggle('show');
}

let switchThrottleTimer = null;
function throttledWsSwitch() {
    cancelAnalysis();
    updateCountdown();
    if (switchThrottleTimer) clearTimeout(switchThrottleTimer);
    switchThrottleTimer = setTimeout(() => {
        resetWebSocketBackoff();
        initPriceWebSocket();
    }, 200);
}

function setAsset(el) {
    if (!el) return;
    el = el.closest('.asset-item') || el;
    const a = el.getAttribute('data-asset');
    if (!a) return;
    setCurrentAsset(a);
    const selEl = document.getElementById('selectedAsset');
    if (selEl) selEl.innerText = a;
    document.querySelectorAll('.asset-item').forEach(i => i.classList.remove('active'));
    el.classList.add('active');
    const menuEl = document.getElementById('assetMenu');
    if (menuEl) menuEl.classList.remove('show');
    const sphere = document.getElementById('mainSphere');
    if (sphere) sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
    
    throttledWsSwitch();
    resetSignalKey();
}

function setTf(el) {
    if (!el) return;
    el = el.closest('.tf-btn') || el;
    const tf = el.getAttribute('data-tf');
    if (!tf) return;
    setCurrentTf(tf);
    const selEl = document.getElementById('selectedTf');
    if (selEl) selEl.innerText = tf.toUpperCase();
    document.querySelectorAll('.tf-btn').forEach(i => i.classList.remove('active'));
    el.classList.add('active');
    const menuEl = document.getElementById('tfMenu');
    if (menuEl) menuEl.classList.remove('show');
    const sphere = document.getElementById('mainSphere');
    if (sphere) sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
    
    throttledWsSwitch();
    resetSignalKey();
}

let copilotExecTimer = null;
export function applyCopilotRecommendation(rec) {
    if (!rec) return;
    if (rec.asset) {
        const safeAsset = CSS.escape(rec.asset);
        const item = document.querySelector(`.asset-item[data-asset="${safeAsset}"]`);
        if (item) {
            setAsset(item);
        } else {
            setCurrentAsset(rec.asset);
            const selEl = document.getElementById('selectedAsset');
            if (selEl) selEl.textContent = rec.asset;
            throttledWsSwitch();
            resetSignalKey();
        }
    }
    if (rec.tf) {
        const tfUpper = rec.tf.toUpperCase();
        const safeTf = CSS.escape(tfUpper);
        const tfItem = document.querySelector(`.tf-btn[data-tf="${safeTf}"]`);
        if (tfItem) {
            setTf(tfItem);
        } else {
            setCurrentTf(rec.tf);
            const selEl = document.getElementById('selectedTf');
            if (selEl) selEl.textContent = tfUpper;
            throttledWsSwitch();
            resetSignalKey();
        }
    }
    if (copilotExecTimer) clearTimeout(copilotExecTimer);
    copilotExecTimer = setTimeout(() => {
        copilotExecTimer = null;
        executeAnalysis();
    }, 250);
}

function handleGlobalInteraction(e) {
    const target = e.target;
    if (!target) return;

    const btnGet = target.closest('#btnGet');
    if (btnGet) {
        executeAnalysis();
        return;
    }

    const assetTrigger = target.closest('#assetBtn');
    if (assetTrigger) {
        toggleMenu('assetMenu');
        return;
    }

    const tfTrigger = target.closest('#tfBtn');
    if (tfTrigger) {
        toggleMenu('tfMenu');
        return;
    }

    const assetItem = target.closest('.asset-item');
    if (assetItem) {
        setAsset(assetItem);
        return;
    }

    const tfItem = target.closest('.tf-btn');
    if (tfItem) {
        setTf(tfItem);
        return;
    }

    const tabBtnChart = target.closest('#tabBtnChart');
    if (tabBtnChart) {
        switchResultTab('chart');
        return;
    }

    const tabBtnAI = target.closest('#tabBtnAI');
    if (tabBtnAI) {
        switchResultTab('ai');
        return;
    }

    if (!target.closest('.selector-section') && !target.closest('.error-debug-toggle')) {
        document.querySelectorAll('.asset-menu, .tf-menu').forEach(m => m.classList.remove('show'));
    }
}

document.addEventListener('click', handleGlobalInteraction);

// URL Params with CSS Injection Guard
(function() {
    try {
        const p = new URLSearchParams(window.location.search);
        const a = p.get('asset');
        const t = p.get('tf');
        if (a) {
            const el = document.querySelector(`.asset-item[data-asset="${CSS.escape(a.toUpperCase())}"]`);
            if (el) { setAsset(el); el.scrollIntoView && el.scrollIntoView({ block: 'nearest' }); }
        }
        if (t) {
            const el = document.querySelector(`.tf-btn[data-tf="${CSS.escape(t.toUpperCase())}"]`);
            if (el) setTf(el);
        }
    } catch(e) {}
})();

function getTfSeconds() {
    const map = { s3:3, s5:5, s10:10, s15:15, s30:30, m1:60, m2:120, m3:180, m5:300, m15:900, m30:1800, h1:3600, h4:14400, d1:86400 };
    return map[currentTf] || 60;
}

let cachedCandleTimeEl = null;
let lastCandleClass = '';
let lastCandleText = '';

function updateCountdown() {
    if (document.hidden) return;
    if (!cachedCandleTimeEl) {
        cachedCandleTimeEl = document.getElementById('candleTime');
        if (!cachedCandleTimeEl) return;
    }
    const tfSec = getTfSeconds();
    const now = Math.floor((Date.now() + timeOffset) / 1000);
    const remaining = tfSec - (now % tfSec);
    const mins = Math.floor(remaining / 60);
    const secs = remaining % 60;
    const nextText = `${mins}:${secs.toString().padStart(2, '0')}`;
    if (nextText !== lastCandleText) {
        cachedCandleTimeEl.textContent = nextText;
        lastCandleText = nextText;
    }
    
    let greenThresh = 3;
    let yellowThresh = 10;
    
    if (tfSec <= 5) {
        greenThresh = 1; yellowThresh = 2;
    } else if (tfSec <= 10) {
        greenThresh = 1; yellowThresh = 3;
    } else if (tfSec <= 15) {
        greenThresh = 1; yellowThresh = 4;
    } else if (tfSec <= 30) {
        greenThresh = 2; yellowThresh = 6;
    }
    let nextClass = 'time';
    if (remaining <= greenThresh) {
        nextClass = 'time critical';
        updateTrafficLight('action');
    } else if (remaining <= yellowThresh) {
        nextClass = 'time warning';
        updateTrafficLight('prepare');
    } else {
        updateTrafficLight('wait');
    }
    if (nextClass !== lastCandleClass) {
        cachedCandleTimeEl.className = nextClass;
        lastCandleClass = nextClass;
    }
}

function resumeLive() {
    if (document.hidden) return;
    hideNetworkBanner();
    resetWebSocketBackoff();
    syncTime().finally(updateCountdown);
    updateCountdown(); // Immediately update display without waiting for 1s interval
    initPriceWebSocket();
}

initAssetGrid();
syncTime().finally(updateCountdown);
initPriceWebSocket();
(function tickCountdown() {
    updateCountdown();
    const drift = (Date.now() + timeOffset) % 1000;
    setTimeout(tickCountdown, 1000 - (drift >= 0 ? drift : drift + 1000) + 15);
})();

// Page Visibility API - Battery and Railway Resource Protection
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        closePriceWebSocket();
    } else {
        updateCountdown();
        if (navigator.onLine) {
            resumeLive();
        } else {
            showNetworkBanner('Нет интернет-соединения');
        }
    }
});

window.addEventListener('pageshow', (e) => { if (e.persisted) resumeLive(); });
if (navigator.connection && typeof navigator.connection.addEventListener === 'function') {
    navigator.connection.addEventListener('change', () => { if (navigator.onLine) resumeLive(); });
}

// Network Reconnect Recovery
window.addEventListener('online', () => {
    setNetworkOnlineState(true);
    resumeLive();
});

window.addEventListener('offline', () => {
    setNetworkOnlineState(false);
    closePriceWebSocket(); // Close socket first...
    showNetworkBanner('Нет интернет-соединения'); // ...then show network banner
    const valEl = document.getElementById('livePriceValue');
    if (valEl) {
        valEl.innerText = 'ОФФЛАЙН';
        valEl.className = 'live-price-value';
    }
});
