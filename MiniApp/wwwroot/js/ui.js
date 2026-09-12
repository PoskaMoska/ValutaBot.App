import { lastPriceVal } from './api.js?v=20260912_1';

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
        
        setTimeout(() => {
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
    safeSetStyle('tabContentAI', 'display', 'none');
    safeSetStyle('mlEnsembleCard', 'display', 'none');
    safeSetStyle('confluenceCard', 'display', 'none');
    safeSetStyle('mcCard', 'display', 'none');
    safeSetStyle('reasoningCard', 'display', 'none');
    safeSetStyle('newsCard', 'display', 'none');
    safeSetStyle('welcomeSec', 'display', 'flex');
    safeSetStyle('topCategories', 'display', 'none');
    document.querySelectorAll('.res-card').forEach(c => c.classList.remove('flash'));
}

export function flashResults() {
    document.querySelectorAll('.res-card').forEach(c => {
        c.classList.remove('flash');
        void c.offsetWidth;
        c.classList.add('flash');
    });
}

export function parseMd(text) {
    if (!text) return '';
    return text.replace(/\*\*(.*?)\*\*/g, '<b>$1</b>')
               .replace(/\*(.*?)\*/g, '<i>$1</i>')
               .replace(/\n/g, '<br/>');
}

export function renderMiniChart(containerId, values, color) {
    const container = document.getElementById(containerId);
    if(!container) return;
    const max = Math.max(...values, 1);
    container.innerHTML = values.map(v => {
        const h = Math.max(4, (v / max) * 38);
        return `<div class='res-chart-bar ${color}' style='height:${h}px'></div>`;
    }).join('');
}

// Expiry candles: renders N real OHLC candlesticks (N = expiryCandles),
// so the "Время" card literally shows the N candles the trade lives through.
// ohlc: [{o,h,l,c}...] chronological. Last candle = forming -> pulses.
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
    if(!chart) return;
    if(direction === 'BUY') {
        chart.innerHTML = `<svg viewBox='0 0 80 40'><path d='M10 35 L30 25 L45 30 L70 5' stroke='#00e676' stroke-width='3' fill='none' stroke-linecap='round' stroke-linejoin='round'/><circle cx='70' cy='5' r='3.5' fill='#00e676'/></svg>`;
    } else if(direction === 'PUT') {
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

    // Projection length: if projFrac (expected move as fraction of chart
    // height, from ATR*sqrt(expiry)/span) is given, the dashed segment shows
    // a statistically sized move. Otherwise legacy fixed-geometry dash.
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
let sbTimer = null, sbIdx = 0;

export function startStatusBar() {
    const sb = document.getElementById('statusBar');
    if (!sb) return;
    sb.classList.add('show');
    const title = document.getElementById('sbTitle');
    const sub = document.getElementById('sbSub');
    if (title) title.innerHTML = 'АНАЛИЗИРУЮ РЫНОК<span class=\'blink\'>.</span>';
    if (sub) { sub.textContent = sbStatuses[0]; sub.className = 'sb-sub'; }
    sbIdx = 0;

    if (sbTimer) clearInterval(sbTimer);
    sbTimer = setInterval(() => {
        const title = document.getElementById('sbTitle');
        if (title) {
            const m = title.textContent.match(/\.+$/);
            const dots = m ? m[0].length : 0;
            title.innerHTML = 'АНАЛИЗИРУЮ РЫНОК<span class=\'blink\'>' + '.'.repeat((dots % 3) + 1) + '</span>';
        }
        sbIdx = (sbIdx + 1) % sbStatuses.length;
        const sub = document.getElementById('sbSub');
        if (sub) {
            sub.classList.add('fade');
            setTimeout(() => { sub.textContent = sbStatuses[sbIdx]; sub.classList.remove('fade'); }, 200);
        }
    }, 900);
}

export function stopStatusBar() {
    const sb = document.getElementById('statusBar');
    if (sb) sb.classList.remove('show');
    if (sbTimer) { clearInterval(sbTimer); sbTimer = null; }
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
        const errLower = rawError.toLowerCase();
        
        if (errLower.includes('run out of api credits') || errLower.includes('api credits') || (errLower.includes('limit') && errLower.includes('twelvedata'))) {
            title = '⚠️ Лимит TwelveData исчерпан';
            desc = 'Превышен суточный лимит запросов к API TwelveData (800 шт). Пожалуйста, подождите обновления лимита (следующий день).';
        } else if (errLower.includes('too many requests') || errLower.includes('rate limit') || errLower.includes('429')) {
            title = '⚠️ Превышен лимит запросов';
            const match = rawError.match(/(\d+)s/);
            const sec = match ? ` на ${match[1]} сек.` : '';
            desc = `Слишком много запросов. Пожалуйста, подождите${sec} перед следующим сканированием.`;
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
        } else if (errLower.includes('api key') || errLower.includes('apikey')) {
            title = '⚠️ Сбой конфигурации';
            desc = 'На сервере не настроен API-ключ TwelveData.';
        } else if (errLower.includes('plan') || errLower.includes('subscription') || errLower.includes('tier')) {
            title = '⚠️ Ограничение тарифа';
            desc = 'Ваш тариф TwelveData не поддерживает этот актив или таймфрейм. Попробуйте выбрать другой инструмент.';
        } else if (errLower.includes('fetch') || errLower.includes('network') || errLower.includes('failed') || errLower.includes('connect')) {
            title = '⚠️ Ошибка соединения';
            desc = 'Не удалось подключиться к серверу. Пожалуйста, проверьте интернет-соединение.';
        } else {
            title = '⚠️ Сбой операции';
            desc = rawError;
            desc = desc.replace(/failed/gi, 'ошибка');
            desc = desc.replace(/error/gi, 'сбой');
            desc = desc.replace(/internal server error/gi, 'Внутренняя ошибка сервера');
        }
    }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
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


// --- AI Animated Chart ---
let aiChartAnimationId = null;
let aiChartData = [];
let aiChartPhase = 0;

export function showAiChart(asset, tf) {
    const container = document.getElementById('aiChartContainer');
    if (container) {
        container.style.display = 'block';
        // force reflow
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
    if (container) {
        container.classList.remove('active');
        setTimeout(() => {
            container.style.display = 'none';
            if (aiChartAnimationId) {
                cancelAnimationFrame(aiChartAnimationId);
                aiChartAnimationId = null;
            }
        }, 400);
    }
}

function renderAiChartLoop() {
    const canvas = document.getElementById('aiChartCanvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    
    const rect = canvas.parentElement.getBoundingClientRect();
    if(rect.width === 0) {
        aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
        return;
    }
    
    const dpr = window.devicePixelRatio || 1;
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
    
    aiChartPhase += 0.018;
    
    if (aiChartData.length > 0) {
        const paddingY = 25;
        const paddingX = 15;
        const count = aiChartData.length;
        const spacing = (w - paddingX * 2) / count;
        let candleWidth = Math.max(3, Math.floor(spacing * 0.55));
        if (candleWidth % 2 === 0) candleWidth += 1;
        
        let minP = Infinity, maxP = -Infinity;
        aiChartData.forEach(c => {
            if (c.low < minP) minP = c.low;
            if (c.high > maxP) maxP = c.high;
        });
        const range = maxP - minP || 1;
        const scaleY = (h - paddingY * 2) / range;
        
        // --- Build bottom-contour points from candles (one Y per candle) ---
        // Use the "low" of each candle as the bottom contour reference
        const contourPts = aiChartData.map((c, i) => ({
            x: paddingX + i * spacing + spacing / 2,
            y: paddingY + (maxP - c.low) * scaleY
        }));
        
        // Smoothed wave: for each pixel X, interpolate between neighboring contour points
        // then add gentle animated drift below the low
        const waveOffset = 15; // how far below the low to float the wave (px)
        const waveDrift = 3;  // reduce live oscillation amplitude
        
        // Build a smoothed Y array for the wave at pixel resolution
        const wavePoints = [];
        for (let px = 0; px <= w; px += 3) {
            // Find which candle segment we're in
            let segY = h - paddingY; // default to bottom if out of range
            if (contourPts.length >= 2) {
                // Binary-search adjacent candle pair
                let ci = 0;
                for (let k = 0; k < contourPts.length - 1; k++) {
                    if (px >= contourPts[k].x && px <= contourPts[k + 1].x) { ci = k; break; }
                    if (px > contourPts[contourPts.length - 1].x) ci = contourPts.length - 2;
                }
                const p0 = contourPts[Math.max(0, ci - 1)];
                const p1 = contourPts[ci];
                const p2 = contourPts[Math.min(contourPts.length - 1, ci + 1)];
                const p3 = contourPts[Math.min(contourPts.length - 1, ci + 2)];
                // Catmull-Rom interpolation for smooth curve
                const t = p1.x === p2.x ? 0 : (px - p1.x) / (p2.x - p1.x);
                const t2 = t * t, t3 = t2 * t;
                segY = 0.5 * ((2 * p1.y) +
                    (-p0.y + p2.y) * t +
                    (2*p0.y - 5*p1.y + 4*p2.y - p3.y) * t2 +
                    (-p0.y + 3*p1.y - 3*p2.y + p3.y) * t3);
            }
            // Shift below contour + gentle oscillation
            const wy = segY + waveOffset + Math.sin(px * 0.015 - aiChartPhase) * waveDrift;
            wavePoints.push({ x: px, y: wy });
        }
        
        // Draw the wave line only (no fill)
        ctx.beginPath();
        wavePoints.forEach((p, idx) => {
            if (idx === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
        });
        
        // Create a gradient so the line fades out at the left and right edges
        const lineGrad = ctx.createLinearGradient(0, 0, w, 0);
        lineGrad.addColorStop(0, 'rgba(139, 92, 246, 0)');
        lineGrad.addColorStop(0.1, 'rgba(139, 92, 246, 0.6)');
        lineGrad.addColorStop(0.9, 'rgba(139, 92, 246, 0.6)');
        lineGrad.addColorStop(1, 'rgba(139, 92, 246, 0)');
        
        ctx.strokeStyle = lineGrad;
        ctx.lineWidth = 1;
        ctx.lineJoin = 'round';
        ctx.shadowColor = 'rgba(139, 92, 246, 0.7)';
        ctx.shadowBlur = 3;
        ctx.stroke();
        ctx.shadowBlur = 0;
        
        // --- Candles (drawn on top of waves) ---
        aiChartData.forEach((c, i) => {
            const isBull = c.close >= c.open;
            // Very subtle float — barely perceptible
            const swayY = Math.sin(aiChartPhase * 1.5 + i * 0.6) * 0.3;
            
            const cx = Math.floor(paddingX + i * spacing + spacing / 2) + 0.5;
            const x = Math.floor(cx - candleWidth / 2);
            
            const yHigh = paddingY + (maxP - c.high) * scaleY + swayY;
            const yLow  = paddingY + (maxP - c.low)  * scaleY + swayY;
            const yTop  = paddingY + (maxP - Math.max(c.open, c.close)) * scaleY + swayY;
            let bodyH = Math.abs(c.close - c.open) * scaleY;
            if (bodyH < 3) bodyH = 3;
            
            const color = isBull ? '#00dcff' : '#dd00ff';
            const glowColor = isBull ? 'rgba(0,220,255,0.6)' : 'rgba(200,0,255,0.6)';
            
            // Wick — thin, sharp
            ctx.strokeStyle = color;
            ctx.lineWidth = 1;
            ctx.shadowColor = glowColor;
            ctx.shadowBlur = 4;
            ctx.beginPath();
            ctx.moveTo(cx, Math.floor(yHigh) + 0.5);
            ctx.lineTo(cx, Math.floor(yLow) + 0.5);
            ctx.stroke();
            ctx.shadowBlur = 0;
            
            // Body — solid fill, moderate glow
            ctx.fillStyle = color;
            ctx.shadowColor = glowColor;
            ctx.shadowBlur = 8;
            ctx.fillRect(Math.floor(x), Math.floor(yTop), candleWidth, Math.ceil(bodyH));
            ctx.shadowBlur = 0;
        });
    }
    
    ctx.restore();
    aiChartAnimationId = requestAnimationFrame(renderAiChartLoop);
}
