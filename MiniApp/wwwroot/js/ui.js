import { lastPriceVal } from './state.js?v=20261113_nocopilot';

export function hapticImpact(style = 'medium') {
    try {
        const activeTg = (typeof window !== 'undefined' && window.Telegram && window.Telegram.WebApp) ? window.Telegram.WebApp : null;
        if (activeTg && activeTg.HapticFeedback && typeof activeTg.HapticFeedback.impactOccurred === 'function') {
            activeTg.HapticFeedback.impactOccurred(style);
        }
    } catch (_) {}
}

export function hapticNotification(type = 'success') {
    try {
        const activeTg = (typeof window !== 'undefined' && window.Telegram && window.Telegram.WebApp) ? window.Telegram.WebApp : null;
        if (activeTg && activeTg.HapticFeedback && typeof activeTg.HapticFeedback.notificationOccurred === 'function') {
            activeTg.HapticFeedback.notificationOccurred(type);
        }
    } catch (_) {}
}

export function hapticSelection() {
    try {
        const activeTg = (typeof window !== 'undefined' && window.Telegram && window.Telegram.WebApp) ? window.Telegram.WebApp : null;
        if (activeTg && activeTg.HapticFeedback && typeof activeTg.HapticFeedback.selectionChanged === 'function') {
            activeTg.HapticFeedback.selectionChanged();
        }
    } catch (_) {}
}

export function normalizeDirection(dir) {
    if (!dir) return 'NEUTRAL';
    const u = String(dir).trim().toUpperCase();
    if (u === 'BUY' || u === 'CALL' || u === 'UP') return 'BUY';
    if (u === 'PUT' || u === 'SELL' || u === 'DOWN') return 'PUT';
    return 'NEUTRAL';
}

let priceFlashTimeout = null;
let cachedLivePriceEl = null;
let cachedTrafficLightEl = null;
let pendingTickPrice = null;
let tickRafId = null;
let lastRenderedPrice = 0;

export function resetLivePriceFlash() {
    if (tickRafId) {
        cancelAnimationFrame(tickRafId);
        tickRafId = null;
    }
    pendingTickPrice = null;
    lastRenderedPrice = 0;
    if (priceFlashTimeout) {
        clearTimeout(priceFlashTimeout);
        priceFlashTimeout = null;
    }
    if (cachedLivePriceEl && cachedLivePriceEl.isConnected) {
        cachedLivePriceEl.className = 'live-price-value';
    }
}

export function updateLivePriceUI(price) {
    const numPrice = Number(price);
    if (!Number.isFinite(numPrice) || numPrice <= 0) return;

    pendingTickPrice = numPrice;
    if (tickRafId) return;

    tickRafId = requestAnimationFrame(() => {
        tickRafId = null;
        if (pendingTickPrice === null) return;
        const currentPrice = pendingTickPrice;
        pendingTickPrice = null;

        if (!cachedLivePriceEl || !cachedLivePriceEl.isConnected) {
            cachedLivePriceEl = document.getElementById('livePriceValue');
            if (!cachedLivePriceEl) return;
        }

        const isHighVal = currentPrice > 100;
        const formatted = currentPrice.toFixed(isHighVal ? 2 : 5);

        // textContent avoids Layout Thrashing / Reflow on high-frequency quote updates
        if (cachedLivePriceEl.textContent !== formatted) {
            cachedLivePriceEl.textContent = formatted;
        }

        const prevPrice = lastRenderedPrice > 0 ? lastRenderedPrice : lastPriceVal;
        if (prevPrice > 0) {
            if (currentPrice > prevPrice) {
                cachedLivePriceEl.className = 'live-price-value up';
            } else if (currentPrice < prevPrice) {
                cachedLivePriceEl.className = 'live-price-value down';
            }
            
            if (priceFlashTimeout) clearTimeout(priceFlashTimeout);
            priceFlashTimeout = setTimeout(() => {
                if (cachedLivePriceEl && cachedLivePriceEl.textContent === formatted) {
                    cachedLivePriceEl.className = 'live-price-value';
                }
            }, 400);
        } else {
            cachedLivePriceEl.className = 'live-price-value';
        }

        lastRenderedPrice = currentPrice;
    });
}

export function switchResultTab(tabName) {
    const contentChart = document.getElementById('resultsGrid');
    const contentAI = document.getElementById('tabContentAI');

    if (contentChart) contentChart.style.display = 'grid';
    if (contentAI) contentAI.style.display = 'block';
}

export function clearResults() {
    const safeSetText = (id, txt) => { const el = document.getElementById(id); if (el) el.textContent = txt; };
    const safeSetHtml = (id, html) => { const el = document.getElementById(id); if (el) el.innerHTML = html; };
    const safeSetStyle = (id, prop, val) => { const el = document.getElementById(id); if (el) el.style[prop] = val; };

    safeSetText('resProb', '--%');
    safeSetStyle('resProb', 'color', 'var(--accent)');
    safeSetText('resProbFact', '');
    safeSetText('resDirLabel', 'Направление');
    safeSetText('resDir', '--');
    safeSetStyle('resDir', 'color', 'var(--subtext)');
    safeSetText('resDur', '--');
    safeSetText('resRsi', '--');
    safeSetStyle('resRsi', 'color', 'var(--subtext)');
    safeSetText('resEma', '--');
    safeSetText('resVol', '--');
    safeSetStyle('resVol', 'color', 'var(--subtext)');
    safeSetHtml('probChart', '');
    safeSetHtml('dirChart', '<svg viewBox=\'0 0 80 40\'><path d=\'M10 35 L40 5 L70 35\' stroke=\'var(--dim)\' stroke-width=\'2.5\' fill=\'none\' stroke-linecap=\'round\' stroke-linejoin=\'round\' opacity=\'0.3\'/></svg>');
    safeSetHtml('durChart', '');
    safeSetStyle('resultsTabBar', 'display', 'none');
    safeSetStyle('resultsGrid', 'display', 'none');
    safeSetStyle('marketWeatherCard', 'display', 'none');
    safeSetStyle('consensusRadarCard', 'display', 'none');
    safeSetStyle('tabContentAI', 'display', 'none');
    safeSetStyle('mlEnsembleCard', 'display', 'none');
    safeSetStyle('confluenceCard', 'display', 'none');
    safeSetStyle('reasoningCard', 'display', 'none');
    safeSetStyle('newsCard', 'display', 'none');
    safeSetStyle('welcomeSec', 'display', 'flex');
    document.querySelectorAll('.res-card').forEach(c => c.classList.remove('flash'));
}

export function flashResults() {
    requestAnimationFrame(() => {
        const cards = document.querySelectorAll('.res-card');
        cards.forEach(c => c.classList.remove('flash'));
        if (cards.length > 0) void cards[0].offsetWidth; // 1 single reflow instead of loop thrashing
        cards.forEach(c => c.classList.add('flash'));
    });
}

// XSS-Safe Markdown Parser
export function parseMd(text) {
    if (!text) return '';
    const normalized = String(text).replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const escaped = normalized
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');

    return escaped
        .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
        .replace(/\*([^*]+)\*/g, '<i>$1</i>')
        .replace(/\n/g, '<br/>');
}

export function renderMiniChart(containerId, values, color) {
    const container = document.getElementById(containerId);
    if (!container || !Array.isArray(values)) return;
    const safeValues = values.map(Number).filter(Number.isFinite);
    if (!safeValues.length) return;
    const safeColor = String(color || '').replace(/[^a-zA-Z0-9_-]/g, '');
    const max = Math.max(...safeValues, 1);
    container.innerHTML = safeValues.map(v => {
        const h = Math.max(4, (v / max) * 38).toFixed(1);
        return `<div class='res-chart-bar ${safeColor}' style='height:${h}px'></div>`;
    }).join('');
}

function resolveExpiryCandleClass(direction, candle) {
    if (direction !== undefined && direction !== null && String(direction).trim() !== '') {
        const norm = normalizeDirection(direction);
        if (norm === 'BUY') return 'exp-bull';
        if (norm === 'PUT') return 'exp-bear';
        return 'exp-neutral';
    }
    if (candle) return candle.c >= candle.o ? 'exp-bull' : 'exp-bear';
    return 'exp-neutral';
}

// Expiry candles: renders canonical thick candle for 1-candle expiry (always visible regardless of doji/flat OHLC),
// or N readable OHLC candlesticks when count > 1, colored by the final signal direction.
export function renderExpiryCandles(containerId, ohlc, count, direction) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const tail = Array.isArray(ohlc) ? ohlc.map(k => {
        if (!k || typeof k !== 'object') return null;
        const o = Number(k.o ?? k.open ?? k.Open);
        const h = Number(k.h ?? k.high ?? k.High);
        const l = Number(k.l ?? k.low ?? k.Low);
        const c = Number(k.c ?? k.close ?? k.Close);
        if (!Number.isFinite(o) || !Number.isFinite(h) || !Number.isFinite(l) || !Number.isFinite(c) || l <= 0) {
            return null;
        }
        return { o, h: Math.max(h, o, c), l: Math.min(l, o, c), c };
    }).filter(Boolean) : [];

    const reqCount = Number(count) > 0 ? Math.max(1, Number(count) | 0) : (tail.length || 1);
    const hasExplicitDirection = direction !== undefined && direction !== null && String(direction).trim() !== '';

    if (!tail.length && !hasExplicitDirection && !(Number(count) > 0)) return;

    // For single-candle expiry (standard 1-candle horizon) or missing OHLC, render crisp full-body candle
    // so doji / newly-opened ticks never collapse to a hairline or upside-down cross.
    if (reqCount <= 1 || tail.length < 2) {
        const refCandle = tail.length ? tail[tail.length - 1] : null;
        const cls = resolveExpiryCandleClass(direction, refCandle);
        const norm = hasExplicitDirection ? normalizeDirection(direction) : (refCandle && refCandle.c < refCandle.o ? 'PUT' : 'BUY');
        const bodyTop = norm === 'PUT' ? 6 : norm === 'NEUTRAL' ? 9 : 10;
        const bodyH = 24;
        const wickStyle = 'top:2.0px;height:35.0px';
        const bodyStyle = `top:${bodyTop.toFixed(1)}px;height:${bodyH.toFixed(1)}px`;
        container.innerHTML = `<div class='exp-candle forming'><div class='exp-wick ${cls}' style='${wickStyle}'></div><div class='exp-body ${cls}' style='${bodyStyle}'></div></div>`;
        return;
    }

    const sliceTail = tail.slice(-Math.min(reqCount, tail.length));
    const H = 40;
    const lo = Math.min(...sliceTail.map(k => k.l));
    const hi = Math.max(...sliceTail.map(k => k.h));
    const span = hi - lo;
    const y = (p) => span < 1e-12 ? H / 2 : 2 + ((hi - p) / span) * (H - 4);
    container.innerHTML = sliceTail.map((k, i) => {
        const yH = y(k.h), yL = y(k.l);
        const yO = y(k.o), yC = y(k.c);
        const rawBodyH = Math.abs(yC - yO);
        const bodyH = Math.max(5, rawBodyH);
        const midBody = (yO + yC) / 2;
        const bodyTop = Math.max(2, Math.min(H - 2 - bodyH, midBody - bodyH / 2));
        const forming = i === sliceTail.length - 1 ? ' forming' : '';
        const wickTop = Math.min(yH, bodyTop);
        const wickBot = Math.max(yL, bodyTop + bodyH);
        const wickStyle = `top:${wickTop.toFixed(1)}px;height:${Math.max(12, wickBot - wickTop).toFixed(1)}px`;
        const bodyStyle = `top:${bodyTop.toFixed(1)}px;height:${bodyH.toFixed(1)}px`;
        const cls = resolveExpiryCandleClass(direction, k);
        return `<div class='exp-candle${forming}'><div class='exp-wick ${cls}' style='${wickStyle}'></div><div class='exp-body ${cls}' style='${bodyStyle}'></div></div>`;
    }).join('');
}

export function renderDirSvg(direction) {
    const chart = document.getElementById('dirChart');
    if (!chart) return;
    const norm = normalizeDirection(direction);
    if (norm === 'BUY') {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 35 L30 25 L45 30 L70 5' stroke='#00e676' stroke-width='3' fill='none' stroke-linecap='round' stroke-linejoin='round'/><circle cx='70' cy='5' r='3.5' fill='#00e676'/></svg>`;
    } else if (norm === 'PUT') {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 5 L30 15 L45 10 L70 35' stroke='#ff1744' stroke-width='3' fill='none' stroke-linecap='round' stroke-linejoin='round'/><circle cx='70' cy='35' r='3.5' fill='#ff1744'/></svg>`;
    } else {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 20 L70 20' stroke='var(--dim)' stroke-width='2.5' stroke-dasharray='4 4' fill='none' stroke-linecap='round' opacity='0.5'/><circle cx='40' cy='20' r='3.5' fill='var(--dim)'/></svg>`;
    }
}

export function renderSparklinePrediction(containerId, normalizedPrices, direction, projFrac) {
    const container = document.getElementById(containerId);
    if (!container || !Array.isArray(normalizedPrices)) return;
    const width = 100;
    const height = 40;
    const count = normalizedPrices.length;
    if (count === 0) return;
    
    const denom = Math.max(1, count - 1);
    const points = normalizedPrices.map((v, i) => {
        const safeV = Number.isFinite(Number(v)) ? Number(v) : 0.5;
        const x = ((i / denom) * (width * 0.75)).toFixed(1);
        const y = (height - (safeV * height * 0.8 + height * 0.1)).toFixed(1);
        return `${x},${y}`;
    });
    
    const pathD = count === 1
        ? `M 0,${points[0].split(',')[1]} L ${width * 0.75},${points[0].split(',')[1]}`
        : `M ${points.join(' L ')}`;
    const lastX = width * 0.75;
    const lastVal = Number.isFinite(Number(normalizedPrices[count - 1])) ? Number(normalizedPrices[count - 1]) : 0.5;
    const lastY = height - (lastVal * height * 0.8 + height * 0.1);
    
    const predX = width - 2;
    let predY = lastY;
    let predColor = 'var(--dim)';
    const hasProj = Number.isFinite(projFrac) && projFrac > 0;
    const dashLen = hasProj ? Math.min(height - 8, Math.max(3, projFrac * height)) : null;
    const normDir = normalizeDirection(direction);
    if (normDir === 'BUY') {
        predY = dashLen != null ? Math.max(4, lastY - dashLen) : 5;
        predColor = '#10b981';
    } else if (normDir === 'PUT') {
        predY = dashLen != null ? Math.min(height - 4, lastY + dashLen) : 35;
        predColor = '#ef4444';
    }
    
    container.innerHTML = `
        <svg viewBox="0 0 ${width} ${height}" style="width:100%; height:100%; overflow:visible;">
            <path d="${pathD}" stroke="#8b5cf6" stroke-width="2.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
            <line x1="${lastX}" y1="${lastY}" x2="${predX}" y2="${predY}" stroke="${predColor}" stroke-width="2.5" stroke-dasharray="3,3" stroke-linecap="round"/>
            <circle cx="${lastX}" cy="${lastY}" r="3.5" fill="#8b5cf6" />
            <circle cx="${predX}" cy="${predY}" r="3" fill="${predColor}" />
        </svg>
    `;
}

const sbStatuses = ['ЗАГРУЗКА ДАННЫХ', 'ПОЛУЧЕНИЕ ЦЕНЫ', 'АНАЛИЗ РЫНКА'];
let sbTimer = null, sbFadeTimer = null, sbIdx = 0;

export function startStatusBar() {
    const sb = document.getElementById('statusBar');
    if (!sb) return;
    stopStatusBar();
    sb.classList.add('show');
    const title = document.getElementById('sbTitle');
    const sub = document.getElementById('sbSub');
    if (title) title.innerHTML = 'АНАЛИЗИРУЮ РЫНОК<span class=\'blink\'>.</span>';
    if (sub) { sub.textContent = sbStatuses[0]; sub.className = 'sb-sub'; }
    sbIdx = 0;

    sbTimer = setInterval(() => {
        if (document.hidden) return;
        const titleEl = document.getElementById('sbTitle');
        if (titleEl) {
            const m = titleEl.textContent.match(/\.+$/);
            const dots = m ? m[0].length : 0;
            titleEl.innerHTML = 'АНАЛИЗИРУЮ РЫНОК<span class=\'blink\'>' + '.'.repeat((dots % 3) + 1) + '</span>';
        }
        sbIdx = (sbIdx + 1) % sbStatuses.length;
        const subEl = document.getElementById('sbSub');
        if (subEl) {
            subEl.classList.add('fade');
            if (sbFadeTimer) clearTimeout(sbFadeTimer);
            sbFadeTimer = setTimeout(() => { 
                subEl.textContent = sbStatuses[sbIdx]; 
                subEl.classList.remove('fade'); 
            }, 200);
        }
    }, 900);
}

export function stopStatusBar() {
    const sb = document.getElementById('statusBar');
    if (sb) sb.classList.remove('show');
    if (sbTimer) { clearInterval(sbTimer); sbTimer = null; }
    if (sbFadeTimer) { clearTimeout(sbFadeTimer); sbFadeTimer = null; }
}

export function pricesToBars(prices, count) {
    if (!prices || !prices.length) return [];
    const tail = prices.slice(-count);
    const min = Math.min.apply(null, tail);
    const max = Math.max.apply(null, tail);
    const span = max - min;
    if (span < 1e-12) return tail.map(() => 0.5);
    return tail.map(p => 0.05 + 0.9 * (p - min) / span);
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

export function renderError(rawError, debugText) {
    const errDisp = document.getElementById('errorDisplay');
    if (!errDisp) return;
    let title = '⚠️ Ошибка';
    let desc = 'Произошла непредвиденная ошибка при обработке запроса.';
    if (rawError) {
        const errLower = String(rawError).toLowerCase();
        
        if (errLower.includes('circuit_breaker') || errLower.includes('предохранитель') || errLower.includes('приостановлен')) {
            title = '⚠️ ТОРГИ ПРИОСТАНОВЛЕНЫ';
            desc = String(rawError).replace(/^circuit_breaker:\s*/i, '') || 
                   'Сработал защитный предохранитель депозита (Circuit Breaker). Пожалуйста, подождите или смените актив.';
        } else if (errLower.includes('too many requests') || errLower.includes('rate limit') || errLower.includes('429')) {
            title = '⚠️ Превышен лимит запросов';
            const match = String(rawError).match(/(\d+)s/);
            const sec = match ? ` на ${match[1]} сек.` : '';
            desc = `Слишком частые клики. Пожалуйста, подождите${sec} перед следующим сканированием.`;
        } else if (errLower.includes('signature') || errLower.includes('initdata') || errLower.includes('unauthorized') || errLower.includes('401') || errLower.includes('403') || errLower.includes('сесси')) {
            title = '⚠️ Ошибка авторизации';
            desc = 'Сессия авторизации устарела. Нажмите кнопку ниже для бесшовного обновления сессии без выхода из приложения.';
        } else if (errLower.includes('access denied') || errLower.includes('deposit required')) {
            title = '⚠️ Доступ ограничен';
            desc = 'Для использования бота необходима регистрация на Pocket Option и внесение депозита.';
        } else if (errLower.includes('502') || errLower.includes('503') || errLower.includes('504') || errLower.includes('bad gateway') || errLower.includes('service unavailable') || errLower.includes('недоступен')) {
            title = '⚠️ Сервер перезагружается';
            desc = 'Сервер ValutaBot на Railway обновляется или временно недоступен. Попробуйте через 10–15 секунд.';
        } else if (errLower.includes('500') || errLower.includes('internal server error') || errLower.includes('пустой ответ')) {
            title = '⚠️ Внутренняя ошибка сервера (500)';
            desc = 'На сервере произошел сбой при расчете сигнала. Повторите попытку через несколько секунд или смените актив.';
        } else if (errLower.includes('run out of api credits') || errLower.includes('api credits')) {
            title = '⚠️ Лимит запросов исчерпан';
            desc = 'Превышен лимит запросов к внешнему потоку данных. Пожалуйста, повторите попытку чуть позже.';
        } else if (errLower.includes('asset and timeframe')) {
            title = '⚠️ Неверные параметры';
            desc = 'Необходимо выбрать валютную пару и таймфрейм.';
        } else if (errLower.includes('pocketid')) {
            title = '⚠️ Ошибка профиля';
            desc = 'Не указан Pocket Option ID.';
        } else if (errLower.includes('fetch') || errLower.includes('network') || errLower.includes('failed') || errLower.includes('connect') || errLower.includes('14с') || errLower.includes('лимит времени') || errLower.includes('сетев')) {
            title = '⚠️ Ошибка соединения';
            desc = 'Не удалось получить ответ от сервера. Проверьте интернет-соединение (4G/Wi-Fi).';
        } else {
            title = '⚠️ Сбой операции';
            desc = String(rawError)
                .replace(/failed/gi, 'ошибка')
                .replace(/error/gi, 'сбой')
                .replace(/internal server error/gi, 'Внутренняя ошибка сервера');
        }
    }
    const isAuth = rawError && (String(rawError).toLowerCase().includes('401') || String(rawError).toLowerCase().includes('unauthorized') || String(rawError).toLowerCase().includes('сесси') || String(rawError).toLowerCase().includes('signature') || String(rawError).toLowerCase().includes('initdata'));
    const actionBtnHtml = isAuth 
        ? `<div style="margin-top:10px;"><button class="error-action-btn" id="btnRefreshSession">🔄 Обновить сессию</button></div>`
        : '';
    errDisp.innerHTML = `
        <div class="error-header">${escapeHtml(title)}</div>
        <div class="error-desc">${escapeHtml(desc)}</div>
        ${actionBtnHtml}
        <div class="error-debug-toggle" id="errorDebugToggle">▸ Детали отладки</div>
        <div class="error-debug-content" id="errorDebugContent" style="display: none;">${escapeHtml(debugText)}</div>
    `;
    errDisp.style.display = 'block';
    if (isAuth) {
        const refreshBtn = document.getElementById('btnRefreshSession');
        if (refreshBtn) {
            refreshBtn.onclick = () => {
                hapticImpact('medium');
                refreshBtn.disabled = true;
                refreshBtn.innerText = 'Обновление...';
                if (typeof window !== 'undefined') {
                    try {
                        const url = new URL(window.location.href);
                        ['custom_user_id', 'custom_user_sign', 'auth_date', 'userId', 'userSign'].forEach(k => url.searchParams.delete(k));
                        window.history.replaceState({}, document.title, url.toString());
                    } catch (_) {}
                    window.location.reload();
                }
            };
        }
    }
    const debugToggleBtn = document.getElementById('errorDebugToggle');
    if (debugToggleBtn) {
        debugToggleBtn.onclick = () => {
            const content = document.getElementById('errorDebugContent');
            if (!content) return;
            const isHidden = content.style.display === 'none';
            content.style.display = isHidden ? 'block' : 'none';
            debugToggleBtn.textContent = isHidden ? '▾ Скрыть детали' : '▸ Детали отладки';
        };
    }
}

let lastTrafficStatus = null;
export function updateTrafficLight(status) {
    if (status === lastTrafficStatus) return;
    lastTrafficStatus = status;
    if (!cachedTrafficLightEl || !cachedTrafficLightEl.isConnected) cachedTrafficLightEl = document.getElementById('trafficLight');
    if (!cachedTrafficLightEl) return;
    if (status === 'action') {
        cachedTrafficLightEl.style.background = '#10b981';
        cachedTrafficLightEl.style.boxShadow = '0 0 10px #10b981';
        hapticImpact('light');
    } else if (status === 'prepare') {
        cachedTrafficLightEl.style.background = '#f59e0b';
        cachedTrafficLightEl.style.boxShadow = '0 0 10px #f59e0b';
    } else {
        cachedTrafficLightEl.style.background = '#ef4444';
        cachedTrafficLightEl.style.boxShadow = '0 0 10px #ef4444';
    }
}

export function showNetworkBanner(text) {
    const banner = document.getElementById('networkAlert');
    const txt = document.getElementById('networkAlertText');
    if (banner && txt) {
        txt.innerText = text;
        banner.style.display = 'flex';
    }
}

export function hideNetworkBanner() {
    const banner = document.getElementById('networkAlert');
    if (banner) {
        banner.style.display = 'none';
    }
}

// --- AI Animated Chart (Cached Gradient + Immediate rAF Stop + Resize rAF Guard) ---
let aiChartAnimationId = null;
let aiChartData = [];
let aiChartPhase = 0;
let hideChartTimer = null;
let cachedCanvasW = 0;
let cachedCanvasH = 0;
let cachedFloorGrad = null;
let resizeRafId = null;
let lastChartFrameTime = 0;

let isContextLostListenersAttached = false;
export function initCanvasContextLossHandlers() {
    if (isContextLostListenersAttached || typeof document === 'undefined') return;
    const canvas = document.getElementById('aiChartCanvas');
    if (!canvas || typeof canvas.addEventListener !== 'function') return;
    isContextLostListenersAttached = true;
    canvas.addEventListener('contextlost', (e) => {
        if (typeof e.preventDefault === 'function') e.preventDefault();
        if (aiChartAnimationId) {
            cancelAnimationFrame(aiChartAnimationId);
            aiChartAnimationId = null;
        }
        cachedFloorGrad = null;
    });
    canvas.addEventListener('contextrestored', () => {
        cachedFloorGrad = null;
        updateCanvasDimensions();
        const container = document.getElementById('aiChartContainer');
        if (container && container.classList.contains('active')) {
            if (aiChartData.length > 0) {
                lastChartFrameTime = 0;
                renderAiChartLoop();
            } else {
                renderAiChartStatic();
            }
        }
    });
}

export function updateCanvasDimensions() {
    const canvas = document.getElementById('aiChartCanvas');
    if (!canvas || !canvas.parentElement) return;
    const rect = canvas.parentElement.getBoundingClientRect();
    // Поддержка 3x дисплеев (iPhone Retina / Samsung Ultra) с ограничением до 3
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    const displayW = Math.round(rect.width);
    const displayH = Math.round(rect.height);
    cachedCanvasW = displayW;
    cachedCanvasH = displayH;
    cachedFloorGrad = null;
    if (displayW > 0 && displayH > 0) {
        const targetW = Math.round(displayW * dpr);
        const targetH = Math.round(displayH * dpr);
        let sizeChanged = false;
        if (canvas.width !== targetW || canvas.height !== targetH) {
            canvas.width = targetW;
            canvas.height = targetH;
            sizeChanged = true;
        }
        canvas.style.width = `${displayW}px`;
        canvas.style.height = `${displayH}px`;
        const ctx = canvas.getContext ? canvas.getContext('2d') : null;
        if (ctx && sizeChanged) {
            ctx.clearRect(0, 0, canvas.width, canvas.height);
        }
        const container = document.getElementById('aiChartContainer');
        if (container && container.classList.contains('active')) {
            if (aiChartData.length === 0 || !aiChartAnimationId) {
                renderAiChartStatic();
            }
        }
    }
}

export function showAiChart(asset, tf) {
    initCanvasContextLossHandlers();
    if (hideChartTimer) {
        clearTimeout(hideChartTimer);
        hideChartTimer = null;
    }
    const container = document.getElementById('aiChartContainer');
    if (container) {
        container.style.display = 'block';
        void container.offsetWidth;
        container.classList.add('active');
        
        aiChartData = [];
        updateCanvasDimensions();
        // Рендерим статический начальный кадр без холостого запуска rAF
        renderAiChartStatic();
    }
}

export function updateAiChartData(ohlcArray) {
    if (!Array.isArray(ohlcArray)) return;
    aiChartData = ohlcArray.map(c => {
        if (!c || typeof c !== 'object') return null;
        const o = Number(c.open ?? c.Open ?? c.o);
        const h = Number(c.high ?? c.High ?? c.h);
        const l = Number(c.low ?? c.Low ?? c.l);
        const cl = Number(c.close ?? c.Close ?? c.c);
        if (!Number.isFinite(o) || !Number.isFinite(h) || !Number.isFinite(l) || !Number.isFinite(cl) || l <= 0) {
            return null;
        }
        return {
            open: o,
            high: Math.max(h, o, cl),
            low: Math.min(l, o, cl),
            close: cl
        };
    }).filter(Boolean).slice(-30);

    // Запускаем цикл анимации только при наличии реальных свечных данных
    if (aiChartData.length > 0 && !aiChartAnimationId) {
        const container = document.getElementById('aiChartContainer');
        if (container && container.classList.contains('active')) {
            lastChartFrameTime = 0;
            renderAiChartLoop();
        }
    }
}

export function hideAiChart() {
    const container = document.getElementById('aiChartContainer');
    if (!container) return;
    container.classList.remove('active');
    // Stop rAF immediately so main thread is free for result cards & flash animation
    if (aiChartAnimationId) {
        cancelAnimationFrame(aiChartAnimationId);
        aiChartAnimationId = null;
    }
    if (resizeRafId) {
        cancelAnimationFrame(resizeRafId);
        resizeRafId = null;
    }
    cachedFloorGrad = null;
    lastChartFrameTime = 0;
    const canvas = document.getElementById('aiChartCanvas');
    if (canvas) {
        const ctx = canvas.getContext ? canvas.getContext('2d') : null;
        if (ctx && (!ctx.isContextLost || !ctx.isContextLost())) {
            ctx.clearRect(0, 0, canvas.width, canvas.height);
        }
    }
    if (hideChartTimer) clearTimeout(hideChartTimer);
    hideChartTimer = setTimeout(() => {
        container.style.display = 'none';
        hideChartTimer = null;
    }, 350);
}

function _onChartVisibilityChange() {
    const c = document.getElementById('aiChartContainer');
    if (document.hidden) {
        if (aiChartAnimationId) {
            cancelAnimationFrame(aiChartAnimationId);
            aiChartAnimationId = null;
        }
    } else {
        if (!aiChartAnimationId && c && c.classList.contains('active')) {
            updateCanvasDimensions();
            if (aiChartData.length > 0) {
                lastChartFrameTime = 0;
                renderAiChartLoop();
            } else {
                renderAiChartStatic();
            }
        }
    }
}

if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', _onChartVisibilityChange);
    document.addEventListener('resume', _onChartVisibilityChange);
    document.addEventListener('freeze', () => {
        if (aiChartAnimationId) {
            cancelAnimationFrame(aiChartAnimationId);
            aiChartAnimationId = null;
        }
    });
    if (typeof window !== 'undefined') {
        window.addEventListener('pageshow', (e) => {
            if (e.persisted) _onChartVisibilityChange();
        });
    }
}

function renderAiChartStatic() {
    const canvas = document.getElementById('aiChartCanvas');
    if (!canvas) return;
    if (cachedCanvasW === 0 || cachedCanvasH === 0) {
        updateCanvasDimensions();
        if (cachedCanvasW === 0 || cachedCanvasH === 0) return;
    }
    const ctx = canvas.getContext ? canvas.getContext('2d') : null;
    if (!ctx || (ctx.isContextLost && ctx.isContextLost())) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.save();
    ctx.scale(dpr, dpr);
    const w = cachedCanvasW;
    const h = cachedCanvasH;
    ctx.fillStyle = '#0b0d1f';
    ctx.fillRect(0, 0, w, h);
    if (!cachedFloorGrad && h > 0) {
        cachedFloorGrad = ctx.createLinearGradient(0, h * 0.6, 0, h);
        cachedFloorGrad.addColorStop(0, 'rgba(184, 41, 255, 0)');
        cachedFloorGrad.addColorStop(1, 'rgba(184, 41, 255, 0.15)');
    }
    if (cachedFloorGrad) {
        ctx.fillStyle = cachedFloorGrad;
        ctx.fillRect(0, h * 0.6, w, h * 0.4);
    }
    ctx.restore();
}

function renderAiChartLoop(timestamp = 0) {
    if (document.hidden) {
        aiChartAnimationId = null;
        return;
    }
    const container = document.getElementById('aiChartContainer');
    if (!container || !container.classList.contains('active')) {
        aiChartAnimationId = null;
        return;
    }
    const canvas = document.getElementById('aiChartCanvas');
    if (!canvas) {
        aiChartAnimationId = null;
        return;
    }
    if (cachedCanvasW === 0 || cachedCanvasH === 0) {
        updateCanvasDimensions();
        if (cachedCanvasW === 0 || cachedCanvasH === 0) {
            aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
            return;
        }
    }

    // Stop animation loop if no candle data available (saves battery)
    if (aiChartData.length === 0) {
        renderAiChartStatic();
        aiChartAnimationId = null;
        return;
    }

    // ProMotion 120Hz frame throttle: cap at ~60 FPS (min 16ms between frames)
    if (timestamp && lastChartFrameTime && (timestamp - lastChartFrameTime < 16)) {
        aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
        return;
    }
    const deltaMs = lastChartFrameTime ? (timestamp - lastChartFrameTime) : 16.6;
    lastChartFrameTime = timestamp || performance.now();

    const ctx = canvas.getContext ? canvas.getContext('2d') : null;
    if (!ctx || (ctx.isContextLost && ctx.isContextLost())) {
        aiChartAnimationId = null;
        return;
    }
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    
    // Hardware frame buffer reset
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    
    ctx.save();
    ctx.scale(dpr, dpr);
    
    const w = cachedCanvasW;
    const h = cachedCanvasH;
    
    ctx.fillStyle = '#0b0d1f';
    ctx.fillRect(0, 0, w, h);
    
    if (!cachedFloorGrad && h > 0) {
        cachedFloorGrad = ctx.createLinearGradient(0, h * 0.6, 0, h);
        cachedFloorGrad.addColorStop(0, 'rgba(184, 41, 255, 0)');
        cachedFloorGrad.addColorStop(1, 'rgba(184, 41, 255, 0.15)');
    }
    if (cachedFloorGrad) {
        ctx.fillStyle = cachedFloorGrad;
        ctx.fillRect(0, h * 0.6, w, h * 0.4);
    }
    
    // Delta-time normalized phase advancement (constant sway speed on 60Hz/120Hz)
    const dt = Math.min(deltaMs / 1000, 0.1);
    aiChartPhase += dt * 1.08;
    
    const paddingY = Math.floor(h * 0.25);
    const paddingX = 15;
    const count = aiChartData.length;
    const spacing = (w - paddingX * 2) / count;
    
    let candleWidth = Math.max(3, Math.floor(spacing * 0.5));
    if (candleWidth % 2 === 0) candleWidth += 1;
    
    let minP = Infinity, maxP = -Infinity;
    for (let i = 0; i < count; i++) {
        const c = aiChartData[i];
        const low = Number.isFinite(c.low) ? c.low : Number(c.low ?? c.Low ?? c.l);
        const high = Number.isFinite(c.high) ? c.high : Number(c.high ?? c.High ?? c.h);
        const open = Number.isFinite(c.open) ? c.open : Number(c.open ?? c.Open ?? c.o);
        const close = Number.isFinite(c.close) ? c.close : Number(c.close ?? c.Close ?? c.c);
        const trueLow = Math.min(low, open, close);
        const trueHigh = Math.max(high, open, close);
        if (Number.isFinite(trueLow) && trueLow < minP) minP = trueLow;
        if (Number.isFinite(trueHigh) && trueHigh > maxP) maxP = trueHigh;
    }
    if (!Number.isFinite(minP) || !Number.isFinite(maxP) || maxP <= minP) {
        minP = Number.isFinite(minP) && minP !== Infinity ? minP : 1.0;
        maxP = minP + 0.0001;
    }
    const range = maxP - minP || 0.0001;
    const scaleY = (h - paddingY * 2) / range;
    
    // Zero-allocation batch rendering: avoid creating arrays & objects per frame
    // Pass 1: Bullish (#00b3ff) wicks
    ctx.strokeStyle = '#00b3ff';
    ctx.fillStyle = '#00b3ff';
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let i = 0; i < count; i++) {
        const c = aiChartData[i];
        if (c.close < c.open) continue;
        const swayY = Math.sin(aiChartPhase * 1.5 + i * 0.6) * 0.3;
        const cx = Math.floor(paddingX + i * spacing + spacing / 2) + 0.5;
        const yHigh = paddingY + (maxP - c.high) * scaleY + swayY;
        const yLow  = paddingY + (maxP - c.low)  * scaleY + swayY;
        ctx.moveTo(cx, Math.floor(yHigh) + 0.5);
        ctx.lineTo(cx, Math.floor(yLow) + 0.5);
    }
    ctx.stroke();

    // Bullish bodies
    for (let i = 0; i < count; i++) {
        const c = aiChartData[i];
        if (c.close < c.open) continue;
        const swayY = Math.sin(aiChartPhase * 1.5 + i * 0.6) * 0.3;
        const cx = Math.floor(paddingX + i * spacing + spacing / 2) + 0.5;
        const x = Math.floor(cx - candleWidth / 2);
        const yTop = paddingY + (maxP - Math.max(c.open, c.close)) * scaleY + swayY;
        let bodyH = Math.abs(c.close - c.open) * scaleY;
        if (bodyH < 2) bodyH = 2;
        ctx.fillRect(x, Math.floor(yTop), candleWidth, Math.ceil(bodyH));
    }

    // Pass 2: Bearish (#9b5de5) wicks
    ctx.strokeStyle = '#9b5de5';
    ctx.fillStyle = '#9b5de5';
    ctx.beginPath();
    for (let i = 0; i < count; i++) {
        const c = aiChartData[i];
        if (c.close >= c.open) continue;
        const swayY = Math.sin(aiChartPhase * 1.5 + i * 0.6) * 0.3;
        const cx = Math.floor(paddingX + i * spacing + spacing / 2) + 0.5;
        const yHigh = paddingY + (maxP - c.high) * scaleY + swayY;
        const yLow  = paddingY + (maxP - c.low)  * scaleY + swayY;
        ctx.moveTo(cx, Math.floor(yHigh) + 0.5);
        ctx.lineTo(cx, Math.floor(yLow) + 0.5);
    }
    ctx.stroke();

    // Bearish bodies
    for (let i = 0; i < count; i++) {
        const c = aiChartData[i];
        if (c.close >= c.open) continue;
        const swayY = Math.sin(aiChartPhase * 1.5 + i * 0.6) * 0.3;
        const cx = Math.floor(paddingX + i * spacing + spacing / 2) + 0.5;
        const x = Math.floor(cx - candleWidth / 2);
        const yTop = paddingY + (maxP - Math.max(c.open, c.close)) * scaleY + swayY;
        let bodyH = Math.abs(c.close - c.open) * scaleY;
        if (bodyH < 2) bodyH = 2;
        ctx.fillRect(x, Math.floor(yTop), candleWidth, Math.ceil(bodyH));
    }

    ctx.restore();
    aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
}
// Экспорт для Telegram WebApp viewportChanged
/* updateCanvasDimensions is exported inline above */
// Обработка ориентации и визуального вьюпорта
if (typeof window !== 'undefined') {
    const onWindowOrViewportResize = () => {
        if (resizeRafId) return;
        resizeRafId = requestAnimationFrame(() => {
            resizeRafId = null;
            updateCanvasDimensions();
            const container = document.getElementById('aiChartContainer');
            if (container && container.classList.contains('active')) {
                if (aiChartData.length > 0) {
                    if (!aiChartAnimationId) renderAiChartLoop();
                } else {
                    renderAiChartStatic();
                }
            }
        });
    };
    window.addEventListener('resize', onWindowOrViewportResize, { passive: true });
    window.addEventListener('orientationchange', onWindowOrViewportResize, { passive: true });
    if (typeof screen !== 'undefined' && screen.orientation && typeof screen.orientation.addEventListener === 'function') {
        screen.orientation.addEventListener('change', onWindowOrViewportResize);
    }
    if (window.visualViewport) {
        window.visualViewport.addEventListener('resize', onWindowOrViewportResize, { passive: true });
    }
    if (window.matchMedia) {
        const mq = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
        if (mq && typeof mq.addEventListener === 'function') {
            mq.addEventListener('change', onWindowOrViewportResize);
        }
    }
}

if (typeof document !== 'undefined') {
    initCanvasContextLossHandlers();
}
