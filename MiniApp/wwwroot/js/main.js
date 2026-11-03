import { initPriceWebSocket, closePriceWebSocket, handleWakeupReconnect, resetWebSocketBackoff, syncTime, executeAnalysis, cancelAnalysis, resetSignalKey } from './api.js?v=20261103_audit_v7';
import { tg, getCustomInitData, currentAsset, currentTf, setCurrentAsset, setCurrentTf, timeOffset, onCopilotAction, setNetworkOnlineState, clearExpiredAuthParams } from './state.js?v=20261103_audit_v7';
export { tg, getCustomInitData, currentAsset, currentTf };
import { switchResultTab, updateTrafficLight, showNetworkBanner, hideNetworkBanner, renderError, updateCanvasDimensions, hapticImpact, hapticSelection } from './ui.js?v=20261103_audit_v7';

// 1. Telegram WebApp Lifecycle, Viewport & Theme Synchronization
const syncViewport = () => {
    try {
        const vv = (typeof window !== 'undefined') ? window.visualViewport : null;
        const vh = vv ? vv.height : ((tg && tg.viewportHeight) ? tg.viewportHeight : window.innerHeight);
        const vsh = (tg && tg.viewportStableHeight) ? tg.viewportStableHeight : (window.innerHeight || vh);
        if (vh) {
            document.documentElement.style.setProperty('--tg-viewport-height', `${Math.round(vh)}px`);
        }
        if (vsh) {
            document.documentElement.style.setProperty('--tg-viewport-stable-height', `${Math.round(vsh)}px`);
        }
        if (vv) {
            const keyboardHeight = Math.max(0, window.innerHeight - vv.height);
            document.documentElement.style.setProperty('--keyboard-height', `${Math.round(keyboardHeight)}px`);
            document.documentElement.style.setProperty('--visual-viewport-offset-top', `${Math.round(vv.offsetTop)}px`);
            const isKeyboardOpen = keyboardHeight > 100;
            document.body.classList.toggle('keyboard-visible', isKeyboardOpen);
        }
    } catch (e) {}
};

// Синхронная установка высоты вьюпорта до первого кадра рендера (устраняет скачок 100vh на iOS Safari)
syncViewport();

if (typeof window !== 'undefined' && window.visualViewport) {
    window.visualViewport.addEventListener('resize', () => {
        syncViewport();
        if (typeof updateCanvasDimensions === 'function') updateCanvasDimensions();
    }, { passive: true });
    window.visualViewport.addEventListener('scroll', syncViewport, { passive: true });
}

if (tg) {
    try {
        tg.ready();
        tg.expand();
        if (typeof tg.enableClosingConfirmation === 'function') {
            tg.enableClosingConfirmation();
        }
        if (typeof tg.disableVerticalSwipes === 'function') {
            tg.disableVerticalSwipes();
        }
        
        // Синхронизация системного бара и оверскролла Telegram с темой терминала
        const syncTgTheme = () => {
            try {
                const themeColor = '#03010c';
                if (typeof tg.setHeaderColor === 'function') tg.setHeaderColor(themeColor);
                if (typeof tg.setBackgroundColor === 'function') tg.setBackgroundColor(themeColor);

                let metaTheme = document.querySelector('meta[name="theme-color"]');
                if (!metaTheme) {
                    metaTheme = document.createElement('meta');
                    metaTheme.setAttribute('name', 'theme-color');
                    document.head.appendChild(metaTheme);
                }
                metaTheme.setAttribute('content', themeColor);

                const scheme = (tg && tg.colorScheme) ? tg.colorScheme : 'dark';
                document.documentElement.setAttribute('data-color-scheme', scheme);
                if (typeof updateCanvasDimensions === 'function') {
                    updateCanvasDimensions();
                }
            } catch (e) {}
        };
        syncTgTheme();

        // Повторная синхронизация актуального вьюпорта после expand()
        syncViewport();

        if (typeof tg.onEvent === 'function') {
            tg.onEvent('themeChanged', syncTgTheme);
            
            // Обработка открытия/закрытия виртуальной клавиатуры и изменения вьюпорта
            tg.onEvent('viewportChanged', (eventData) => {
                syncViewport();
                if (typeof updateCanvasDimensions === 'function') {
                    updateCanvasDimensions();
                }
            });
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
} else {
    window.addEventListener('resize', syncViewport, { passive: true });
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

function filterAssets(query) {
    const gridEl = document.getElementById('assetGrid');
    if (!gridEl) return;
    const q = (query || '').trim().toUpperCase();
    const filtered = q ? assetsList.filter(a => a.toUpperCase().includes(q)) : assetsList;
    if (filtered.length === 0) {
        gridEl.innerHTML = `<div class='otc-scroll' style='grid-column:1/-1'><div style='text-align:center;padding:16px;color:var(--dim);font-size:12px;'>Активы не найдены</div></div>`;
        return;
    }
    gridEl.innerHTML = `<div class='otc-scroll' style='grid-column:1/-1'><div class='asset-grid'>${renderAssets(filtered)}</div></div>`;
    const safeAsset = (window.CSS && CSS.escape) ? CSS.escape(currentAsset) : currentAsset.replace(/(["\\])/g, '\\$1');
    const activeItem = gridEl.querySelector(`.asset-item[data-asset="${safeAsset}"]`);
    if (activeItem) activeItem.classList.add('active');
}

function initAssetGrid() {
    filterAssets('');
    const searchInput = document.getElementById('assetSearchInput');
    if (searchInput && !searchInput.dataset.initialized) {
        searchInput.dataset.initialized = 'true';
        searchInput.addEventListener('input', (e) => {
            filterAssets(e.target.value);
        });
        searchInput.addEventListener('click', (e) => {
            e.stopPropagation();
        });
        searchInput.addEventListener('focus', () => {
            setTimeout(() => {
                searchInput.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }, 100);
        });
    }
}

function toggleMenu(m) {
    document.querySelectorAll('.asset-menu, .tf-menu').forEach(menu => { 
        if (menu.id !== m) menu.classList.remove('show'); 
    });
    const menuEl = document.getElementById(m);
    if (menuEl) {
        menuEl.classList.toggle('show');
        if (m === 'assetMenu' && menuEl.classList.contains('show')) {
            const searchInput = document.getElementById('assetSearchInput');
            if (searchInput) {
                searchInput.value = '';
                filterAssets('');
            }
        }
    }
}

let switchThrottleTimer = null;
let copilotExecTimer = null;
let countdownTimerId = null;

export function cancelCopilotTimer() {
    if (copilotExecTimer) {
        clearTimeout(copilotExecTimer);
        copilotExecTimer = null;
    }
}

function throttledWsSwitch() {
    cancelCopilotTimer();
    cancelAnalysis();
    closePriceWebSocket(); // Immediate disconnect: prevents stale ticks from old pair leaking into UI
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
    hapticSelection();
    
    const searchInput = document.getElementById('assetSearchInput');
    if (searchInput) {
        searchInput.value = '';
        filterAssets('');
        searchInput.blur();
    }
    
    // Если актив не изменился — просто закрываем меню без сброса сокета и анализа
    if (a === currentAsset) {
        const menuEl = document.getElementById('assetMenu');
        if (menuEl) menuEl.classList.remove('show');
        return;
    }
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
    hapticSelection();
    
    // Если таймфрейм не изменился — закрываем меню без прерывания потока
    if (tf.toLowerCase() === currentTf.toLowerCase()) {
        const menuEl = document.getElementById('tfMenu');
        if (menuEl) menuEl.classList.remove('show');
        return;
    }
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

export function applyCopilotRecommendation(rec) {
    if (!rec) return;
    cancelCopilotTimer();
    let assetChanged = false;
    let tfChanged = false;
    if (rec.asset && rec.asset !== currentAsset) {
        setCurrentAsset(rec.asset);
        const selEl = document.getElementById('selectedAsset');
        if (selEl) selEl.textContent = rec.asset;
        document.querySelectorAll('.asset-item').forEach(i => {
            i.classList.toggle('active', i.getAttribute('data-asset') === rec.asset);
        });
        assetChanged = true;
    }
    if (rec.tf && rec.tf.toLowerCase() !== currentTf.toLowerCase()) {
        const tfClean = rec.tf.toLowerCase();
        setCurrentTf(tfClean);
        const selEl = document.getElementById('selectedTf');
        if (selEl) selEl.textContent = tfClean.toUpperCase();
        document.querySelectorAll('.tf-btn').forEach(i => {
            i.classList.toggle('active', (i.getAttribute('data-tf') || '').toLowerCase() === tfClean);
        });
        tfChanged = true;
    }
    if (assetChanged || tfChanged) {
        const sphere = document.getElementById('mainSphere');
        if (sphere) sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
        throttledWsSwitch();
        resetSignalKey();
        copilotExecTimer = setTimeout(() => {
            copilotExecTimer = null;
            executeAnalysis();
        }, 250);
    } else {
        executeAnalysis();
    }
}

function handleGlobalInteraction(e) {
    const target = e.target;
    if (!target) return;

    const btnGet = target.closest('#btnGet');
    if (btnGet) {
        hapticImpact('medium');
        executeAnalysis();
        return;
    }

    const copilotBtn = target.closest('#copilotActionBtn');
    if (copilotBtn) {
        hapticImpact('light');
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
        hapticSelection();
        switchResultTab('chart');
        return;
    }

    const tabBtnAI = target.closest('#tabBtnAI');
    if (tabBtnAI) {
        hapticSelection();
        switchResultTab('ai');
        return;
    }

    // Закрываем меню при клике вне триггеров и самих списков меню
    if (!target.closest('.dropdown-trigger') && !target.closest('.asset-menu') && !target.closest('.tf-menu')) {
        const am = document.getElementById('assetMenu');
        const tm = document.getElementById('tfMenu');
        if (am && am.classList.contains('show')) am.classList.remove('show');
        if (tm && tm.classList.contains('show')) tm.classList.remove('show');
    }
}

document.addEventListener('click', handleGlobalInteraction);

// Initialize asset DOM elements first so subsequent URL params can match them
initAssetGrid();

// URL Params with CSS Injection Guard
(function() {
    try {
        const p = new URLSearchParams(window.location.search);
        const a = p.get('asset');
        const t = p.get('tf');
        if (a) {
            const safeA = (window.CSS && CSS.escape) ? CSS.escape(a.toUpperCase()) : a.toUpperCase().replace(/(["\\])/g, '\\$1');
            const el = document.querySelector(`.asset-item[data-asset="${safeA}"]`);
            if (el) { setAsset(el); el.scrollIntoView && el.scrollIntoView({ block: 'nearest' }); }
        }
        if (t) {
            const safeT = (window.CSS && CSS.escape) ? CSS.escape(t.toUpperCase()) : t.toUpperCase().replace(/(["\\])/g, '\\$1');
            const el = document.querySelector(`.tf-btn[data-tf="${safeT}"]`);
            if (el) setTf(el);
        }
    } catch(e) {}
})();

function getTfSeconds() {
    const map = { s3:3, s5:5, s10:10, s15:15, s30:30, m1:60, m2:120, m3:180, m5:300, m15:900, m30:1800, h1:3600, h4:14400, d1:86400 };
    const tfClean = (currentTf || 'm1').toLowerCase();
    return map[tfClean] || 60;
}

let cachedCandleTimeEl = null;
let lastCandleClass = '';
let lastCandleText = '';

function updateCountdown() {
    if (document.hidden) return;
    if (!cachedCandleTimeEl || !cachedCandleTimeEl.isConnected) {
        cachedCandleTimeEl = document.getElementById('candleTime');
        if (!cachedCandleTimeEl) return;
    }
    const tfSec = getTfSeconds();
    const nowSec = Math.floor((Date.now() + timeOffset) / 1000);
    const elapsed = nowSec % tfSec;
    const remaining = (tfSec - 1) - elapsed; // Range: [tfSec - 1 ... 0]
    const mins = Math.floor(remaining / 60);
    const secs = remaining % 60;
    const nextText = `${mins}:${secs.toString().padStart(2, '0')}`;
    if (nextText !== lastCandleText) {
        cachedCandleTimeEl.textContent = nextText;
        lastCandleText = nextText;
    }
    
    let greenThresh = 2;
    let yellowThresh = 8;
    
    if (tfSec <= 5) {
        greenThresh = 0; yellowThresh = 1;
    } else if (tfSec <= 10) {
        greenThresh = 1; yellowThresh = 2;
    } else if (tfSec <= 15) {
        greenThresh = 1; yellowThresh = 3;
    } else if (tfSec <= 30) {
        greenThresh = 2; yellowThresh = 5;
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

let resumeDebounceTimer = null;
function resumeLive() {
    if (document.hidden) return;
    if (resumeDebounceTimer) clearTimeout(resumeDebounceTimer);
    resumeDebounceTimer = setTimeout(() => {
        resumeDebounceTimer = null;
        if (document.hidden) return;
        hideNetworkBanner();
        resetWebSocketBackoff();
        syncTime().finally(updateCountdown);
        updateCountdown();
        // Защита от дублирующего соединения: если реконнект пробуждения сработал, второй вызов опускается
        if (!handleWakeupReconnect()) {
            initPriceWebSocket();
        }
    }, 150);
}

// Banner tap recovery
const netAlertEl = document.getElementById('networkAlert');
if (netAlertEl) {
    netAlertEl.style.cursor = 'pointer';
    netAlertEl.addEventListener('click', () => {
        hapticImpact('light');
        resumeLive();
    });
}

syncTime().finally(updateCountdown);
initPriceWebSocket();

function tickCountdown() {
    if (document.hidden) {
        countdownTimerId = null;
        return;
    }
    updateCountdown();
    const nowMs = Date.now() + timeOffset;
    const drift = ((nowMs % 1000) + 1000) % 1000;
    const delay = Math.max(50, 1000 - drift + 5);
    countdownTimerId = setTimeout(tickCountdown, delay);
}
tickCountdown();

// Page Visibility API - Battery and Railway Resource Protection
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        cancelAnalysis();
        closePriceWebSocket();
        cancelCopilotTimer();
        if (countdownTimerId) {
            clearTimeout(countdownTimerId);
            countdownTimerId = null;
        }
    } else {
        syncViewport();
        if (typeof updateCanvasDimensions === 'function') {
            updateCanvasDimensions();
        }
        updateCountdown();
        if (!countdownTimerId) {
            tickCountdown();
        }
        if (navigator.onLine) {
            resumeLive();
        } else {
            showNetworkBanner('Нет интернет-соединения');
        }
    }
});

window.addEventListener('focus', () => {
    syncViewport();
    if (typeof updateCanvasDimensions === 'function') updateCanvasDimensions();
});
window.addEventListener('pageshow', () => {
    syncViewport();
    if (typeof updateCanvasDimensions === 'function') updateCanvasDimensions();
    resumeLive();
});
window.addEventListener('pagehide', () => {
    cancelAnalysis();
    closePriceWebSocket();
    cancelCopilotTimer();
    if (countdownTimerId) {
        clearTimeout(countdownTimerId);
        countdownTimerId = null;
    }
});

if (typeof document !== 'undefined') {
    document.addEventListener('freeze', () => {
        cancelAnalysis();
        closePriceWebSocket();
        cancelCopilotTimer();
    });
}
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
    closePriceWebSocket();
    cancelAnalysis();
    showNetworkBanner('Нет интернет-соединения');
    renderError('⚠️ Ошибка соединения', 'Соединение с интернетом потеряно. Проверьте подключение (4G/Wi-Fi).');
    const valEl = document.getElementById('livePriceValue');
    if (valEl) {
        valEl.innerText = 'ОФФЛАЙН';
        valEl.className = 'live-price-value';
    }
});
