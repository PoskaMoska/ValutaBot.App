import { 
    tg, 
    currentAsset, 
    currentTf, 
    getCustomInitData,
    getAuthHeader, 
    timeOffset, 
    setTimeOffset, 
    lastPriceVal, 
    setLastPriceVal, 
    priceSocket, 
    setPriceSocket, 
    triggerCopilotAction,
    setCircuitBreakerState,
    isNetworkOnline
} from './state.js?v=20261028_audit_v2';

function resolveAuthData() {
    try {
        if (typeof getAuthHeader === 'function') {
            const h = getAuthHeader();
            if (h) return h;
        }
    } catch (e) {}
    try {
        const webApp = tg || (typeof window !== 'undefined' && window.Telegram && window.Telegram.WebApp);
        if (webApp && webApp.initData) return webApp.initData;
        if (typeof getCustomInitData === 'function') return getCustomInitData();
    } catch (e) {}
    return '';
}

function getSafeLocationUrl() {
    if (typeof window === 'undefined' || !window.location) return 'unknown';
    return `${window.location.origin}${window.location.pathname}`;
}

import { 
    updateLivePriceUI, 
    renderError, 
    clearResults, 
    startStatusBar, 
    stopStatusBar, 
    flashResults, 
    renderDirSvg, 
    renderMiniChart, 
    renderSparklinePrediction, 
    switchResultTab, 
    parseMd, 
    pricesToBars, 
    renderExpiryCandles, 
    showAiChart, 
    hideAiChart, 
    updateAiChartData, 
    updateAiCopilot,
    showNetworkBanner,
    hideNetworkBanner
} from './ui.js?v=20261028_audit_v2';

// Tracks the last signal to detect unchanged results
let lastSignalKey = null; // format: "ASSET_TF_DIRECTION_PROB"
export function resetSignalKey() { lastSignalKey = null; }

let reconnectTimeout = null;
let reconnectAttempts = 0;
let wsWatchdogTimer = null;
let isAnalyzingInProgress = false;
let chartOhlcAbortController = null;

const WS_WATCHDOG_TIMEOUT_MS = 12000;

function resetWsWatchdog() {
    if (wsWatchdogTimer) clearTimeout(wsWatchdogTimer);
    wsWatchdogTimer = setTimeout(() => {
        if (priceSocket && (priceSocket.readyState === WebSocket.OPEN || priceSocket.readyState === WebSocket.CONNECTING)) {
            console.warn('[WS] Watchdog triggered: socket stalled in state', priceSocket.readyState);
            try { priceSocket.close(4000, 'Watchdog timeout'); } catch (e) {}
        }
    }, WS_WATCHDOG_TIMEOUT_MS);
}

function clearWsWatchdog() {
    if (wsWatchdogTimer) {
        clearTimeout(wsWatchdogTimer);
        wsWatchdogTimer = null;
    }
}

export function resetWebSocketBackoff() {
    reconnectAttempts = 0;
    if (reconnectTimeout) {
        clearTimeout(reconnectTimeout);
        reconnectTimeout = null;
    }
}

function scheduleWsReconnect(valEl) {
    if (document.hidden) return;
    reconnectAttempts++;
    const baseDelay = Math.min(1000 * Math.pow(1.5, reconnectAttempts), 15000);
    const jitter = Math.floor(Math.random() * 800);
    const finalDelay = baseDelay + jitter;
    if (valEl) valEl.innerText = 'ПЕРЕПОДКЛЮЧЕНИЕ...';
    if (reconnectAttempts >= 2) {
        showNetworkBanner(`Переподключение к потоку котировок (попытка ${reconnectAttempts})...`);
    }
    clearTimeout(reconnectTimeout);
    reconnectTimeout = setTimeout(() => {
        if (!document.hidden && (typeof navigator === 'undefined' || navigator.onLine)) {
            initPriceWebSocket();
        }
    }, finalDelay);
}

export function initPriceWebSocket() {
    closePriceWebSocket();

    const isSecondsTf = Boolean(currentTf && currentTf.startsWith('s'));
    const livePriceContainer = document.getElementById('livePriceContainer');
    
    if (!isSecondsTf) {
        if (livePriceContainer) livePriceContainer.style.display = 'none';
        return;
    }

    if (livePriceContainer) livePriceContainer.style.display = 'flex';
    const valEl = document.getElementById('livePriceValue');
    if (valEl) {
        valEl.innerText = 'ПОДКЛЮЧЕНИЕ...';
        valEl.className = 'live-price-value';
    }

    if (typeof navigator !== 'undefined' && !navigator.onLine) {
        if (valEl) valEl.innerText = 'ОФФЛАЙН';
        showNetworkBanner('Нет интернет-соединения');
        return;
    }

    try {
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${protocol}//${window.location.host}/ws/prices?asset=${encodeURIComponent(currentAsset)}`;
        
        const sock = new WebSocket(wsUrl);
        setPriceSocket(sock);

        // Start watchdog immediately to guard against blackholed TCP SYN handshakes
        resetWsWatchdog();

        sock.onopen = function() {
            if (priceSocket !== sock) return;
            reconnectAttempts = 0;
            hideNetworkBanner();
            resetWsWatchdog();
        };

        sock.onmessage = function(event) {
            if (priceSocket !== sock) return;
            resetWsWatchdog();
            try {
                const data = JSON.parse(event.data);
                if (data && data.price !== undefined) {
                    const newPrice = Number(data.price);
                    if (Number.isFinite(newPrice) && newPrice > 0) {
                        updateLivePriceUI(newPrice);
                        setLastPriceVal(newPrice);
                    }
                }
            } catch (e) {
                console.error('Error parsing WS message:', e);
            }
        };

        sock.onclose = function(e) {
            if (priceSocket !== sock) return;
            clearWsWatchdog();
            if (e.code === 1000 || document.hidden) return;
            scheduleWsReconnect(valEl);
        };

        sock.onerror = function(err) {
            console.warn('Price WebSocket error:', err);
        };
    } catch (err) {
        console.warn('Failed to create WebSocket:', err);
        scheduleWsReconnect(valEl);
    }
}

export function closePriceWebSocket() {
    clearWsWatchdog();
    if (reconnectTimeout) {
        clearTimeout(reconnectTimeout);
        reconnectTimeout = null;
    }
    if (priceSocket) {
        try {
            priceSocket.onopen = null;
            priceSocket.onmessage = null;
            priceSocket.onerror = null;
            priceSocket.onclose = null;
            if (priceSocket.readyState === WebSocket.OPEN || priceSocket.readyState === WebSocket.CONNECTING) {
                priceSocket.close(1000);
            }
        } catch (e) {}
        setPriceSocket(null);
    }
    setLastPriceVal(0);
}

export async function syncTime() {
    if (typeof navigator !== 'undefined' && !navigator.onLine) return;
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 5000);
    try {
        const r = await fetch('/api/time', {
            headers: {
                'X-Telegram-Init-Data': resolveAuthData()
            },
            signal: controller.signal
        });
        if (r.ok) {
            const d = await r.json();
            if (d && Number.isFinite(Number(d.t))) {
                setTimeOffset(Number(d.t) - Date.now());
            }
        }
    } catch (e) {
        // Preserve previous valid timeOffset on transient 4G drops instead of resetting to 0
    } finally {
        clearTimeout(timeoutId);
    }
}

export async function executeAnalysis() {
    const btn = document.getElementById('btnGet');
    if (isAnalyzingInProgress || (btn && btn.disabled)) return;

    if (typeof navigator !== 'undefined' && !navigator.onLine) {
        showNetworkBanner('Нет интернет-соединения');
        renderError('⚠️ Ошибка соединения', 'Отсутствует подключение к интернету. Проверьте 4G/Wi-Fi и повторите попытку.');
        return;
    }

    // Mutex lock against rapid double-clicks
    isAnalyzingInProgress = true;
    if (btn) {
        btn.disabled = true;
        btn.innerText = 'СКАНИРОВАНИЕ...';
    }

    const sphere = document.getElementById('mainSphere');
    const ed = document.getElementById('errorDisplay');
    if (ed) ed.style.display = 'none';

    clearResults();
    startStatusBar();

    if (sphere) {
        sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
        sphere.classList.add('analyzing');
    }

    showAiChart(currentAsset, currentTf);

    // Cancel any previous in-flight chart-ohlc request
    if (chartOhlcAbortController) {
        chartOhlcAbortController.abort();
    }
    chartOhlcAbortController = new AbortController();

    // Fetch early OHLC for the animated chart during the analysis phase
    fetch(`/api/chart-ohlc?asset=${encodeURIComponent(currentAsset)}&timeframe=${encodeURIComponent(currentTf)}`, {
        headers: {
            'X-Telegram-Init-Data': resolveAuthData()
        },
        signal: chartOhlcAbortController.signal
    })
        .then(r => r.ok ? r.json() : [])
        .then(ohlc => {
            if (Array.isArray(ohlc) && ohlc.length) {
                updateAiChartData(ohlc);
            }
        })
        .catch(() => {});

    const startTime = Date.now();
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 18000); // 18s fail-safe timeout

    try {
        const res = await fetch(`/api/analyze?asset=${encodeURIComponent(currentAsset)}&timeframe=${encodeURIComponent(currentTf)}&_=${Date.now()}`, {
            headers: {
                'X-Telegram-Init-Data': resolveAuthData()
            },
            signal: controller.signal
        });

        clearTimeout(timeoutId);

        if (!res.ok) {
            let errorText = `HTTP_${res.status}: ${res.statusText || 'Error'}`;
            try {
                const errData = await res.json();
                if (errData && errData.error) errorText = `${errData.error} (HTTP ${res.status})`;
                else if (errData && errData.detail) errorText = `${errData.detail} (HTTP ${res.status})`;
                else if (errData && errData.title) errorText = `${errData.title} (HTTP ${res.status})`;
            } catch (_) {
                if (res.status === 502 || res.status === 503 || res.status === 504) {
                    errorText = `Сервер обновляется или временно недоступен (HTTP ${res.status})`;
                } else if (res.status === 500) {
                    errorText = 'Internal Server Error (HTTP 500)';
                } else if (res.status === 401 || res.status === 403) {
                    errorText = `Unauthorized (HTTP ${res.status})`;
                } else if (res.status === 429) {
                    errorText = 'Too many requests (HTTP 429)';
                }
            }
            throw new Error(errorText);
        }

        const contentType = (res.headers && typeof res.headers.get === 'function')
            ? (res.headers.get('content-type') || '')
            : 'application/json';
        if (contentType && !contentType.includes('application/json')) {
            throw new Error(`Некорректный формат ответа прокси (HTTP ${res.status})`);
        }

        const rawData = await res.json();
        
        let data = rawData;
        let config = null;
        if (rawData.result && rawData.config) {
            data = rawData.result;
            config = rawData.config;
        }

        const elapsed = Date.now() - startTime;
        const remainingDelay = (currentTf && currentTf.toLowerCase().startsWith('s')) ? 0 : Math.max(0, 450 - elapsed);
        if (remainingDelay > 0) {
            await new Promise(resolve => setTimeout(resolve, remainingDelay));
        }

        hideAiChart();
        stopStatusBar();
        if (sphere) sphere.classList.remove('analyzing');

        // 1. Explicit Circuit Breaker Check First
        if (data.reason === 'CIRCUIT_BREAKER_ACTIVE' || (data.message && data.message.includes('Circuit Breaker'))) {
            if (typeof setCircuitBreakerState === 'function') setCircuitBreakerState(true);
            renderError('⚠️ ТОРГИ ПРИОСТАНОВЛЕНЫ ⚠️', 'Сработал защитный предохранитель депозита (Circuit Breaker).\n' + (data.message || ''));
            return;
        }

        // 2. Generic Error Check
        if (data.error) {
            const safeLoc = getSafeLocationUrl();
            const debugMsg = `• Длина токена: ${tg && tg.initData ? tg.initData.length : 0}\n• Платформа: ${tg ? tg.platform : 'unknown'}\n• Адрес: ${safeLoc}`;
            renderError(data.error, debugMsg);
            return;
        }

        if (typeof setCircuitBreakerState === 'function') setCircuitBreakerState(false);
        
        // Apply config to UI elements if present
        if (config) {
            const mlCard = document.getElementById('mlEnsembleCard');
            if (mlCard) mlCard.style.display = config.ml ? 'block' : 'none';
            
            const smcCard = document.getElementById('smcCard');
            if (smcCard) smcCard.style.display = config.smc ? 'block' : 'none';
            
            const ofCard = document.getElementById('orderFlowCard');
            if (ofCard) ofCard.style.display = 'none'; // OF отключён (35.8% anti-signal)
        }

        const resDir = document.getElementById('resDir');
        const signalKey = `${currentAsset}_${currentTf}_${data.direction}_${data.probability}`;
        const isRepeat = lastSignalKey !== null && signalKey === lastSignalKey;
        lastSignalKey = signalKey;

        const resDirLabel = document.getElementById('resDirLabel');
        if (resDirLabel) {
            resDirLabel.innerHTML = isRepeat 
                ? 'Направление <span title="Сигнал не изменился" style="font-size:10px;opacity:0.8">🔄</span>'
                : 'Направление';
        }

        if (resDir) {
            if (data.direction === 'BUY') {
                resDir.innerHTML = 'ВВЕРХ';
                resDir.innerText = 'ВВЕРХ';
                resDir.style.color = '#00e676';
                if (sphere) sphere.classList.add('buy-signal');
            } else if (data.direction === 'PUT') {
                resDir.innerHTML = 'ВНИЗ';
                resDir.innerText = 'ВНИЗ';
                resDir.style.color = '#ff1744';
                if (sphere) sphere.classList.add('put-signal');
            } else {
                resDir.innerHTML = 'НЕЙТРАЛЬНО';
                resDir.innerText = 'НЕЙТРАЛЬНО';
                resDir.style.color = 'var(--dim)';
                if (sphere) sphere.classList.add('neutral-signal');
            }
        }

        const probEl = document.getElementById('resProb');
        if (probEl) {
            const probNum = Number(data.probability) || 0;
            probEl.innerText = probNum + '%';
            probEl.style.color = probNum >= 58 ? '#00e676' : probNum >= 54 ? '#ffd600' : 'var(--accent)';
        }

        // Measured fact vs model score
        const factEl = document.getElementById('resProbFact');
        if (factEl) {
            if (data.winRateAsset != null && (data.signalsVerifiedAsset || 0) >= 5) {
                factEl.innerText = `факт: ${Math.round(data.winRateAsset)}% (n=${data.signalsVerifiedAsset})`;
            } else {
                factEl.innerText = '';
            }
        }

        const durEl = document.getElementById('resDur');
        if (durEl) durEl.innerText = data.duration || '--';

        if (data.rsi !== undefined) {
            const rsiEl = document.getElementById('resRsi');
            if (rsiEl) {
                rsiEl.innerText = data.rsi;
                rsiEl.style.color = data.rsi > 70 ? '#ff1744' : data.rsi < 30 ? '#00e676' : 'var(--subtext)';
            }
        }
        if (data.ema !== undefined) {
            const emaEl = document.getElementById('resEma');
            if (emaEl) emaEl.innerText = data.ema;
        }
        if (data.volumeStrength !== undefined) {
            const volEl = document.getElementById('resVol');
            if (volEl) {
                const vs = Number(data.volumeStrength) || 0;
                if (Math.abs(vs) > 0.1) {
                    volEl.innerText = vs > 0 ? '↑ ' + vs.toFixed(1) + 'x' : '↓ ' + Math.abs(vs).toFixed(1) + 'x';
                    volEl.style.color = vs > 0.5 ? '#00e676' : vs < -0.5 ? '#ff1744' : 'var(--subtext)';
                } else {
                    volEl.innerText = 'Баланс';
                    volEl.style.color = 'var(--subtext)';
                }
            }
        }
        if (data.tfConflict) {
            const rp = document.getElementById('resProb');
            if (rp) rp.innerHTML += " <span title='Конфликт со старшим таймфреймом (учтен нейросетью)'>⚠️</span>";
        }

        // Market Weather Bindings
        const mwCard = document.getElementById('marketWeatherCard');
        if (mwCard) {
            if (data.uiMarketSession || data.uiMarketPhase || data.uiMarketEntropy || data.direction) {
                mwCard.style.display = 'block';
            } else {
                mwCard.style.display = 'none';
            }
        }
        const wSession = document.getElementById("weatherSession");
        if (wSession && data.uiMarketSession) {
            wSession.innerText = data.uiMarketSession;
            let sColor = "#f59e0b";
            let sessionLow = data.uiMarketSession.toLowerCase();
            if (sessionLow.includes("европа") || sessionLow.includes("америка") || sessionLow.includes("нью-йорк") || sessionLow.includes("лондон")) {
                sColor = "#10b981";
            } else if (sessionLow.includes("otc")) {
                sColor = "#8b5cf6";
            } else if (sessionLow.includes("мертв") || sessionLow.includes("пил") || sessionLow.includes("ази")) {
                sColor = "#f59e0b";
            }
            wSession.style.color = sColor;
        }
        
        const wPhase = document.getElementById("weatherPhase");
        if (wPhase && data.uiMarketPhase) {
            wPhase.innerText = data.uiMarketPhase;
            let phaseColor = "#10b981";
            let phaseLow = data.uiMarketPhase.toLowerCase();
            if (phaseLow.includes("флэт") || phaseLow.includes("замедление") || phaseLow.includes("консолидация") || phaseLow.includes("боковик")) {
                phaseColor = "#f59e0b";
            } else if (phaseLow.includes("медвеж") || phaseLow.includes("перепродан") || phaseLow.includes("падени") || phaseLow.includes("сброс")) {
                phaseColor = "#ef4444";
            } else if (phaseLow.includes("быч") || phaseLow.includes("перекуплен") || phaseLow.includes("рост")) {
                phaseColor = "#10b981";
            }
            wPhase.style.color = phaseColor;
        }
        
        const wEntropy = document.getElementById("weatherEntropy");
        const wTitle = document.getElementById("weatherTitle");
        if (wEntropy && data.uiMarketEntropy) {
            wEntropy.innerText = data.uiMarketEntropy;
            let entColor = "#10b981";
            let entLow = data.uiMarketEntropy.toLowerCase();
            if (entLow.includes("норм") || entLow.includes("безопасн")) {
                entColor = "#10b981";
            } else if (entLow.includes("хаос") || entLow.includes("опасн") || entLow.includes("шторм")) {
                entColor = "#ef4444";
            } else if (entLow.includes("мертв") || entLow.includes("сонн") || entLow.includes("штиль")) {
                entColor = "#f59e0b";
            }
            wEntropy.style.color = entColor;
            if (wTitle) wTitle.style.color = entColor;
        }

        // ML Ensemble Card
        if (data.llmReport && !data.llmReport.includes('Оффлайн')) {
            const mlCard = document.getElementById('mlEnsembleCard');
            if (mlCard) mlCard.style.display = (config && config.ml === false) ? 'none' : 'block';
            const badge = document.getElementById('mlEnsembleBadge') || document.getElementById('mlModelBadge');
            const isEnabled = data.lgbmModelVersion && data.lgbmModelVersion !== 'disabled';
            if (badge) {
                badge.innerText = isEnabled ? '🧠 ML Ансамбль' : '⚠️ ML';
                badge.style.background = isEnabled ? 'linear-gradient(135deg,#8b5cf6,#6d28d9)' : 'rgba(100,100,100,0.4)';
            }
            const dir = document.getElementById('mlEnsembleDir');
            if (dir && data.lgbmDirection) {
                dir.innerText = data.lgbmDirection === 'BUY' ? 'ВВЕРХ' : data.lgbmDirection === 'PUT' ? 'ВНИЗ' : '—';
                dir.style.color = data.lgbmDirection === 'BUY' ? '#a78bfa' : data.lgbmDirection === 'PUT' ? '#f472b6' : 'var(--subtext)';
            }
            const conf = document.getElementById('mlEnsembleConf');
            if (conf && data.lgbmConfidence != null) {
                conf.innerText = Math.round(data.lgbmConfidence) + '%';
            }
            const rep = document.getElementById('mlEnsembleReport');
            if (rep) {
                rep.innerHTML = parseMd(data.llmReport);
            }
        } else {
            const mlCard = document.getElementById('mlEnsembleCard');
            if (mlCard) mlCard.style.display = 'none';
        }

        // Confluence + Win Rate Card
        const confCard = document.getElementById('confluenceCard');
        const hasConfData = data.confluenceLabel || data.winRateAsset != null || data.winRateOverall != null;
        if (confCard) confCard.style.display = hasConfData ? 'block' : 'none';
        
        const confLabel = document.getElementById('confluenceLabel');
        if (confLabel) confLabel.innerText = data.confluenceLabel || 'Анализ';
        const goldenBadge = document.getElementById('goldenSetupBadge');
        const goldenBadgeMain = document.getElementById('goldenSetupBadgeMain');
        if (goldenBadge) goldenBadge.style.display = data.goldenSetup ? 'inline-block' : 'none';
        if (goldenBadgeMain) goldenBadgeMain.style.display = data.goldenSetup ? 'inline-block' : 'none';
        
        const wrAssetEl = document.getElementById('winRateAsset');
        if (wrAssetEl) {
            if (data.winRateAsset != null) {
                const pct = Math.round(data.winRateAsset);
                wrAssetEl.innerText = pct + '%';
                wrAssetEl.style.color = pct >= 55 ? '#10b981' : pct >= 50 ? '#f59e0b' : '#f43f5e';
            } else {
                wrAssetEl.innerText = 'нет данных';
                wrAssetEl.style.color = 'var(--subtext)';
            }
        }
        const wrOverallEl = document.getElementById('winRateOverall');
        if (wrOverallEl) {
            if (data.winRateOverall != null) {
                const pct = Math.round(data.winRateOverall);
                wrOverallEl.innerText = pct + '%';
                wrOverallEl.style.color = pct >= 55 ? '#10b981' : pct >= 50 ? '#f59e0b' : '#f43f5e';
            } else {
                wrOverallEl.innerText = 'нет данных';
            }
        }
        const sigCountEl = document.getElementById('signalsCount') || document.getElementById('signalCount');
        if (sigCountEl) {
            const verified = data.signalsVerified || 0;
            const pending = data.signalsPending || 0;
            sigCountEl.innerText = verified + (pending > 0 ? ' (+' + pending + ')' : '');
        }

        // Consensus Radar Card
        const rCard = document.getElementById('consensusRadarCard');
        if (rCard) {
            if (data.direction) {
                rCard.style.display = 'block';

                const formatDir = (dir, conf) => {
                    const confNum = Number(conf);
                    const confStr = Number.isFinite(confNum) && confNum > 0 ? ` (${Math.round(confNum)}%)` : '';
                    if (dir === 'BUY') return `<span style='color:#10b981; display:flex; align-items:center; gap:4px;'><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg> ВВЕРХ${confStr}</span>`;
                    if (dir === 'PUT') return `<span style='color:#ef4444; display:flex; align-items:center; gap:4px;'><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 5v14M19 12l-7 7-7-7"/></svg> ВНИЗ${confStr}</span>`;
                    return `<span style='color:var(--subtext); display:flex; align-items:center; gap:4px;'><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14"/></svg> НЕЙТРАЛЬНО</span>`;
                };

                const rMl = document.getElementById('radarMl');
                if (rMl) rMl.innerHTML = formatDir(data.lgbmDirection, data.lgbmConfidence);

                const rSmc = document.getElementById('radarSmc');
                if (rSmc) rSmc.innerHTML = formatDir(data.smcDirection, data.smcConfidence);

                const rOf = document.getElementById('radarOf');
                if (rOf) { rOf.closest('[data-module="of"]')?.style.setProperty('display','none'); rOf.innerHTML = ''; }

                const rTa = document.getElementById('radarTa');
                if (rTa) rTa.innerHTML = formatDir(data.taDirection, data.taConfidence);
            } else {
                rCard.style.display = 'none';
            }
        }

        const probBars = pricesToBars(data.chartData, 20);
        let projFrac = null;
        if (data.atr > 0 && data.chartData && data.chartData.length >= 2 && (data.expiryCandles | 0) > 0) {
            const tailP = data.chartData.slice(-20);
            const spanP = Math.max(...tailP) - Math.min(...tailP);
            if (spanP > 1e-12) {
                projFrac = Math.min(0.9, Math.max(0.05, (data.atr * Math.sqrt(data.expiryCandles)) / spanP * 0.8));
            }
        }
        if (probBars.length) renderSparklinePrediction('probChart', probBars, data.direction, projFrac);

        renderDirSvg(data.direction);

        const durBars = pricesToBars(data.chartData, 8);
        if (data.chartOhlc && data.chartOhlc.length) renderExpiryCandles('durChart', data.chartOhlc, data.expiryCandles);
        else if (durBars.length) renderMiniChart('durChart', durBars, '');

        // AI Copilot Tactical Intelligence
        updateAiCopilot(data, (rec) => {
            triggerCopilotAction(rec);
        });

        const tabReg = document.getElementById('resultsTabBar');
        if (tabReg) tabReg.style.display = 'flex';
        switchResultTab('chart');
        flashResults();
    } catch(e) {
        clearTimeout(timeoutId);
        hideAiChart(); // Ensure Canvas loop is stopped on failure!
        stopStatusBar();
        if (sphere) sphere.classList.remove('analyzing');

        const isAuthError = Boolean(e && e.message && (e.message.includes('HTTP 401') || e.message.includes('HTTP_401') || e.message.includes('auth_date') || e.message.includes('authorization') || e.message.includes('Access Denied')));
        let errorMsg;
        if (e.name === 'AbortError') {
            errorMsg = 'Превышен лимит времени ответа сервера (18с)';
        } else if (e instanceof TypeError && (e.message.includes('fetch') || e.message.includes('NetworkError') || e.message.includes('Failed to fetch'))) {
            errorMsg = 'Сетевая ошибка: проверьте интернет-соединение или доступ к серверу';
        } else if (isAuthError) {
            errorMsg = 'Сессия устарела или доступ не авторизован. Перезапустите бота командой /start в Telegram.';
        } else {
            errorMsg = e.message || 'Ошибка обработки анализа';
        }
        const safeLoc = getSafeLocationUrl();
        const catchMsg = `• Длина токена: ${tg && tg.initData ? tg.initData.length : 0}\n• Платформа: ${tg ? tg.platform : 'unknown'}\n• Адрес: ${safeLoc}`;
        renderError(errorMsg, catchMsg);
        
        if (window.Sentry && !isAuthError) {
            Sentry.captureException(e, {
                extra: {
                    asset: currentAsset,
                    tf: currentTf,
                    platform: tg ? tg.platform : 'unknown',
                    tokenLength: tg && tg.initData ? tg.initData.length : 0
                }
            });
        }
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.innerText = 'ПОЛУЧИТЬ АНАЛИЗ';
        }
        isAnalyzingInProgress = false;
    }
}
