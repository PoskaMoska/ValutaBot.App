import { showAiChart, hideAiChart, updateAiChartData, clearResults, renderError, startStatusBar, stopStatusBar, updateLivePriceUI } from './ui.js?v=20260918_1';
import { currentAsset, currentTf, getCustomInitData, tg } from './main.js?v=20260918_1';
import { mapBackendResponseToUI } from './mappers.js?v=20260918_1';
import { renderAnalysisResult } from './ui-renderer.js?v=20260918_1';

export let priceSocket = null;
export let lastPriceVal = 0;
export let timeOffset = 0;

let lastSignalKey = null;
export function resetSignalKey() { lastSignalKey = null; }

export function initPriceWebSocket() {
    closePriceWebSocket();

    const isSecondsTf = currentTf.startsWith('s');
    const livePriceContainer = document.getElementById('livePriceContainer');
    
    if (!isSecondsTf) {
        if (livePriceContainer) livePriceContainer.style.display = 'none';
        return;
    }

    if (livePriceContainer) livePriceContainer.style.display = 'flex';
    const valEl = document.getElementById('livePriceValue');
    if (valEl) {
        valEl.innerText = 'ЗАГРУЗКА...';
        valEl.className = 'live-price-value';
    }

    try {
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = ${protocol}///ws/prices?asset=;
        
        priceSocket = new WebSocket(wsUrl);

        priceSocket.onmessage = function(event) {
            try {
                const data = JSON.parse(event.data);
                if (data && data.price !== undefined) {
                    const newPrice = data.price;
                    updateLivePriceUI(newPrice);
                    lastPriceVal = newPrice;
                }
            } catch (e) {
                console.error('Error parsing WS message:', e);
            }
        };

        priceSocket.onclose = function() {};
        priceSocket.onerror = function(err) {};
    } catch (err) {
        console.error('Failed to create WebSocket:', err);
    }
}

export function closePriceWebSocket() {
    if (priceSocket) {
        try { priceSocket.close(); } catch(e) {}
        priceSocket = null;
    }
    lastPriceVal = 0;
}

export async function syncTime() {
    try {
        var r = await fetch('/api/time', {
            headers: { 'X-Telegram-Init-Data': tg ? tg.initData : '' }
        });
        var d = await r.json();
        timeOffset = d.t - Date.now();
    } catch(e) { timeOffset = 0; }
}

export async function executeAnalysis() {
    const btn = document.getElementById('btnGet');
    if (btn && btn.disabled) return;
    const sphere = document.getElementById('mainSphere');
    
    // Capture state to prevent race conditions
    const requestAsset = currentAsset;
    const requestTf = currentTf;
    
    try {
        const ed = document.getElementById('errorDisplay');
        if (ed) ed.style.display = 'none';
        clearResults();
        startStatusBar();

        requestAnimationFrame(() => {
            if (sphere) {
                sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
                sphere.classList.add('analyzing');
            }
            if (btn) {
                btn.disabled = true;
                btn.innerText = 'СКАНИРОВАНИЕ...';
            }
        });

        showAiChart(requestAsset, requestTf);

        fetch(/api/chart-ohlc?asset=&timeframe=, {
            headers: { 'X-Telegram-Init-Data': tg && tg.initData ? tg.initData : getCustomInitData() }
        })
            .then(r => r.json())
            .then(ohlc => {
                // Drop if asset changed
                if (requestAsset !== currentAsset || requestTf !== currentTf) return;
                if (ohlc && ohlc.length) updateAiChartData(ohlc);
            })
            .catch(err => console.log('ohlc error', err));

        const startTime = Date.now();

        const res = await fetch(/api/analyze?asset=&timeframe=&_=, {
            headers: { 'X-Telegram-Init-Data': tg && tg.initData ? tg.initData : getCustomInitData() }
        });
        const rawData = await res.json();
        
        // Strict mapping via unified mapper
        const uiModel = mapBackendResponseToUI(rawData);

        const elapsed = Date.now() - startTime;
        const remainingDelay = Math.max(0, 2000 - elapsed);

        setTimeout(() => {
            // Drop rendering completely if user switched tabs during fetch
            if (requestAsset !== currentAsset || requestTf !== currentTf) {
                if (btn) {
                    btn.disabled = false;
                    btn.innerText = 'ПОЛУЧИТЬ АНАЛИЗ';
                }
                return;
            }

            hideAiChart();
            stopStatusBar();
            if (sphere) sphere.classList.remove('analyzing');
            if (btn) {
                btn.disabled = false;
                btn.innerText = 'ПОЛУЧИТЬ АНАЛИЗ';
            }

            if (uiModel.error) {
                const debugMsg = • Длина токена: \n• Платформа: \n• Адрес: ;
                renderError(uiModel.error, debugMsg);
                return;
            }

            if (uiModel.reason === 'CIRCUIT_BREAKER_ACTIVE') {
                renderError('⚠️ ТОРГИ ПРИОСТАНОВЛЕНЫ ⚠️', 'Сработал защитный предохранитель депозита (Circuit Breaker).\n' + uiModel.message);
                return;
            }
            
            const signalKey = ${requestAsset}___;
            const isRepeat = lastSignalKey !== null && signalKey === lastSignalKey;
            lastSignalKey = signalKey;

            // Render strictly via ui-renderer.js
            renderAnalysisResult(uiModel, btn, sphere, requestAsset, requestTf, isRepeat);

        }, remainingDelay);
    } catch(e) {
        stopStatusBar();
        if (sphere) sphere.classList.remove('analyzing');
        if (btn) {
            btn.disabled = false;
            btn.innerText = 'ПОЛУЧИТЬ АНАЛИЗ';
        }
        const catchMsg = • Длина токена: \n• Платформа: \n• Адрес: ;
        renderError(e.message, catchMsg);
    }
}

