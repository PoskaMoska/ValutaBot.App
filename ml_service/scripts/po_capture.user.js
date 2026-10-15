// ==UserScript==
// @name         PO price capture (read-only)
// @namespace    valutabot
// @version      1.2
// @description  Records Pocket Option price-stream WebSocket frames and sends them to a local collector (127.0.0.1:8765). Opens extra read-only price streams for the other OTC pairs. Never places trades.
// @match        https://pocketoption.com/*
// @match        https://*.pocketoption.com/*
// @run-at       document-start
// @grant        unsafeWindow
// @grant        GM_xmlhttpRequest
// @connect      127.0.0.1
// ==/UserScript==

(function () {
  'use strict';
  const COLLECTOR = 'http://127.0.0.1:8765/ingest';
  // The page itself streams whichever asset is on the chart; these get their own extra read-only streams.
  const AUX_ASSETS = ['EURUSD_otc', 'GBPUSD_otc', 'USDJPY_otc', 'AUDUSD_otc', 'USDCHF_otc', 'USDCAD_otc'];
  const DROP = new Set(['updateAssets', 'chat_room_list_update', 'counters/all/success']);
  const MAX_LEN = 20000;
  const NativeWS = unsafeWindow.WebSocket;
  let buf = [];
  let authPayload = null;   // copied from the page's own auth message (kept in memory only, never logged)
  let tradingUrl = null;
  let auxStarted = false;

  function push(rec) {
    if (buf.length < 20000) buf.push(rec);
  }

  setInterval(function () {
    if (!buf.length) return;
    const batch = buf; buf = [];
    GM_xmlhttpRequest({
      method: 'POST', url: COLLECTOR, headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify({ msgs: batch }),
      onerror: function () { /* collector not running: drop batch */ },
    });
  }, 1000);

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
    if (/events-po/.test(url)) return;           // chat/events socket: not needed
    ws._poPending = null;
    ws.addEventListener('message', function (ev) {
      const t = Date.now() + (performance.now() % 1) / 1000;   // ms epoch (fractional)
      const d = ev.data;
      if (typeof d === 'string') {
        const name = evName(d);
        if (!name) return;                        // ping/pong/handshake
        if (name === 'successauth' && !ws._aux && /po\.market/.test(url) && !auxStarted && authPayload) {
          auxStarted = true;
          setTimeout(function () {
            AUX_ASSETS.forEach(function (a, i) { setTimeout(function () { startAux(a); }, i * 1500); });
          }, 3000);
        }
        if (d.indexOf('_placeholder') !== -1) { ws._poPending = name; return; }
        if (DROP.has(name)) return;
        push({ t: t, ws: url, dir: 'in', ev: name, d: d.length > MAX_LEN ? d.slice(0, MAX_LEN) : d });
      } else {
        const name = ws._poPending; ws._poPending = null;
        if (name && DROP.has(name)) return;
        const toText = (b) => new TextDecoder('utf-8').decode(b);
        if (d instanceof ArrayBuffer) {
          push({ t: t, ws: url, dir: 'in', ev: name, d: toText(d).slice(0, MAX_LEN) });
        } else if (d && d.text) {
          d.text().then(function (s) { push({ t: t, ws: url, dir: 'in', ev: name, d: s.slice(0, MAX_LEN) }); });
        }
      }
    });
    const nativeSend = ws.send.bind(ws);
    ws.send = function (data) {
      if (typeof data === 'string') {
        const name = evName(data);
        if (name === 'auth' && !ws._aux && /po\.market/.test(url)) {
          try { 
            authPayload = JSON.parse(data.slice(data.indexOf('[')))[1]; 
            tradingUrl = url; 
            GM_xmlhttpRequest({
              method: 'POST', url: 'http://127.0.0.1:8765/save_session',
              headers: { 'Content-Type': 'application/json' },
              data: JSON.stringify({ authPayload: authPayload, tradingUrl: tradingUrl }),
            });
          } catch (e) { /* ignore */ }
        }
        if (name && name !== 'auth') push({ t: Date.now(), ws: url, dir: 'out', ev: name, d: data.slice(0, 2000) });
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
  unsafeWindow.WebSocket = WrappedWS;
})();
