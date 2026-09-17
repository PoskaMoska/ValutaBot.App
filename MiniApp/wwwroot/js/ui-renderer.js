import { switchResultTab, renderSparklinePrediction, pricesToBars, renderDirSvg, renderExpiryCandles, renderMiniChart, flashResults } from './ui.js?v=20260918_1';

export function renderAnalysisResult(uiModel, btn, sphere, currentAsset, currentTf, isRepeat) {
    if (!uiModel) return;

    // Apply config visibility
    const mlCard = document.getElementById('mlEnsembleCard');
    if (mlCard) mlCard.style.display = uiModel.showMl ? 'block' : 'none';
    
    const smcCard = document.getElementById('smcCard');
    if (smcCard) smcCard.style.display = uiModel.showSmc ? 'block' : 'none';
    
    const ofCard = document.getElementById('orderFlowCard');
    if (ofCard) ofCard.style.display = uiModel.showOf ? 'block' : 'none';

    // Warnings
    const warnReg = document.getElementById('warningRegion');
    if (warnReg && uiModel.warningMessage) {
        warnReg.innerText = uiModel.warningMessage;
        warnReg.style.display = 'block';
    } else if (warnReg) {
        warnReg.style.display = 'none';
    }

    // Direction and Probability
    const resDir = document.getElementById('resDir');
    const resDirLabel = document.getElementById('resDirLabel');
    if (resDirLabel) {
        resDirLabel.innerHTML = isRepeat 
            ? 'Направление <span title="Сигнал не изменился" style="font-size:10px;opacity:0.8">🔄</span>'
            : 'Направление';
    }

    if (uiModel.direction === 'BUY') {
        resDir.innerHTML = 'ВВЕРХ';
        resDir.style.color = '#00e676';
        if (sphere) sphere.classList.add('buy-signal');
    } else if (uiModel.direction === 'PUT') {
        resDir.innerHTML = 'ВНИЗ';
        resDir.style.color = '#ff1744';
        if (sphere) sphere.classList.add('put-signal');
    } else {
        resDir.innerHTML = 'НЕЙТРАЛЬНО';
        resDir.style.color = 'var(--dim)';
        if (sphere) sphere.classList.add('neutral-signal');
    }

    const goldenBadgeMain = document.getElementById('goldenSetupBadgeMain');
    if (goldenBadgeMain) {
        goldenBadgeMain.style.display = uiModel.goldenSetup ? 'inline-block' : 'none';
    }
    const goldenBadge = document.getElementById('goldenSetupBadge');
    if (goldenBadge) {
        goldenBadge.style.display = uiModel.goldenSetup ? 'inline-block' : 'none';
    }

    const probEl = document.getElementById('resProb');
    if (probEl) {
        probEl.innerText = uiModel.probability + '%';
        if (uiModel.tfConflict) {
            probEl.innerHTML += " <span title='Старший таймфрейм против сигнала — оценка уже снижена'>⚠️</span>";
        }
        probEl.style.color = uiModel.probability >= 90 ? '#00e676' : uiModel.probability >= 85 ? '#ffd600' : 'var(--accent)';
    }

    const durEl = document.getElementById('resDur');
    if (durEl) durEl.innerText = uiModel.duration;

    const resSess = document.getElementById('resSess');
    if (resSess) resSess.innerText = uiModel.uiMarketSession;

    const resPhase = document.getElementById('resPhase');
    if (resPhase) resPhase.innerText = uiModel.uiMarketPhase;
    
    const resEntr = document.getElementById('resEntr');
    if (resEntr) resEntr.innerText = uiModel.uiMarketEntropy;

    // Radar Data
    const rCard = document.getElementById('consensusRadarCard');
    if (rCard) {
        if (uiModel.direction && uiModel.direction !== 'NEUTRAL') {
            rCard.style.display = 'block';

            const formatDir = (dir, conf) => {
                if (dir === 'BUY') return `<span style='color:#10b981; display:flex; align-items:center; gap:4px;'><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg> ВВЕРХ${conf ? ` (${conf}%)` : ''}</span>`;
                if (dir === 'PUT') return `<span style='color:#ef4444; display:flex; align-items:center; gap:4px;'><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 5v14M19 12l-7 7-7-7"/></svg> ВНИЗ${conf ? ` (${conf}%)` : ''}</span>`;
                return `<span style='color:var(--subtext); display:flex; align-items:center; gap:4px;'><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14"/></svg> НЕЙТРАЛЬНО</span>`;
            };

            const rMl = document.getElementById('radarMl');
            if (rMl) rMl.innerHTML = formatDir(uiModel.lgbmDirection, uiModel.lgbmConfidence);
            const rSmc = document.getElementById('radarSmc');
            if (rSmc) rSmc.innerHTML = formatDir(uiModel.smcDirection, uiModel.smcConfidence);
            const rOf = document.getElementById('radarOf');
            if (rOf) rOf.innerHTML = formatDir(uiModel.ofDirection, uiModel.ofConfidence);
            const rTa = document.getElementById('radarTa');
            if (rTa) rTa.innerHTML = formatDir(uiModel.taDirection, uiModel.taConfidence);
        } else {
            rCard.style.display = 'none';
        }
    }

    // Stats
    const wrAssetEl = document.getElementById('winRateAsset');
    if (wrAssetEl) {
        if (uiModel.winRateAsset !== null) {
            const pct = Math.round(uiModel.winRateAsset * 100);
            wrAssetEl.innerText = pct + '%';
            wrAssetEl.style.color = pct >= 55 ? '#10b981' : pct >= 50 ? '#f59e0b' : '#f43f5e';
        } else {
            wrAssetEl.innerText = 'нет данных';
            wrAssetEl.style.color = 'var(--subtext)';
        }
    }

    const wrOverallEl = document.getElementById('winRateOverall');
    if (wrOverallEl) {
        if (uiModel.winRateOverall !== null) {
            const pct = Math.round(uiModel.winRateOverall * 100);
            wrOverallEl.innerText = pct + '%';
            wrOverallEl.style.color = pct >= 55 ? '#10b981' : pct >= 50 ? '#f59e0b' : '#f43f5e';
        } else {
            wrOverallEl.innerText = 'нет данных';
        }
    }

    const sigCountEl = document.getElementById('signalsCount');
    if (sigCountEl) {
        sigCountEl.innerText = uiModel.signalsVerified + (uiModel.signalsPending > 0 ? ' (+' + uiModel.signalsPending + ')' : '');
    }

    // Reasoning
    const reasoningEl = document.getElementById('resReasoning');
    if (reasoningEl && uiModel.adaptiveReasoning) {
        reasoningEl.innerText = uiModel.adaptiveReasoning;
    }

    // Confluence
    const confluenceCard = document.getElementById('mtfConfluenceCard');
    if (confluenceCard && uiModel.confluenceLabel) {
        confluenceCard.style.display = 'block';
        const labelEl = document.getElementById('mtfLabel');
        if (labelEl) labelEl.innerText = uiModel.confluenceLabel;
        const mtfcEl = document.getElementById('mtfConfluence');
        if (mtfcEl) {
            const ratio = uiModel.confluenceRatio || 0;
            mtfcEl.innerText = Math.round(ratio * 100) + '%';
        }
    } else if (confluenceCard) {
        confluenceCard.style.display = 'none';
    }

    // Charts
    const probBars = pricesToBars(uiModel.chartData, 20);
    let projFrac = null;
    if (uiModel.atr > 0 && uiModel.chartData.length >= 2 && uiModel.expiryCandles > 0) {
        const tailP = uiModel.chartData.slice(-20);
        const spanP = Math.max(...tailP) - Math.min(...tailP);
        if (spanP > 1e-12) {
            projFrac = Math.min(0.9, Math.max(0.05, (uiModel.atr * Math.sqrt(uiModel.expiryCandles)) / spanP * 0.8));
        }
    }
    if (probBars.length) renderSparklinePrediction('probChart', probBars, uiModel.direction, projFrac);

    renderDirSvg(uiModel.direction);

    const durBars = pricesToBars(uiModel.chartData, 8);
    if (uiModel.chartOhlc && uiModel.chartOhlc.length) {
        renderExpiryCandles('durChart', uiModel.chartOhlc, uiModel.expiryCandles);
    } else if (durBars.length) {
        renderMiniChart('durChart', durBars, '');
    }

    const tabReg = document.getElementById('resultsTabBar');
    if (tabReg) tabReg.style.display = 'flex';
    switchResultTab('chart');
    if (typeof flashResults === 'function') flashResults();
}
