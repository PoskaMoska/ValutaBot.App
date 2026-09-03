import { initPriceWebSocket, syncTime, executeAnalysis, timeOffset, resetSignalKey } from './api.js';
import { switchResultTab } from './ui.js';

export const tg = window.Telegram ? window.Telegram.WebApp : null;
if (tg) {
    try {
        tg.expand();
        tg.ready();
    } catch(e) {}
}

export function getCustomInitData() {
    const urlParams = new URLSearchParams(window.location.search);
    const userId = urlParams.get('custom_user_id') || urlParams.get('userId');
    const userSign = urlParams.get('custom_user_sign') || urlParams.get('userSign');
    if (userId && userSign) {
        return `custom_user_id=${userId}&custom_user_sign=${userSign}`;
    }
    return '';
}

export let currentAsset = 'EUR/USD OTC';
export let currentTf = 'm1';

const assetsData = {
    fiat: {
        otc: ['EUR/USD OTC', 'GBP/USD OTC', 'USD/JPY OTC']
    },
    commodities: {
        otc: []
    },
    crypto: {
        otc: []
    },
    stocks: {
        otc: []
    }
};

function getTopAssets() {
    try {
        const h = JSON.parse(localStorage.getItem('vhistory') || '[]');
        var freq = {};
        for (var i = 0; i < h.length; i++) { var e = h[i]; freq[e.asset] = (freq[e.asset] || 0) + 1; }
        return Object.keys(freq).sort(function(a,b) { return freq[b] - freq[a]; }).slice(0, 8);
    } catch(e) { return []; }
}

function renderAssets(arr) {
    const top = getTopAssets();
    const majors = ['EUR/USD OTC', 'GBP/USD OTC', 'USD/JPY OTC'];
    return arr.map(function(a) {
        var star = top.indexOf(a) !== -1 ? '<span class="top-star">★</span>' : '';
        var cls = majors.indexOf(a) !== -1 ? 'asset-item major' : 'asset-item';
        return '<div class="' + cls + '" data-asset="' + a + '">' + a + star + '</div>';
    }).join('');
}

function changeTopCategory(el) {
    if (!el) return;
    el = el.closest('.top-cat-btn') || el;
    document.querySelectorAll('.top-cat-btn').forEach(c => c.classList.remove('active'));
    el.classList.add('active');
    let cat = el.getAttribute('data-cat') || 'fiat';
    if (!assetsData[cat]) cat = 'fiat';
    const gridEl = document.getElementById('assetGrid');
    if (gridEl) {
        gridEl.innerHTML = `<div class='otc-scroll' style='grid-column:1/-1'><div class='asset-grid'>${renderAssets(assetsData[cat].otc)}</div></div>`;
    }
    let firstAssetEl = document.querySelector('.asset-item');
    if (firstAssetEl) setAsset(firstAssetEl);
}

function toggleMenu(m, b) {
    document.querySelectorAll('.asset-menu, .tf-menu').forEach(menu => { 
        if(menu.id !== m) menu.classList.remove('show'); 
    });
    const menuEl = document.getElementById(m);
    if (menuEl) menuEl.classList.toggle('show');
}

function setAsset(el) {
    if (!el) return;
    el = el.closest('.asset-item') || el;
    let a = el.getAttribute('data-asset');
    if (!a) return;
    currentAsset = a;
    const selEl = document.getElementById('selectedAsset');
    if (selEl) selEl.innerText = a;
    document.querySelectorAll('.asset-item').forEach(i => i.classList.remove('active'));
    el.classList.add('active');
    const menuEl = document.getElementById('assetMenu');
    if (menuEl) menuEl.classList.remove('show');
    const sphere = document.getElementById('mainSphere');
    if (sphere) sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
    initPriceWebSocket();
    resetSignalKey();
}

function setTf(el) {
    if (!el) return;
    el = el.closest('.tf-btn') || el;
    let tf = el.getAttribute('data-tf');
    if (!tf) return;
    currentTf = tf.toLowerCase();
    const selEl = document.getElementById('selectedTf');
    if (selEl) selEl.innerText = tf;
    document.querySelectorAll('.tf-btn').forEach(i => i.classList.remove('active'));
    el.classList.add('active');
    const menuEl = document.getElementById('tfMenu');
    if (menuEl) menuEl.classList.remove('show');
    const sphere = document.getElementById('mainSphere');
    if (sphere) sphere.classList.remove('buy-signal', 'put-signal', 'neutral-signal');
    initPriceWebSocket();
    resetSignalKey();
}

function handleGlobalInteraction(e) {
    const target = e.target;
    if (!target) return;

    const btnGet = target.closest('#btnGet');
    if (btnGet) {
        executeAnalysis();
        return;
    }

    const catBtn = target.closest('.top-cat-btn');
    if (catBtn) {
        changeTopCategory(catBtn);
        return;
    }

    const assetTrigger = target.closest('#assetBtn');
    if (assetTrigger) {
        toggleMenu('assetMenu', 'assetBtn');
        return;
    }

    const tfTrigger = target.closest('#tfBtn');
    if (tfTrigger) {
        toggleMenu('tfMenu', 'tfBtn');
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
        switchResultTab('chart');
        return;
    }

    const tabBtnAI = target.closest('#tabBtnAI');
    if (tabBtnAI) {
        switchResultTab('ai');
        return;
    }

    if (!target.closest('.selector-section') && !target.closest('.error-debug-toggle')) {
        document.querySelectorAll('.asset-menu, .tf-menu').forEach(m => m.classList.remove('show'));
    }
}

document.addEventListener('click', handleGlobalInteraction);

(function() {
    var p = new URLSearchParams(window.location.search);
    var a = p.get('asset'), t = p.get('tf');
    if (a) {
        var el = document.querySelector('.asset-item[data-asset="' + a.toUpperCase() + '"]');
        if (el) { setAsset(el); el.scrollIntoView && el.scrollIntoView({ block: 'nearest' }); }
    }
    if (t) {
        var el = document.querySelector('.tf-btn[data-tf="' + t.toUpperCase() + '"]');
        if (el) setTf(el);
    }
})();

const topCatInitial = document.querySelector('.top-cat-btn');
if (topCatInitial) changeTopCategory(topCatInitial);
syncTime();
initPriceWebSocket();

function getTfSeconds() {
    const map = { s3:3, s5:5, s10:10, s15:15, s30:30, m1:60, m2:120, m3:180, m5:300, m15:900, m30:1800, h1:3600, h4:14400, d1:86400 };
    return map[currentTf] || 60;
}

function updateCountdown() {
    const tfSec = getTfSeconds();
    const now = Math.floor((Date.now() + timeOffset) / 1000);
    const remaining = tfSec - (now % tfSec);
    const mins = Math.floor(remaining / 60);
    const secs = remaining % 60;
    const el = document.getElementById('candleTime');
    if (!el) return;
    el.innerText = `${mins}:${secs.toString().padStart(2,'0')}`;
    el.className = 'time' + (remaining <= 5 ? ' critical' : remaining <= 15 ? ' warning' : '');
}

setInterval(updateCountdown, 1000);
setTimeout(updateCountdown, 100);
