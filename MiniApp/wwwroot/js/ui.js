import { lastPriceVal } from './state.js?v=20261026_audit_v1';

let priceFlashTimeout = null;

export function updateLivePriceUI(price) {
    const valEl = document.getElementById('livePriceValue');
    if (!valEl) return;

    const isHighVal = price > 100;
    const formatted = price.toFixed(isHighVal ? 2 : 5);

    valEl.innerText = formatted;

    if (lastPriceVal > 0) {
        if (price > lastPriceVal) {
            valEl.className = 'live-price-value up';
        } else if (price < lastPriceVal) {
            valEl.className = 'live-price-value down';
        }
        
        if (priceFlashTimeout) clearTimeout(priceFlashTimeout);
        priceFlashTimeout = setTimeout(() => {
            if (valEl.innerText === formatted) {
                valEl.className = 'live-price-value';
            }
        }, 400);
    } else {
        valEl.className = 'live-price-value';
    }
}

export function switchResultTab(tabName) {
    const contentChart = document.getElementById('resultsGrid');
    const contentAI = document.getElementById('tabContentAI');

    if (contentChart) contentChart.style.display = 'grid';
    if (contentAI) contentAI.style.display = 'block';
}

export function clearResults() {
    const safeSetText = (id, txt) => { const el = document.getElementById(id); if (el) el.innerText = txt; };
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
    safeSetStyle('aiCopilotCard', 'display', 'none');
    safeSetStyle('consensusRadarCard', 'display', 'none');
    safeSetStyle('tabContentAI', 'display', 'none');
    safeSetStyle('mlEnsembleCard', 'display', 'none');
    safeSetStyle('confluenceCard', 'display', 'none');
    safeSetStyle('mcCard', 'display', 'none');
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
    const escaped = String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');

    return escaped
        .replace(/\*\*(.*?)\*\*/g, '<b>$1</b>')
        .replace(/\*(.*?)\*/g, '<i>$1</i>')
        .replace(/\n/g, '<br/>');
}

export function renderMiniChart(containerId, values, color) {
    const container = document.getElementById(containerId);
    if (!container) return;
    const max = Math.max(...values, 1);
    container.innerHTML = values.map(v => {
        const h = Math.max(4, (v / max) * 38);
        return `<div class='res-chart-bar ${color}' style='height:${h}px'></div>`;
    }).join('');
}

// Expiry candles: renders N real OHLC candlesticks (N = expiryCandles)
export function renderExpiryCandles(containerId, ohlc, count) {
    const container = document.getElementById(containerId);
    if (!container || !ohlc || !ohlc.length) return;
    const n = Math.max(1, Math.min(count || ohlc.length, ohlc.length));
    const tail = ohlc.slice(-n).map(k => ({
        o: Number(k.o), h: Number(k.h), l: Number(k.l), c: Number(k.c)
    })).filter(k => [k.o, k.h, k.l, k.c].every(Number.isFinite));
    if (!tail.length) return;

    const H = 40;
    const lo = Math.min(...tail.map(k => k.l));
    const hi = Math.max(...tail.map(k => k.h));
    const span = hi - lo;
    const y = (p) => span < 1e-12 ? H / 2 : 2 + ((hi - p) / span) * (H - 4);

    container.innerHTML = tail.map((k, i) => {
        const yH = y(k.h), yL = y(k.l);
        const yO = y(k.o), yC = y(k.c);
        const bodyTop = Math.min(yO, yC);
        const bodyH = Math.max(2.5, Math.abs(yC - yO));
        const forming = i === tail.length - 1 ? ' forming' : '';
        const wickStyle = `top:${yH.toFixed(1)}px;height:${Math.max(1, yL - yH).toFixed(1)}px`;
        const bodyStyle = `top:${bodyTop.toFixed(1)}px;height:${bodyH.toFixed(1)}px`;
        const cls = 'exp-neutral';
        return `<div class='exp-candle${forming}'><div class='exp-wick ${cls}' style='${wickStyle}'></div><div class='exp-body ${cls}' style='${bodyStyle}'></div></div>`;
    }).join('');
}

export function renderDirSvg(direction) {
    const chart = document.getElementById('dirChart');
    if (!chart) return;
    if (direction === 'BUY') {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 35 L30 25 L45 30 L70 5' stroke='#00e676' stroke-width='3' fill='none' stroke-linecap='round' stroke-linejoin='round'/><circle cx='70' cy='5' r='3.5' fill='#00e676'/></svg>`;
    } else if (direction === 'PUT') {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 5 L30 15 L45 10 L70 35' stroke='#ff1744' stroke-width='3' fill='none' stroke-linecap='round' stroke-linejoin='round'/><circle cx='70' cy='35' r='3.5' fill='#ff1744'/></svg>`;
    } else {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 20 L70 20' stroke='var(--dim)' stroke-width='2.5' stroke-dasharray='4 4' fill='none' stroke-linecap='round' opacity='0.5'/><circle cx='40' cy='20' r='3.5' fill='var(--dim)'/></svg>`;
    }
}

export function renderSparklinePrediction(containerId, normalizedPrices, direction, projFrac) {
    const container = document.getElementById(containerId);
    if (!container) return;

    const width = 100;
    const height = 40;
    const count = normalizedPrices.length;
    if (count === 0) return;
    
    // History 75% of width
    const points = normalizedPrices.map((v, i) => {
        const x = (i / (count - 1)) * (width * 0.75); 
        const y = height - (v * height * 0.8 + height * 0.1); 
        return `${x},${y}`;
    });
    
    const pathD = `M ${points.join(' L ')}`;
    const lastX = width * 0.75;
    const lastY = height - (normalizedPrices[count - 1] * height * 0.8 + height * 0.1);
    
    const predX = width - 2;
    let predY = lastY;
    let predColor = 'var(--dim)';

    const hasProj = Number.isFinite(projFrac) && projFrac > 0;
    const dashLen = hasProj ? Math.min(height - 8, Math.max(3, projFrac * height)) : null;

    if (direction === 'BUY') {
        predY = dashLen != null ? Math.max(4, lastY - dashLen) : 5;
        predColor = '#10b981';
    } else if (direction === 'PUT') {
        predY = dashLen != null ? Math.min(height - 4, lastY + dashLen) : 35;
        predColor = '#ef4444';
    }
    
    const svgHtml = `
        <svg viewBox="0 0 ${width} ${height}" style="width:100%; height:100%; overflow:visible;">
            <path d="${pathD}" stroke="#8b5cf6" stroke-width="2.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
            <line x1="${lastX}" y1="${lastY}" x2="${predX}" y2="${predY}" stroke="${predColor}" stroke-width="2.5" stroke-dasharray="3,3" stroke-linecap="round"/>
            <circle cx="${lastX}" cy="${lastY}" r="3.5" fill="#8b5cf6" />
            <circle cx="${predX}" cy="${predY}" r="3" fill="${predColor}" />
        </svg>
    `;
    container.innerHTML = svgHtml;
}

const sbStatuses = ['ЗАГРУЗКА ДАННЫХ', 'ПОЛУЧЕНИЕ ЦЕНЫ', 'АНАЛИЗ РЫНКА'];
let sbTimer = null, sbFadeTimer = null, sbIdx = 0;

export function startStatusBar() {
    const sb = document.getElementById('statusBar');
    if (!sb) return;
    sb.classList.add('show');
    const title = document.getElementById('sbTitle');
    const sub = document.getElementById('sbSub');
    if (title) title.innerHTML = 'АНАЛИЗИРУЮ РЫНОК<span class=\'blink\'>.</span>';
    if (sub) { sub.textContent = sbStatuses[0]; sub.className = 'sb-sub'; }
    sbIdx = 0;

    stopStatusBar();
    sbTimer = setInterval(() => {
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

export function renderError(rawError, debugText) {
    const errDisp = document.getElementById('errorDisplay');
    if (!errDisp) return;

    let title = '⚠️ Ошибка';
    let desc = 'Произошла непредвиденная ошибка при обработке запроса.';

    if (rawError) {
        const errLower = String(rawError).toLowerCase();
        
        if (errLower.includes('circuit_breaker') || errLower.includes('предохранитель')) {
            title = '⚠️ ТОРГИ ПРИОСТАНОВЛЕНЫ';
            desc = 'Сработал защитный предохранитель депозита (Circuit Breaker). Пожалуйста, подождите или смените актив.';
        } else if (errLower.includes('502') || errLower.includes('503') || errLower.includes('bad gateway') || errLower.includes('unexpected token') || errLower.includes('сервер вернул статус')) {
            title = '⚠️ Сервер перезагружается';
            desc = 'Сервер ValutaBot на Railway обновляется или временно недоступен. Попробуйте через 10-15 секунд.';
        } else if (errLower.includes('run out of api credits') || errLower.includes('api credits')) {
            title = '⚠️ Лимит запросов исчерпан';
            desc = 'Превышен лимит запросов к внешнему потоку данных. Пожалуйста, повторите попытку чуть позже.';
        } else if (errLower.includes('too many requests') || errLower.includes('rate limit') || errLower.includes('429')) {
            title = '⚠️ Превышен лимит запросов';
            const match = String(rawError).match(/(\d+)s/);
            const sec = match ? ` на ${match[1]} сек.` : '';
            desc = `Слишком частые клики. Пожалуйста, подождите${sec} перед следующим сканированием.`;
        } else if (errLower.includes('access denied') || errLower.includes('deposit required')) {
            title = '⚠️ Доступ ограничен';
            desc = 'Для использования бота необходима регистрация на Pocket Option и внесение депозита.';
        } else if (errLower.includes('signature') || errLower.includes('initdata') || errLower.includes('unauthorized') || errLower.includes('401')) {
            title = '⚠️ Ошибка авторизации';
            desc = 'Пожалуйста, перезапустите бота через Telegram, чтобы обновить сессию.';
        } else if (errLower.includes('asset and timeframe')) {
            title = '⚠️ Неверные параметры';
            desc = 'Необходимо выбрать валютную пару и таймфрейм.';
        } else if (errLower.includes('pocketid')) {
            title = '⚠️ Ошибка профиля';
            desc = 'Не указан Pocket Option ID.';
        } else if (errLower.includes('fetch') || errLower.includes('network') || errLower.includes('failed') || errLower.includes('connect')) {
            title = '⚠️ Ошибка соединения';
            desc = 'Не удалось подключиться к серверу. Пожалуйста, проверьте интернет-соединение.';
        } else {
            title = '⚠️ Сбой операции';
            desc = String(rawError)
                .replace(/failed/gi, 'ошибка')
                .replace(/error/gi, 'сбой')
                .replace(/internal server error/gi, 'Внутренняя ошибка сервера');
        }
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

    const safeTitle = escapeHtml(title);
    const safeDesc = escapeHtml(desc);
    const safeDebug = escapeHtml(debugText);

    errDisp.innerHTML = `
        <div class="error-header">${safeTitle}</div>
        <div class="error-desc">${safeDesc}</div>
        <div class="error-debug-toggle" id="errorDebugToggle">▸ Детали отладки</div>
        <div class="error-debug-content" id="errorDebugContent" style="display: none;">${safeDebug}</div>
    `;
    errDisp.style.display = 'block';

    const debugToggleBtn = document.getElementById('errorDebugToggle');
    if (debugToggleBtn) {
        debugToggleBtn.addEventListener('click', () => {
            const content = document.getElementById('errorDebugContent');
            if (!content) return;
            const isHidden = content.style.display === 'none';
            content.style.display = isHidden ? 'block' : 'none';
            debugToggleBtn.innerText = isHidden ? '▾ Скрыть детали' : '▸ Детали отладки';
        });
    }
}

export function updateTrafficLight(status) {
    const tl = document.getElementById('trafficLight');
    if (!tl) return;
    if (status === 'action') {
        tl.style.background = '#10b981';
        tl.style.boxShadow = '0 0 10px #10b981';
    } else if (status === 'prepare') {
        tl.style.background = '#f59e0b';
        tl.style.boxShadow = '0 0 10px #f59e0b';
    } else {
        tl.style.background = '#ef4444';
        tl.style.boxShadow = '0 0 10px #ef4444';
    }
}

// --- AI Animated Chart (Performance & Memory Safe) ---
let aiChartAnimationId = null;
let aiChartData = [];
let aiChartPhase = 0;
let hideChartTimer = null;

export function showAiChart(asset, tf) {
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
        if (!aiChartAnimationId) {
            renderAiChartLoop();
        }
    }
}

export function updateAiChartData(ohlcArray) {
    if (ohlcArray && ohlcArray.length) {
        aiChartData = ohlcArray.slice(-30);
    }
}

export function hideAiChart() {
    const container = document.getElementById('aiChartContainer');
    if (!container) return;
    container.classList.remove('active');
    
    if (hideChartTimer) clearTimeout(hideChartTimer);
    hideChartTimer = setTimeout(() => {
        container.style.display = 'none';
        if (aiChartAnimationId) {
            cancelAnimationFrame(aiChartAnimationId);
            aiChartAnimationId = null;
        }
        hideChartTimer = null;
    }, 400);
}

function renderAiChartLoop() {
    const canvas = document.getElementById('aiChartCanvas');
    if (!canvas) {
        aiChartAnimationId = null;
        return;
    }
    const ctx = canvas.getContext('2d');
    
    const rect = canvas.parentElement ? canvas.parentElement.getBoundingClientRect() : null;
    if (!rect || rect.width === 0) {
        aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
        return;
    }
    
    // Cap DPR at 2.0 to protect mobile battery and GPU rasterization
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const expectedW = Math.floor(rect.width * dpr);
    const expectedH = Math.floor(rect.height * dpr);
    if (canvas.width !== expectedW || canvas.height !== expectedH) {
        canvas.width = expectedW;
        canvas.height = expectedH;
    }
    
    ctx.save();
    ctx.scale(dpr, dpr);
    
    const w = rect.width;
    const h = rect.height;
    
    ctx.fillStyle = '#0b0d1f';
    ctx.fillRect(0, 0, w, h);
    
    // Deep Floor reflection
    const floorGrad = ctx.createLinearGradient(0, h * 0.6, 0, h);
    floorGrad.addColorStop(0, 'rgba(184, 41, 255, 0)');
    floorGrad.addColorStop(1, 'rgba(184, 41, 255, 0.15)');
    ctx.fillStyle = floorGrad;
    ctx.fillRect(0, h * 0.6, w, h * 0.4);
    
    aiChartPhase += 0.018;
    
    if (aiChartData.length > 0) {
        const paddingY = Math.floor(h * 0.25);
        const paddingX = 15;
        const count = aiChartData.length;
        const spacing = (w - paddingX * 2) / count;
        
        let candleWidth = Math.max(3, Math.floor(spacing * 0.5));
        if (candleWidth % 2 === 0) candleWidth += 1;
        
        let minP = Infinity, maxP = -Infinity;
        for (let i = 0; i < count; i++) {
            const c = aiChartData[i];
            const trueLow = Math.min(c.low, c.open, c.close);
            const trueHigh = Math.max(c.high, c.open, c.close);
            if (trueLow < minP) minP = trueLow;
            if (trueHigh > maxP) maxP = trueHigh;
        }
        const range = maxP - minP || 1;
        const scaleY = (h - paddingY * 2) / range;
        
        // Candles (drawn crisp and fast without heavy shadowBlur)
        for (let i = 0; i < count; i++) {
            const c = aiChartData[i];
            const isBull = c.close >= c.open;
            const swayY = Math.sin(aiChartPhase * 1.5 + i * 0.6) * 0.3;
            
            const cx = Math.floor(paddingX + i * spacing + spacing / 2) + 0.5;
            const x = Math.floor(cx - candleWidth / 2);
            
            const yHigh = paddingY + (maxP - c.high) * scaleY + swayY;
            const yLow  = paddingY + (maxP - c.low)  * scaleY + swayY;
            const yTop  = paddingY + (maxP - Math.max(c.open, c.close)) * scaleY + swayY;
            let bodyH = Math.abs(c.close - c.open) * scaleY;
            if (bodyH < 2) bodyH = 2;
            
            const color = isBull ? '#00b3ff' : '#9b5de5'; 
            
            // 1. Draw Wick
            ctx.strokeStyle = color;
            ctx.lineWidth = 1; 
            ctx.beginPath();
            ctx.moveTo(cx, Math.floor(yHigh) + 0.5);
            ctx.lineTo(cx, Math.floor(yLow) + 0.5);
            ctx.stroke();
            
            // 2. Draw Solid Body
            ctx.fillStyle = color;
            ctx.fillRect(Math.floor(x), Math.floor(yTop), candleWidth, Math.ceil(bodyH));
        }
    }

    ctx.restore();
    aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
}

// Session psychometrics tracker
let lastAnalysisTimestamp = 0;
let recentAnalysisCount = 0;
let analysisCountResetTimer = null;

export function getSessionPsychometrics() {
    const now = Date.now();
    let speedPenalty = 0;
    
    if (lastAnalysisTimestamp > 0) {
        const deltaSec = (now - lastAnalysisTimestamp) / 1000;
        if (deltaSec < 12) {
            speedPenalty += 25; // impulsive clicking / tilt risk
        } else if (deltaSec < 25) {
            speedPenalty += 10;
        }
    }
    
    recentAnalysisCount++;
    if (analysisCountResetTimer) clearTimeout(analysisCountResetTimer);
    analysisCountResetTimer = setTimeout(() => {
        recentAnalysisCount = 0;
    }, 60000);
    
    if (recentAnalysisCount > 5) {
        speedPenalty += 15; // overtrading
    }

    lastAnalysisTimestamp = now;
    return { speedPenalty };
}

export function updateAiCopilot(data, onActionCallback) {
    const card = document.getElementById('aiCopilotCard');
    if (!card) return;

    if (!data || !data.copilotAdvice) {
        card.style.display = 'none';
        return;
    }

    const advice = data.copilotAdvice;
    card.style.display = 'block';

    const psycho = getSessionPsychometrics();
    const baseScore = advice.qualityScore || 85;
    const focusScore = Math.max(30, Math.min(99, baseScore - psycho.speedPenalty));

    const focusDot = document.getElementById('copilotFocusDot');
    const focusText = document.getElementById('copilotFocusText');
    if (focusText) {
        focusText.innerText = `Фокус: ${focusScore}%`;
    }
    if (focusDot) {
        if (focusScore >= 80) {
            focusDot.style.background = '#10b981';
            focusDot.style.boxShadow = '0 0 8px #10b981';
        } else if (focusScore >= 60) {
            focusDot.style.background = '#f59e0b';
            focusDot.style.boxShadow = '0 0 8px #f59e0b';
        } else {
            focusDot.style.background = '#ef4444';
            focusDot.style.boxShadow = '0 0 8px #ef4444';
        }
    }

    card.classList.remove('state-golden', 'state-danger', 'state-caution');
    if (advice.status === 'GOLDEN') {
        card.classList.add('state-golden');
    } else if (advice.status === 'CIRCUIT_BREAKER' || advice.status === 'NOISE_GUARD' || advice.status === 'CONFLICT' || advice.status === 'NEWS_RISK' || psycho.speedPenalty >= 25) {
        card.classList.add('state-danger');
    } else if (advice.status === 'WAIT' || advice.status === 'CAUTION') {
        card.classList.add('state-caution');
    }

    const headlineEl = document.getElementById('copilotHeadline');
    if (headlineEl) {
        if (psycho.speedPenalty >= 25) {
            headlineEl.innerText = '⚠️ Внимание: Высокая частота входов (Тильт-контроль)';
        } else {
            headlineEl.innerText = advice.title || 'Рекомендация Ко-пилота';
        }
    }

    const msgEl = document.getElementById('copilotMessage');
    if (msgEl) {
        if (psycho.speedPenalty >= 25) {
            msgEl.innerText = `Ты делаешь частые запросы с интервалом менее 12 сек. Вход на эмоциях без паузы приводит к сливу в 78% случаев. ${advice.message || ''}`;
        } else {
            msgEl.innerText = advice.message || '';
        }
    }

    const actionWrap = document.getElementById('copilotActionWrap');
    const actionBtn = document.getElementById('copilotActionBtn');
    const actionLabel = document.getElementById('copilotActionLabel');

    if (actionWrap && actionBtn && (advice.recommendedAsset || advice.recommendedTf)) {
        actionWrap.style.display = 'block';
        const targetAsset = advice.recommendedAsset;
        const targetTf = advice.recommendedTf;
        let btnText = '🎯 ';
        if (targetAsset && targetTf) {
            btnText += `Переключить на ${targetAsset} (${targetTf.toUpperCase()})`;
        } else if (targetAsset) {
            btnText += `Переключить на ${targetAsset}`;
        } else if (targetTf) {
            btnText += `Переключить на ${targetTf.toUpperCase()}`;
        }

        if (actionLabel) actionLabel.innerText = btnText;

        actionBtn.onclick = () => {
            if (typeof onActionCallback === 'function') {
                onActionCallback({ asset: targetAsset, tf: targetTf });
            }
        };
    } else if (actionWrap) {
        actionWrap.style.display = 'none';
    }
}
