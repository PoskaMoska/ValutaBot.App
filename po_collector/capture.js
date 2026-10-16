// Injected by po_collector.py before any page script runs (read-only: never places trades).
// Wraps WebSocket, forwards price frames to Python via window.__poPush, and opens extra
// read-only price streams for the assets in window.__PO_ASSETS (copied auth from the page itself).
(function () {
  'use strict';
  const AUX_ASSETS = window.__PO_ASSETS || ['EURUSD_otc'];
  const KEEP = new Set(['updateStream', 'successauth', 'updateHistoryNewFast']);
  const NativeWS = window.WebSocket;
  let buf = [];
  let authPayload = null;
  let tradingUrl = null;
  let auxStarted = false;

  setInterval(function () {
    if (!buf.length || typeof window.__poPush !== 'function') return;
    const batch = buf; buf = [];
    try { window.__poPush(JSON.stringify(batch)); } catch (e) { /* python side gone */ }
  }, 1000);

  function push(rec) { if (buf.length < 50000) buf.push(rec); }

  function evName(text) {
    const m = /^4[25]\d*-?\["([^"]+)"/.exec(text);
    return m ? m[1] : null;
  }

  function startAux(asset) {
    if (!authPayload || !tradingUrl) return;
    let w;
    try { w = new NativeWS(tradingUrl); } catch (e) { return; }
    w.binaryType = 'arraybuffer';
    w._aux = true;
    hook(w, tradingUrl);
    w.addEventListener('message', function (ev) {
      const d = ev.data;
      if (typeof d !== 'string') return;
      if (d.indexOf('0{') === 0) w.send('40');
      else if (d.indexOf('40') === 0) w.send('42["auth",' + JSON.stringify(authPayload) + ']');
      else if (d === '2') w.send('3');
      else if (d.indexOf('successauth') !== -1 && !w._sub) {
        w._sub = true;
        w.send('42["changeSymbol",{"asset":"' + asset + '","period":60}]');
        w.send('42["subfor","' + asset + '"]');
      }
    });
    w.addEventListener('close', function () { setTimeout(function () { startAux(asset); }, 5000); });
  }

  function hook(ws, url) {
    if (/events-po/.test(url)) return;
    ws._poPending = null;
    ws.addEventListener('message', function (ev) {
      const t = Date.now();
      const d = ev.data;
      if (typeof d === 'string') {
        const name = evName(d);
        if (!name) return;
        if (name === 'successauth' && !ws._aux && /po\.market/.test(url) && !auxStarted && authPayload) {
          auxStarted = true;
          setTimeout(function () {
            AUX_ASSETS.forEach(function (a, i) { setTimeout(function () { startAux(a); }, i * 1500); });
          }, 3000);
        }
        if (d.indexOf('_placeholder') !== -1) { ws._poPending = name; return; }
        if (KEEP.has(name)) push({ t: t, ev: name, d: d.slice(0, 20000) });
      } else {
        const name = ws._poPending; ws._poPending = null;
        if (!name || !KEEP.has(name)) return;
        if (d instanceof ArrayBuffer) push({ t: t, ev: name, d: new TextDecoder('utf-8').decode(d).slice(0, 20000) });
        else if (d && d.text) d.text().then(function (s) { push({ t: t, ev: name, d: s.slice(0, 20000) }); });
      }
    });
    const nativeSend = ws.send.bind(ws);
    ws.send = function (data) {
      if (typeof data === 'string' && evName(data) === 'auth' && !ws._aux && /po\.market/.test(url)) {
        try {
          authPayload = JSON.parse(data.slice(data.indexOf('[')))[1];
          tradingUrl = url;
          window.__poAuthPayload = authPayload;
          window.__poTradingUrl = tradingUrl;
          push({ t: Date.now(), ev: 'auth_captured', authPayload: authPayload, tradingUrl: tradingUrl });
        } catch (e) { /* ignore */ }
      }
      return nativeSend(data);
    };
  }

  function WrappedWS(url, protocols) {
    const ws = protocols === undefined ? new NativeWS(url) : new NativeWS(url, protocols);
    try { hook(ws, String(url)); } catch (e) { /* never break the page */ }
    return ws;
  }
  WrappedWS.prototype = NativeWS.prototype;
  ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED'].forEach(function (k) { WrappedWS[k] = NativeWS[k]; });
  window.WebSocket = WrappedWS;
})();
