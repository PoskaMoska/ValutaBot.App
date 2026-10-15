"""
po_collector_ws.py - Direct WebSocket Pocket Option OTC Collector.
Lightweight, resilient, zero-browser. Runs locally or on Railway.
Streams quotes for EURUSD_otc, GBPUSD_otc, USDJPY_otc, AUDUSD_otc, USDCAD_otc, USDCHF_otc.
Inserts directly into po_ticks and updates po_collector_state.
"""
import os
import sys
import json
import time
import signal
import asyncio
import datetime as dt
from pathlib import Path
import psycopg2
from psycopg2.extras import execute_batch
import websockets

DEFAULT_ASSETS = ["EURUSD_otc", "GBPUSD_otc", "USDJPY_otc", "AUDUSD_otc", "USDCAD_otc", "USDCHF_otc"]
DEFAULT_DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"
SRV_OFFSET = float(os.environ.get("SRV_OFFSET", "7200.0"))

def log(msg):
    print(f"{dt.datetime.now(dt.timezone.utc):%Y-%m-%d %H:%M:%S}Z  {msg}", flush=True)

class Collector:
    def __init__(self, db_url):
        self.db_url = db_url
        self.conn = None
        self.pending_ticks = []
        self.total_ticks = 0
        self.per_asset = {}
        self.last_tick_time = 0.0
        self._init_db()

    def _init_db(self):
        try:
            self.conn = psycopg2.connect(self.db_url)
            self.conn.autocommit = True
            with self.conn.cursor() as cur:
                cur.execute("""CREATE TABLE IF NOT EXISTS po_ticks (
                                asset   text             NOT NULL,
                                ts      double precision NOT NULL,
                                price   double precision NOT NULL,
                                recv_ms bigint           NOT NULL,
                                PRIMARY KEY (asset, ts))""")
                cur.execute("""CREATE TABLE IF NOT EXISTS po_collector_state (
                                id int PRIMARY KEY, state text, updated_at timestamptz,
                                last_tick_at timestamptz, status text)""")
            log("PostgreSQL connection established.")
        except Exception as e:
            log(f"DB init warning: {e}")

    def get_auth_state(self):
        try:
            if not self.conn or self.conn.closed:
                self._init_db()
            with self.conn.cursor() as cur:
                cur.execute("SELECT state FROM po_collector_state WHERE id = 1;")
                row = cur.fetchone()
                if row and row[0]:
                    st = json.loads(row[0]) if isinstance(row[0], str) else row[0]
                    return st.get("authPayload"), st.get("tradingUrl")
        except Exception as e:
            log(f"Error fetching state: {e}")
        return None, None

    def update_status(self, status):
        try:
            if not self.conn or self.conn.closed:
                self._init_db()
            last_dt = dt.datetime.fromtimestamp(self.last_tick_time, dt.timezone.utc) if self.last_tick_time else None
            with self.conn.cursor() as cur:
                cur.execute("""INSERT INTO po_collector_state (id, last_tick_at, status)
                               VALUES (1, %s, %s)
                               ON CONFLICT (id) DO UPDATE SET last_tick_at = EXCLUDED.last_tick_at, status = EXCLUDED.status""",
                            (last_dt, status))
        except Exception as e:
            pass

    def add_tick(self, asset, srv_ts, price, recv_ms):
        utc_ts = float(srv_ts) - SRV_OFFSET
        self.pending_ticks.append((asset, utc_ts, float(price), int(recv_ms)))
        self.per_asset[asset] = self.per_asset.get(asset, 0) + 1
        self.total_ticks += 1
        self.last_tick_time = time.time()

    def flush(self):
        if not self.pending_ticks:
            return
        batch = self.pending_ticks
        self.pending_ticks = []
        try:
            if not self.conn or self.conn.closed:
                self._init_db()
            with self.conn.cursor() as cur:
                execute_batch(cur, """
                    INSERT INTO po_ticks (asset, ts, price, recv_ms)
                    VALUES (%s, %s, %s, %s)
                    ON CONFLICT (asset, ts) DO NOTHING;
                """, batch, page_size=1000)
        except Exception as e:
            log(f"DB flush error ({e.__class__.__name__}): {e}; re-queueing {len(batch)} ticks")
            self.pending_ticks = batch + self.pending_ticks
            try:
                self._init_db()
            except Exception:
                pass

async def run_collector():
    db_url = os.environ.get("DATABASE_URL", DEFAULT_DB_URL)
    collector = Collector(db_url)
    
    stop_event = asyncio.Event()
    for s in (signal.SIGINT, signal.SIGTERM):
        try:
            asyncio.get_running_loop().add_signal_handler(s, stop_event.set)
        except NotImplementedError:
            pass

    log("Starting Pocket Option Direct WebSocket Collector...")
    backoff = 5
    
    while not stop_event.is_set():
        auth_payload, trading_url = collector.get_auth_state()
        if not auth_payload or not trading_url:
            log("No session found in DB po_collector_state. Waiting 10s...")
            collector.update_status("WAITING_SESSION")
            await asyncio.sleep(10)
            continue

        collector.update_status("CONNECTING")
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
        }
        
        try:
            log(f"Connecting to {trading_url}...")
            async with websockets.connect(trading_url, origin="https://pocketoption.com", additional_headers=headers, ping_interval=None) as ws:
                # Handshake step 1: recv '0{"sid":...}'
                msg = await asyncio.wait_for(ws.recv(), timeout=15)
                # Handshake step 2: send '40'
                await ws.send("40")
                # Handshake step 2b: recv ACK '40{"sid":...}'
                ack = await asyncio.wait_for(ws.recv(), timeout=15)
                # Handshake step 3: send auth
                await ws.send("42" + json.dumps(["auth", auth_payload]))
                log("Authentication sent. Waiting for stream confirmation...")

                authenticated = False
                subscribed = False
                pending_event = None
                last_flush = time.time()
                last_report = time.time()
                backoff = 5 # reset backoff on success

                while not stop_event.is_set():
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=20)
                    except asyncio.TimeoutError:
                        log("WebSocket read timeout (20s) -> sending ping 2")
                        await ws.send("2")
                        continue

                    recv_ms = int(time.time() * 1000)

                    # Binary frame handling (Socket.IO attachments)
                    if isinstance(raw, bytes):
                        if pending_event == "updateStream":
                            try:
                                rows = json.loads(raw.decode("utf-8", errors="replace"))
                                for asset, srv_ts, price in rows:
                                    collector.add_tick(asset, srv_ts, price, recv_ms)
                            except Exception:
                                pass
                        elif pending_event == "updateHistoryNewFast":
                            try:
                                data = json.loads(raw.decode("utf-8", errors="replace"))
                                asset = data.get("asset")
                                for srv_ts, price in data.get("history", []):
                                    collector.add_tick(asset, srv_ts, price, recv_ms)
                                log(f"Received history for {asset}: {len(data.get('history', []))} ticks")
                            except Exception:
                                pass
                        pending_event = None
                        continue

                    # Text frame handling
                    if raw == "2":
                        await ws.send("3")
                        continue
                    elif raw == "3":
                        continue

                    # Parse Socket.IO packet type
                    if raw.startswith("451-"):
                        # Binary attachment placeholder
                        try:
                            meta = json.loads(raw[4:])
                            if isinstance(meta, list) and len(meta) > 0:
                                pending_event = meta[0]
                        except Exception:
                            pending_event = None
                    elif raw.startswith("42"):
                        try:
                            data = json.loads(raw[2:])
                            ev = data[0] if isinstance(data, list) and len(data) > 0 else ""
                            if ev == "updateStream" and len(data) > 1:
                                rows = data[1] if isinstance(data[1], list) else json.loads(data[1])
                                for asset, srv_ts, price in rows:
                                    collector.add_tick(asset, srv_ts, price, recv_ms)
                        except Exception:
                            pass

                    # Check for auth success
                    if ("successauth" in raw or "updateAssets" in raw) and not subscribed:
                        authenticated = True
                        subscribed = True
                        collector.update_status("ONLINE")
                        log("Authenticated successfully! Subscribing to OTC assets...")
                        assets = [a.strip() for a in os.environ.get("PO_ASSETS", ",".join(DEFAULT_ASSETS)).split(",") if a.strip()]
                        for a in assets:
                            await ws.send("42" + json.dumps(["changeSymbol", {"asset": a, "period": 60}]))
                            await ws.send("42" + json.dumps(["subfor", a]))
                        log(f"Subscribed to: {', '.join(assets)}")

                    # Periodic flush and stats
                    now = time.time()
                    if now - last_flush >= 3.0:
                        collector.flush()
                        last_flush = now

                    if now - last_report >= 30.0:
                        status_str = "  ".join(f"{k}={v}" for k, v in sorted(collector.per_asset.items()))
                        log(f"TOTAL={collector.total_ticks:,} ticks | {status_str}")
                        collector.update_status("ONLINE")
                        last_report = now

        except (websockets.exceptions.ConnectionClosed, Exception) as e:
            collector.flush()
            collector.update_status("RECONNECTING")
            log(f"Connection closed or error: {e.__class__.__name__}: {str(e)[:200]}")
            log(f"Reconnecting in {backoff} seconds...")
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 60)

    collector.flush()
    collector.update_status("STOPPED")
    log("Collector stopped.")

if __name__ == "__main__":
    try:
        asyncio.run(run_collector())
    except KeyboardInterrupt:
        log("Interrupted by user.")
