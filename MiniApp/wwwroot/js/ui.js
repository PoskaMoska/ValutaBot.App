
import { lastPriceVal } from './api.js';

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
    safeSetStyle('topCategories', 'display', 'flex');
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
