import { tg, currentAsset, currentTf, getCustomInitData } from './main.js';
import { updateLivePriceUI, renderError, clearResults, startStatusBar, stopStatusBar, flashResults, renderDirSvg, renderMiniChart, switchResultTab, parseMd, pricesToBars } from './ui.js';

export let priceSocket = null;
export let lastPriceVal = 0;
export let timeOffset = 0;

// Tracks the last signal to detect unchanged results
let lastSignalKey = null; // format: "ASSET_TF_DIRECTION_PROB"
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
        const wsUrl = `${protocol}//${window.location.host}/ws/prices?asset=${encodeURIComponent(currentAsset)}`;
        
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

        priceSocket.onclose = function() {
            console.log('Price WebSocket closed');
        };

        priceSocket.onerror = function(err) {
            console.error('Price WebSocket error:', err);
        };
    } catch (err) {
        console.error('Failed to create WebSocket:', err);
    }
}

export function closePriceWebSocket() {
    if (priceSocket) {
        try {
            priceSocket.close();
        } catch(e) {}
        priceSocket = null;
    }
    lastPriceVal = 0;
}

export async function syncTime() {
    try {
        var r = await fetch('/api/time', {
            headers: {
                'X-Telegram-Init-Data': tg ? tg.initData : ''
            }
        });
        var d = await r.json();
        timeOffset = d.t - Date.now();
    } catch(e) { timeOffset = 0; }
}

export async function executeAnalysis() {
    const btn = document.getElementById('btnGet');
    if (btn && btn.disabled) return;
    const sphere = document.getElementById('mainSphere');
    
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

        const startTime = Date.now();

        const res = await fetch(`/api/analyze?asset=${encodeURIComponent(currentAsset)}&timeframe=${currentTf}&_=${Date.now()}`, {
            headers: {
                'X-Telegram-Init-Data': tg && tg.initData ? tg.initData : getCustomInitData()
            }
        });
        const rawData = await res.json();
        
        let data = rawData;
        let config = null;
        if (rawData.result && rawData.config) {
            data = rawData.result;
            config = rawData.config;
        }

        const elapsed = Date.now() - startTime;
        const remainingDelay = Math.max(0, 2000 - elapsed);

        setTimeout(() => {
            stopStatusBar();
            if (sphere) sphere.classList.remove('analyzing');
            if (btn) {
                btn.disabled = false;
                btn.innerText = 'ПОЛУЧИТЬ АНАЛИЗ';
            }

            if(data.error) {
                const debugMsg = `• Длина токена: ${tg && tg.initData ? tg.initData.length : 0}\n• Платформа: ${tg ? tg.platform : 'unknown'}\n• Адрес: ${window.location.href}`;
                renderError(data.error, debugMsg);
                return;
            }
            
            // Apply config to UI elements
            if (config) {
                const mlCard = document.getElementById('mlEnsembleCard');
                if (mlCard) mlCard.style.display = config.ml ? 'block' : 'none';
                
                const smcCard = document.getElementById('smcCard');
                if (smcCard) smcCard.style.display = config.smc ? 'block' : 'none';
                
                const ofCard = document.getElementById('orderFlowCard');
                if (ofCard) ofCard.style.display = config.of ? 'block' : 'none';
            }

            const resDir = document.getElementById('resDir');
            const signalKey = `${currentAsset}_${currentTf}_${data.direction}_${data.probability}`;
            const isRepeat = lastSignalKey !== null && signalKey === lastSignalKey;
            lastSignalKey = signalKey;

            if (data.direction === 'BUY') {
                resDir.innerHTML = isRepeat
                    ? 'ВВЕРХ <span title="Сигнал не изменился" style="font-size:13px;opacity:0.7">🔄</span>'
                    : 'ВВЕРХ';
                resDir.style.color = '#00e676';
                sphere.classList.add('buy-signal');
            } else if (data.direction === 'PUT') {
                resDir.innerHTML = isRepeat
                    ? 'ВНИЗ <span title="Сигнал не изменился" style="font-size:13px;opacity:0.7">🔄</span>'
                    : 'ВНИЗ';
                resDir.style.color = '#ff1744';
                sphere.classList.add('put-signal');
            } else {
                resDir.innerHTML = 'НЕЙТРАЛЬНО';
                resDir.style.color = 'var(--dim)';
                sphere.classList.add('neutral-signal');
            }

            document.getElementById('resProb').innerText = data.probability + '%';
            document.getElementById('resProb').style.color = data.probability >= 90 ? '#00e676' : data.probability >= 85 ? '#ffd600' : 'var(--accent)';

            document.getElementById('resDur').innerText = data.duration;

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
                    const vs = data.volumeStrength;
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
                if (rp) rp.innerText += ' ⚠️';
            }

            // ML Ensemble Card
            if (data.llmReport && !data.llmReport.includes('Оффлайн')) {
                const mlCard = document.getElementById('mlEnsembleCard');
                if (mlCard) mlCard.style.display = (config && config.ml === false) ? 'none' : 'block';
                const badge = document.getElementById('mlEnsembleBadge');
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
                if (conf && data.lgbmConfidence) {
                    conf.innerText = (data.lgbmConfidence * 100).toFixed(0) + '%';
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
            if (confCard) confCard.style.display = 'block';
            const confLabel = document.getElementById('confluenceLabel');
            if (confLabel) confLabel.innerText = data.confluenceLabel || 'Анализ';
            const goldenBadge = document.getElementById('goldenSetupBadge');
            const goldenBadgeMain = document.getElementById('goldenSetupBadgeMain');
            if (goldenBadge) goldenBadge.style.display = data.goldenSetup ? 'inline-block' : 'none';
            if (goldenBadgeMain) goldenBadgeMain.style.display = data.goldenSetup ? 'inline-block' : 'none';
            const wrAssetEl = document.getElementById('winRateAsset');
            if (wrAssetEl) {
                if (data.winRateAsset != null) {
                    const pct = Math.round(data.winRateAsset * 100);
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
                    const pct = Math.round(data.winRateOverall * 100);
                    wrOverallEl.innerText = pct + '%';
                    wrOverallEl.style.color = pct >= 55 ? '#10b981' : pct >= 50 ? '#f59e0b' : '#f43f5e';
                } else {
                    wrOverallEl.innerText = 'нет данных';
                }
            }
            const sigCountEl = document.getElementById('signalsCount');
            if (sigCountEl) {
                const verified = data.signalsVerified || 0;
                const pending = data.signalsPending || 0;
                sigCountEl.innerText = verified + (pending > 0 ? ' (+' + pending + ')' : '');
            }

            // Monte Carlo & Risk Card (Hidden by user request)
            /*
            if (data.evLabel || data.kellyLabel) {
                const mcCard = document.getElementById('mcCard');
                if (mcCard) mcCard.style.display = 'none';
                const mcSimEl = document.getElementById('mcSimCount');
                if (mcSimEl && data.monteCarloIterations) {
                    mcSimEl.innerText = (data.monteCarloSuccess || 0) + ' / ' + data.monteCarloIterations + ' удачных';
                }
                const evEl = document.getElementById('mcEv');
                if (evEl) {
                    evEl.innerText = data.evLabel || '--';
                    evEl.style.color = (data.evPct && data.evPct > 0) ? '#10b981' : '#f43f5e';
                }
                const kellyEl = document.getElementById('mcKelly');
                if (kellyEl) {
                    kellyEl.innerText = data.kellyLabel || '--';
                    kellyEl.style.color = (data.kellyRiskPct && data.kellyRiskPct > 0) ? '#f59e0b' : '#ff1744';
                }
                const wfEl = document.getElementById('wfStatus');
                if (wfEl) {
                    if (data.wfIsCooloffActive) {
                        wfEl.innerText = 'Охлаждение';
                        wfEl.style.color = '#ff1744';
                    } else {
                        wfEl.innerText = 'В норме';
                        wfEl.style.color = '#10b981';
                    }
                }
            }
            */

            // Reasoning Card
            if (data.claudeReasoning) {
                const rCard = document.getElementById('reasoningCard');
                if (rCard) rCard.style.display = 'none';
                const rText = document.getElementById('reasoningText');
                if (rText) rText.innerText = data.claudeReasoning;
                const rDir = document.getElementById('reasoningDir');
                if (rDir) {
                    rDir.innerText = data.direction === 'BUY' ? 'ВВЕРХ' : data.direction === 'PUT' ? 'ВНИЗ' : 'НЕЙТРАЛЬНО';
                    rDir.style.color = data.direction === 'BUY' ? '#a78bfa' : data.direction === 'PUT' ? '#f472b6' : 'var(--dim)';
                }
            }

            // News Card (Hidden by user request)
            /*
            if (data.newsScore && Math.abs(data.newsScore) > 0.1 && data.newsSummary) {
                const nCard = document.getElementById('newsCard');
                if (nCard) nCard.style.display = 'none';
                const nSent = document.getElementById('newsSentimentEl');
                if (nSent) {
                    nSent.innerText = data.newsSentiment || '--';
                    nSent.style.color = data.newsScore > 0 ? '#00e676' : '#ff1744';
                }
                const nSum = document.getElementById('newsSummaryEl');
                if (nSum) nSum.innerText = data.newsSummary;
            }
            */

            const probBars = pricesToBars(data.chartData, 16);
            if (probBars.length) renderMiniChart('probChart', probBars, '');

            renderDirSvg(data.direction);

            const durBars = pricesToBars(data.chartData, 8);
            if (durBars.length) renderMiniChart('durChart', durBars, '');

            const tabReg = document.getElementById('resultsTabBar');
            if (tabReg) tabReg.style.display = 'flex';
            switchResultTab('chart');
            flashResults();

        }, remainingDelay);
    } catch(e) {
        stopStatusBar();
        sphere.classList.remove('analyzing');
        btn.disabled = false;
        btn.innerText = 'ПОЛУЧИТЬ АНАЛИЗ';
        const catchMsg = `• Длина токена: ${tg && tg.initData ? tg.initData.length : 0}\n• Платформа: ${tg ? tg.platform : 'unknown'}\n• Адрес: ${window.location.href}`;
        renderError(e.message, catchMsg);
    }
}
