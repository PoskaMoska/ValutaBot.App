// ═══════════════════════════════════════════════════════════════════════════════
// TradeBE Smart Terminal - Centralized Shared State Module (DAG root)
// 
// Decouples main.js, api.js, and ui.js into a strict Directed Acyclic Graph:
//   state.js (no dependencies)
//     ↑
//     ├── ui.js (imports state.js)
//     ↑     ↑
//     ├── api.js (imports state.js, ui.js)
//     ↑     ↑
//   main.js (imports state.js, ui.js, api.js)
//
// Eliminates circular dependencies and JavaScript Temporal Dead Zone (TDZ)
// ReferenceError crashes across browser webviews.
// ═══════════════════════════════════════════════════════════════════════════════

export const tg = (typeof window !== 'undefined' && window.Telegram && window.Telegram.WebApp) 
    ? window.Telegram.WebApp 
    : null;

export function getCustomInitData() {
    if (typeof window === 'undefined') return '';
    try {
        const urlParams = new URLSearchParams(window.location.search);
        const userId = urlParams.get('custom_user_id') || urlParams.get('userId');
        const userSign = urlParams.get('custom_user_sign') || urlParams.get('userSign');
        const authDate = urlParams.get('auth_date');
        if (userId && userSign) {
            let res = `custom_user_id=${encodeURIComponent(userId)}&custom_user_sign=${encodeURIComponent(userSign)}`;
            if (authDate) res += `&auth_date=${encodeURIComponent(authDate)}`;
            return res;
        }
    } catch (e) {}
    return '';
}

export function getTg() {
    if (typeof window !== 'undefined' && window.Telegram && window.Telegram.WebApp) {
        return window.Telegram.WebApp;
    }
    return tg;
}

export function clearExpiredAuthParams() {
    if (typeof window === 'undefined' || !window.location) return;
    try {
        const url = new URL(window.location.href);
        let modified = false;
        ['custom_user_id', 'custom_user_sign', 'auth_date', 'userId', 'userSign'].forEach(k => {
            if (url.searchParams.has(k)) {
                url.searchParams.delete(k);
                modified = true;
            }
        });
        if (modified) {
            window.history.replaceState({}, document.title, url.toString());
        }
    } catch (_) {}
}

export function getAuthHeader() {
    const webApp = getTg();
    if (webApp && webApp.initData && webApp.initData.length > 0) {
        return webApp.initData;
    }
    return getCustomInitData();
}

// Network Status Tracking
export let isNetworkOnline = typeof navigator !== 'undefined' ? navigator.onLine : true;
export function setNetworkOnlineState(isOnline) {
    isNetworkOnline = Boolean(isOnline);
    if (typeof window !== 'undefined') window.isNetworkOnline = isNetworkOnline;
}

// Active Asset & Timeframe selection
export let currentAsset = (typeof window !== 'undefined' && window.currentAsset) ? window.currentAsset : 'EUR/USD OTC';
export let currentTf = (typeof window !== 'undefined' && window.currentTf) ? window.currentTf : 'm1';

export const TF_SECONDS_MAP = {
    s3: 3, s5: 5, s10: 10, s15: 15, s30: 30,
    m1: 60, m2: 120, m3: 180, m5: 300, m15: 900, m30: 1800,
    h1: 3600, h4: 14400, d1: 86400
};

export function getTfSeconds(tf = currentTf) {
    const tfClean = String(tf || 'm1').toLowerCase();
    return TF_SECONDS_MAP[tfClean] || 60;
}

export function getTrafficLightThresholds(tfSec) {
    if (tfSec <= 5) return { greenThresh: 0, yellowThresh: 1 };
    if (tfSec <= 10) return { greenThresh: 1, yellowThresh: 2 };
    if (tfSec <= 15) return { greenThresh: 1, yellowThresh: 3 };
    if (tfSec <= 30) return { greenThresh: 2, yellowThresh: 5 };
    return { greenThresh: 2, yellowThresh: 8 };
}

/**
 * When triggered inside the green traffic-light window at the end of an expiring bar,
 * returns milliseconds to wait until just after the next candle opens (+120ms safety buffer)
 * so the backend analyzes the newly closed bar and clears ExpiryGuard.
 */
export function getCandleBoundaryWaitMs(tf, nowMs) {
    const tfSec = getTfSeconds(tf);
    if (!Number.isFinite(nowMs) || tfSec <= 0) return 0;
    const nowSec = Math.floor(nowMs / 1000);
    const elapsed = ((nowSec % tfSec) + tfSec) % tfSec;
    const remaining = (tfSec - 1) - elapsed;
    const { greenThresh } = getTrafficLightThresholds(tfSec);
    if (remaining > greenThresh) return 0;
    const tfMs = tfSec * 1000;
    const msIntoBar = ((nowMs % tfMs) + tfMs) % tfMs;
    const msToNextBar = tfMs - msIntoBar;
    return msToNextBar > 0 ? Math.min(msToNextBar + 120, (greenThresh + 1) * 1000 + 250) : 0;
}

export function setCurrentAsset(asset) {
    if (!asset) return;
    currentAsset = asset;
    if (typeof window !== 'undefined') window.currentAsset = asset;
}

export function setCurrentTf(tf) {
    if (!tf) return;
    currentTf = tf.toLowerCase();
    if (typeof window !== 'undefined') window.currentTf = currentTf;
}

// Server Time Synchronization Offset (ms)
export let timeOffset = 0;
export function setTimeOffset(offset) {
    timeOffset = Number(offset) || 0;
    if (typeof window !== 'undefined') window.timeOffset = timeOffset;
}

// Live WebSocket Tick Price
export let lastPriceVal = 0;
export function setLastPriceVal(val) {
    lastPriceVal = Number(val) || 0;
    if (typeof window !== 'undefined') window.lastPriceVal = lastPriceVal;
}

// Active WebSocket Connection
export let priceSocket = null;
export function setPriceSocket(sock) {
    priceSocket = sock;
}

// Circuit Breaker State Tracking
export let isCircuitBreakerActive = false;
export function setCircuitBreakerState(active) {
    isCircuitBreakerActive = Boolean(active);
}

