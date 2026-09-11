import { tg, currentAsset, currentTf, getCustomInitData } from './main.js';
import { updateLivePriceUI, renderError, clearResults, startStatusBar, stopStatusBar, flashResults, renderDirSvg, renderMiniChart, renderSparklinePrediction, switchResultTab, parseMd, pricesToBars, renderExpiryCandles, showAiChart, hideAiChart, updateAiChartData } from './ui.js';

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
        
        showAiChart();

        // Fetch early OHLC specifically for the animated chart during the analysis phase
        fetch(`/api/chart-ohlc?asset=${encodeURIComponent(currentAsset)}&timeframe=${currentTf}`)
            .then(r => r.json())
            .then(ohlc => {
                if (ohlc && ohlc.length) {
                    updateAiChartData(ohlc);
                }
            })
            .catch(err => console.log('ohlc error', err));

        const startTime = Date.now();

        const res = await fetch(`/api/analyze?asset=${encodeURIComponent(currentAsset)}&timeframe=${currentTf}&_=${Date.now()}`, {
            headers: {
                'X-Telegram-Init-Data': tg && tg.initData ? tg.initData : getCustomInitData()
            }
        });
        const rawData = await res.json();
        
        if (rawData && rawData.result && rawData.result.chartOhlc) {
            updateAiChartData(rawData.result.chartOhlc);
        } else if (rawData && rawData.chartOhlc) {
            updateAiChartData(rawData.chartOhlc);
        }
        
        let data = rawData;
        let config = null;
        if (rawData.result && rawData.config) {
            data = rawData.result;
            config = rawData.config;
        }

        const elapsed = Date.now() - startTime;
        const remainingDelay = Math.max(0, 2000 - elapsed);

        setTimeout(() => {
            hideAiChart();
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

            // Measured fact vs model score: show real asset winrate when measured
            // (the big % is a rescaled model score, NOT a measured win probability).
            const factEl = document.getElementById('resProbFact');
            if (factEl) {
                if (data.winRateAsset != null && (data.signalsVerifiedAsset || 0) >= 5) {
                    factEl.innerText = `факт: ${Math.round(data.winRateAsset)}% (n=${data.signalsVerifiedAsset})`;
                } else {
                    factEl.innerText = '';
                }
            }

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
                if (rp) rp.innerHTML += " <span title='Старший таймфрейм против сигнала — оценка уже снижена'>⚠️</span>";
            }


            // Market Weather Bindings
            const wSession = document.getElementById("weatherSession");
            if (wSession && data.uiMarketSession) wSession.innerText = data.uiMarketSession;
            
            const wPhase = document.getElementById("weatherPhase");
            if (wPhase && data.uiMarketPhase) {
                wPhase.innerText = data.uiMarketPhase;
                let phaseColor = "#10b981"; // Green default
                if (data.uiMarketPhase.includes("Замедление") || data.uiMarketPhase.includes("Переход")) {
                    phaseColor = "#f59e0b"; // Yellow (Average)
                } else if (data.uiMarketPhase.includes("Боковик") || data.uiMarketPhase.includes("Неопределенность") || data.uiMarketPhase.includes("Слабый") || data.uiMarketPhase.includes("Волатильный") || data.uiMarketPhase.includes("Резкий") || data.uiMarketPhase.includes("Шум")) {
                    phaseColor = "#ef4444"; // Red (Bad)
                }
                wPhase.style.color = phaseColor;
            }
            
            const wEntropy = document.getElementById("weatherEntropy");
            const wTitle = document.getElementById("weatherTitle");
            if (wEntropy && data.uiMarketEntropy) {
                wEntropy.innerText = data.uiMarketEntropy;
                let entColor = "#10b981";
                if (data.uiMarketEntropy.includes("Опасно") || data.uiMarketEntropy.includes("ВЫСОКАЯ")) {
                    entColor = "#ef4444";
                } else if (data.uiMarketEntropy.includes("Мертвый") || data.uiMarketEntropy.includes("Слабая") || data.uiMarketEntropy.includes("Переход")) {
                    entColor = "#f59e0b";
                }
                wEntropy.style.color = entColor;
                if (wTitle) wTitle.style.color = entColor;
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

            // Consensus Radar Card
            const rCard = document.getElementById('consensusRadarCard');
            if (rCard) {
                // Show the radar if there is a direction
                if (data.direction) {
                    rCard.style.display = 'block';

                    // Formatting helper
                    const formatDir = (dir, conf) => {
                        if (dir === 'BUY') return `<span style='color:#10b981'>🟩 ВВЕРХ${conf ? ` (${conf}%)` : ''}</span>`;
                        if (dir === 'PUT') return `<span style='color:#ef4444'>🟥 ВНИЗ${conf ? ` (${conf}%)` : ''}</span>`;
                        return `<span style='color:var(--subtext)'>🟨 НЕЙТРАЛЬНО</span>`;
                    };

                    const rMl = document.getElementById('radarMl');
                    if (rMl) rMl.innerHTML = formatDir(data.lgbmDirection, data.lgbmConfidence);

                    const rSmc = document.getElementById('radarSmc');
                    if (rSmc) rSmc.innerHTML = formatDir(data.smcDirection);

                    const rOf = document.getElementById('radarOf');
                    if (rOf) rOf.innerHTML = formatDir(data.ofDirection);

                    const rTa = document.getElementById('radarTa');
                    if (rTa) rTa.innerHTML = formatDir(data.taDirection);
                } else {
                    rCard.style.display = 'none';
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

            const probBars = pricesToBars(data.chartData, 20); // Get 20 candles for smoother history
            // Honest projection length: expected move = ATR*sqrt(expiry) as a
            // fraction of the displayed price span (volatility scaling). Falls
            // back to legacy fixed dash when ATR is unavailable.
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
            // Expiry candles (N = expiryCandles) when backend provides OHLC;
            // fallback to legacy price bars for old backend responses.
            if (data.chartOhlc && data.chartOhlc.length) renderExpiryCandles('durChart', data.chartOhlc, data.expiryCandles);
            else if (durBars.length) renderMiniChart('durChart', durBars, '');

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
