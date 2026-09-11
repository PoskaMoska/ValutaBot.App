// Live neon candlestick chart for the MiniApp analysis screen.
// Data: chartOhlc from /api/analyze (real OHLC), live ticks via /ws/prices.
// Standalone module: no imports from api.js/ui.js (avoids import cycles);
// api.js calls startLiveChart()/stopLiveChart().

const BULL = '#22d3ee';   // cyan (matches reference artwork)
const BEAR = '#f472b6';   // pink/magenta
const EMA_COLOR = '#8b5cf6';
const GRID = 'rgba(148,163,184,0.12)';
const TEXT_DIM = 'rgba(148,163,184,0.75)';

const MAX_CANDLES = 60;
const REDRAW_MS = 500;

let candles = [];
let ws = null;
let timer = null;
let runId = 0;
let canvasEl = null;
let lastTickAt = 0;

function num(v) {
    const n = Number(v);
    return Number.isFinite(n) ? n : NaN;
}

function sanitizeOhlc(ohlc) {
    if (!Array.isArray(ohlc)) return [];
    return ohlc
        .map(k => ({ o: num(k.o), h: num(k.h), l: num(k.l), c: num(k.c), v: num(k.v) }))
        .filter(k => [k.o, k.h, k.l, k.c].every(Number.isFinite))
        .map(k => ({ ...k, v: Number.isFinite(k.v) && k.v >= 0 ? k.v : 0 }))
        .slice(-MAX_CANDLES);
}

function emaSeries(closes, period) {
    const out = new Array(closes.length).fill(NaN);
    if (!closes.length) return out;
    const k = 2 / (period + 1);
    let e = closes[0];
    for (let i = 0; i < closes.length; i++) {
        e = i === 0 ? closes[0] : closes[i] * k + e * (1 - k);
        out[i] = i >= period - 1 ? e : NaN;
    }
    return out;
}

function fitCanvas(canvas) {
    const dpr = Math.min(window.devicePixelRatio || 1, 3);
    const rect = canvas.getBoundingClientRect();
    const w = Math.max(50, Math.floor(rect.width));
    const h = Math.max(50, Math.floor(rect.height));
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
        canvas.width = w * dpr;
        canvas.height = h * dpr;
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx, w, h };
}

function draw() {
    if (!canvasEl || !candles.length) return;
    const { ctx, w, h } = fitCanvas(canvasEl);
    ctx.clearRect(0, 0, w, h);

    // Deep-navy stage background + vignette (reference look).
    const bg = ctx.createLinearGradient(0, 0, 0, h);
    bg.addColorStop(0, '#0b1030');
    bg.addColorStop(0.55, '#070a1f');
    bg.addColorStop(1, '#04050f');
    ctx.fillStyle = bg;
    ctx.fillRect(0, 0, w, h);
    const vg = ctx.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.3, w / 2, h / 2, Math.max(w, h) * 0.75);
    vg.addColorStop(0, 'rgba(0,0,0,0)');
    vg.addColorStop(1, 'rgba(0,0,0,0.38)');
    ctx.fillStyle = vg;
    ctx.fillRect(0, 0, w, h);

    const gutter = 40;
    const cw = w - gutter;
    const volH = Math.floor(h * 0.18);
    const priceH = h - volH - 6;

    let lo = Infinity, hi = -Infinity, maxV = 0;
    for (const k of candles) {
        if (k.l < lo) lo = k.l;
        if (k.h > hi) hi = k.h;
        if (k.v > maxV) maxV = k.v;
    }
    if (!Number.isFinite(lo) || !Number.isFinite(hi) || hi - lo < 1e-12) {
        lo -= 1; hi += 1;
    }
    const span = hi - lo;
    const y = (p) => 4 + ((hi - p) / span) * (priceH - 8);

    // Dotted grid + min/max labels (reference has dotted lines, not solid).
    ctx.strokeStyle = GRID;
    ctx.fillStyle = TEXT_DIM;
    ctx.font = '9px system-ui, sans-serif';
    ctx.lineWidth = 1;
    ctx.setLineDash([1, 4]);
    const rows = 3;
    for (let i = 0; i <= rows; i++) {
        const gy = 4 + ((priceH - 8) * i) / rows;
        ctx.beginPath();
        ctx.moveTo(0, gy);
        ctx.lineTo(cw, gy);
        ctx.stroke();
    }
    ctx.setLineDash([]);
    ctx.fillText(hi.toFixed(5), cw + 3, 10);
    ctx.fillText(lo.toFixed(5), cw + 3, priceH - 2);

    const n = candles.length;
    const slot = cw / n;
    const bodyW = Math.max(3, Math.min(16, slot * 0.68));
    const pulse = 0.6 + 0.4 * Math.sin(Date.now() / 600);

    // Ambient sway ("колыхание"): candles gently breathe even with no ticks.
    // Pure decoration — ±1px, far below price-move scale (tens of px), so it
    // can never be misread as market data. Grid/EMA/price tag stay static
    // as the anchor of truth; only candle bodies/wicks/volumes shimmer.
    const t = Date.now() / 1000;
    const swayX = (i) => Math.sin(t * 1.3 + i * 0.7) * 1.0;
    const swayY = (i) => Math.cos(t * 1.1 + i * 0.9) * 0.8;

    // Volume profile
    if (maxV > 0) {
        for (let i = 0; i < n; i++) {
            const k = candles[i];
            const vh = Math.max(1, (k.v / maxV) * volH);
            const x = i * slot + (slot - bodyW) / 2 + swayX(i);
            ctx.fillStyle = k.c >= k.o ? 'rgba(34,211,238,0.45)' : 'rgba(244,114,182,0.45)';
            ctx.fillRect(x, h - vh, bodyW, vh);
        }
    }

    // Flowing waves: real EMA-9 + EMA-21 over closes (the reference curves,
    // but computed from data — never synthesized).
    const closes = candles.map(k => k.c);
    const waves = [
        { data: emaSeries(closes, 9), color: '#c084fc' },
        { data: emaSeries(closes, 21), color: EMA_COLOR }
    ];
    for (const wv of waves) {
        ctx.strokeStyle = wv.color;
        ctx.lineWidth = 2;
        ctx.shadowColor = wv.color;
        ctx.shadowBlur = 8;
        ctx.beginPath();
        let started = false;
        for (let i = 0; i < n; i++) {
            if (!Number.isFinite(wv.data[i])) continue;
            const x = i * slot + slot / 2;
            if (!started) { ctx.moveTo(x, y(wv.data[i])); started = true; }
            else ctx.lineTo(x, y(wv.data[i]));
        }
        ctx.stroke();
    }
    ctx.shadowBlur = 0;
    // Legend for the waves
    ctx.font = '8px system-ui, sans-serif';
    ctx.fillStyle = '#c084fc';
    ctx.fillText('EMA9', 5, 11);
    ctx.fillStyle = EMA_COLOR;
    ctx.fillText('EMA21', 5, 21);

    // Candles: two-pass neon (glow body + hot core line).
    for (let i = 0; i < n; i++) {
        const k = candles[i];
        const bull = k.c >= k.o;
        const color = bull ? BULL : BEAR;
        const core = bull ? '#a5f3fc' : '#fbcfe8';
        const isForming = i === n - 1;
        const cx = i * slot + slot / 2 + swayX(i);
        const dy = swayY(i);
        const glow = isForming ? 8 + 12 * pulse : 8;

        // Pass 1 — glowing silhouette (wick + body)
        ctx.strokeStyle = color;
        ctx.fillStyle = color;
        ctx.shadowColor = color;
        ctx.shadowBlur = glow;

        // Wick
        ctx.lineWidth = Math.max(1.2, bodyW * 0.22);
        ctx.beginPath();
        ctx.moveTo(cx, y(k.h) + dy);
        ctx.lineTo(cx, y(k.l) + dy);
        ctx.stroke();

        // Body (doji -> thin line)
        const yO = y(k.o) + dy, yC = y(k.c) + dy;
        if (Math.abs(yC - yO) < 1.5) {
            ctx.lineWidth = 1.5;
            ctx.beginPath();
            ctx.moveTo(cx - bodyW / 2, yC);
            ctx.lineTo(cx + bodyW / 2, yC);
            ctx.stroke();
        } else {
            const top = Math.min(yO, yC);
            ctx.fillRect(cx - bodyW / 2, top, bodyW, Math.abs(yC - yO));
        }

        // Pass 2 — hot neon core (thin bright centerline, no extra glow)
        ctx.shadowBlur = 0;
        ctx.strokeStyle = core;
        ctx.globalAlpha = isForming ? 0.55 + 0.35 * pulse : 0.5;
        ctx.lineWidth = Math.max(1, bodyW * 0.22);
        ctx.beginPath();
        const coreTop = Math.abs(yC - yO) < 1.5 ? yC : Math.min(yO, yC) + 1;
        const coreBot = Math.abs(yC - yO) < 1.5 ? yC : Math.max(yO, yC) - 1;
        ctx.moveTo(cx, coreTop);
        ctx.lineTo(cx, Math.max(coreTop + 0.5, coreBot));
        ctx.stroke();
        ctx.globalAlpha = 1;
    }
    ctx.shadowBlur = 0;

    // Swing extreme arrows (real highest high / lowest low of the window).
    let hiIdx = 0, loIdx = 0;
    for (let i = 1; i < n; i++) {
        if (candles[i].h > candles[hiIdx].h) hiIdx = i;
        if (candles[i].l < candles[loIdx].l) loIdx = i;
    }
    const arrow = (x, ay, up, color) => {
        ctx.fillStyle = color;
        ctx.shadowColor = color;
        ctx.shadowBlur = 8;
        ctx.beginPath();
        if (up) { ctx.moveTo(x, ay + 7); ctx.lineTo(x - 4, ay); ctx.lineTo(x + 4, ay); }
        else { ctx.moveTo(x, ay - 7); ctx.lineTo(x - 4, ay); ctx.lineTo(x + 4, ay); }
        ctx.closePath();
        ctx.fill();
        ctx.shadowBlur = 0;
    };
    if (hiIdx !== loIdx) {
        arrow(hiIdx * slot + slot / 2, y(candles[hiIdx].h) - 4, false, BEAR);
        arrow(loIdx * slot + slot / 2, y(candles[loIdx].l) + 4, true, BULL);
    }

    // Ambient particles (pure decoration, like the sway — dim drifting dots).
    for (let s = 0; s < 14; s++) {
        const px = ((s * 0.37 + 0.11) % 1) * cw;
        const py = h - ((t * 6 * (0.5 + (s % 3) * 0.25) + s * 47) % h);
        const tw = 0.10 + 0.22 * (0.5 + 0.5 * Math.sin(t * 2 + s * 1.7));
        ctx.fillStyle = s % 2 === 0 ? `rgba(34,211,238,${tw.toFixed(3)})` : `rgba(244,114,182,${tw.toFixed(3)})`;
        ctx.beginPath();
        ctx.arc(px, py, 1 + (s % 2) * 0.6, 0, Math.PI * 2);
        ctx.fill();
    }

    // Last-price dashed line + tag
    const last = candles[n - 1].c;
    const ly = y(last);
    const bullLast = candles[n - 1].c >= candles[n - 1].o;
    ctx.strokeStyle = bullLast ? BULL : BEAR;
    ctx.setLineDash([4, 3]);
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, ly);
    ctx.lineTo(cw, ly);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle = bullLast ? BULL : BEAR;
    const tag = last.toFixed(5);
    const tagW = ctx.measureText(tag).width + 8;
    ctx.fillRect(w - Math.max(tagW, 40), ly - 8, Math.max(tagW, 40), 15);
    ctx.fillStyle = '#0b0e1a';
    ctx.fillText(tag, w - Math.max(tagW, 40) + 4, ly + 4);
}

function setLiveDot(on) {
    const dot = document.getElementById('liveDot');
    if (dot) dot.className = 'live-dot' + (on ? ' on' : '');
}

function connectWs(asset, initData, myRun) {
    try {
        if (ws) { try { ws.close(); } catch (e) { /* ignore */ } ws = null; }
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const url = protocol + '//' + window.location.host + '/ws/prices?asset='
            + encodeURIComponent(asset) + '&init_data=' + encodeURIComponent(initData || '');
        ws = new WebSocket(url);
    } catch (e) {
        scheduleReconnect(asset, initData, myRun);
        return;
    }
    ws.onmessage = function (event) {
        if (myRun !== runId) return;
        try {
            const data = JSON.parse(event.data);
            const price = Number(data.price);
            if (!Number.isFinite(price) || price <= 0 || !candles.length) return;
            const f = candles[candles.length - 1];
            f.c = price;
            if (price > f.h) f.h = price;
            if (price < f.l) f.l = price;
            lastTickAt = Date.now();
            setLiveDot(true);
        } catch (e) { /* ignore malformed tick */ }
    };
    const dead = function () {
        if (myRun !== runId) return;
        setLiveDot(false);
        scheduleReconnect(asset, initData, myRun);
    };
    ws.onclose = dead;
    ws.onerror = dead;
}

let reconnectTimer = null;
function scheduleReconnect(asset, initData, myRun) {
    if (myRun !== runId) return;
    if (reconnectTimer) clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(() => {
        if (myRun !== runId) return;
        connectWs(asset, initData, myRun);
    }, 5000);
}

export function startLiveChart(ohlc, asset, initData) {
    stopLiveChart();
    const clean = sanitizeOhlc(ohlc);
    if (!clean.length) return false;
    canvasEl = document.getElementById('liveChart');
    if (!canvasEl) return false;
    candles = clean;
    lastTickAt = 0;
    const myRun = ++runId;
    const card = document.getElementById('liveChartCard');
    if (card) card.style.display = 'block';
    const title = document.getElementById('liveChartTitle');
    if (title && asset) title.innerText = asset;
    setLiveDot(false);
    connectWs(asset, initData, myRun);
    draw();
    timer = setInterval(() => {
        if (myRun !== runId) return;
        if (document.hidden) return;
        draw();
    }, REDRAW_MS);
    return true;
}

export function stopLiveChart() {
    runId++;
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    if (timer) { clearInterval(timer); timer = null; }
    if (ws) { try { ws.close(); } catch (e) { /* ignore */ } ws = null; }
    candles = [];
    canvasEl = null;
    setLiveDot(false);
    const card = document.getElementById('liveChartCard');
    if (card) card.style.display = 'none';
}
