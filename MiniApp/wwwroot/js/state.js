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

export function getAuthHeader() {
    if (tg && tg.initData && tg.initData.length > 0) {
        return tg.initData;
    }
    return getCustomInitData();
}

// Network Status Tracking
export let isNetworkOnline = typeof navigator !== 'undefined' ? navigator.onLine : true;
export function setNetworkOnlineState(isOnline) {
    isNetworkOnline = Boolean(isOnline);
}

// Active Asset & Timeframe selection
export let currentAsset = (typeof window !== 'undefined' && window.currentAsset) ? window.currentAsset : 'EUR/USD OTC';
export let currentTf = (typeof window !== 'undefined' && window.currentTf) ? window.currentTf : 'm1';

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

// AI Copilot Action Handler Delegate
let _copilotCallback = null;
export function onCopilotAction(fn) {
    _copilotCallback = fn;
}
export function triggerCopilotAction(rec) {
    if (typeof _copilotCallback === 'function') {
        _copilotCallback(rec);
    }
}
