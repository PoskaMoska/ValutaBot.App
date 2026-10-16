"""
po_collector.py - autonomous Pocket Option OTC price collector (headless Chromium via Playwright).

Read-only: it opens the DEMO trading page with a saved login session, captures the price stream
(the same WebSocket frames the chart uses) for several OTC assets and stores every tick in Postgres
(table po_ticks) and, optionally, in local CSV files. It never places trades.

Modes
  1) One-time login on your PC (opens a visible browser; log in by hand, incl. captcha/2FA):
         py -3 po_collector.py --login
     -> saves the session to po_state.json and (if DATABASE_URL is set) to the DB, so the
        Railway instance picks it up automatically.
  2) Collect (locally or on Railway):
         py -3 po_collector.py [--headed] [--csv-dir DIR]

Env
  DATABASE_URL        Postgres (required on Railway)
  PO_ASSETS           comma list, default: EURUSD_otc,GBPUSD_otc,USDJPY_otc,AUDUSD_otc,USDCHF_otc,USDCAD_otc
  PO_URL              trading page, default https://pocketoption.com/ru/cabinet/demo-quick-high-low/
  PO_STATE_B64        optional: base64 of po_state.json (fallback if the DB has no session)
  SRV_OFFSET          server clock offset in seconds (default 7200: PO server time is UTC+2)

Session cookies are stored only in po_state.json / table po_collector_state and are never printed.
"""
import os
import sys
import json
import time
import base64
import signal
import asyncio
import argparse
import datetime as dt
from pathlib import Path

from playwright.async_api import async_playwright

HERE = Path(__file__).resolve().parent
STATE_FILE = HERE / "po_state.json"
CAPTURE_JS = (HERE / "capture.js").read_text(encoding="utf-8")
DEFAULT_ASSETS = "EURUSD_otc,GBPUSD_otc,USDJPY_otc,AUDUSD_otc,USDCHF_otc,USDCAD_otc"
LOGIN_URL = "https://pocketoption.com/ru/login/"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36")

STALE_SEC = 90            # no ticks for this long -> reload the page
STATE_SAVE_SEC = 1800     # persist refreshed cookies every 30 min
FLUSH_SEC = 5             # DB insert batch interval


def log(msg):
    print(f"{dt.datetime.now(dt.timezone.utc):%Y-%m-%d %H:%M:%S}Z  {msg}", flush=True)


# ----------------------------------------------------------------------------- storage
class Store:
    def __init__(self, db_url, csv_dir, srv_offset):
        self.db_url = db_url
        self.csv_dir = Path(csv_dir) if csv_dir else None
        self.srv_offset = srv_offset
        self.pending = []
        self.conn = None
        self.total = 0
        self.per_asset = {}
        self.last_tick = 0.0
        self.auth_payload = None
        self.trading_url = None
        if self.db_url:
            self._connect()

    def _connect(self):
        import psycopg2
        self.conn = psycopg2.connect(self.db_url)
        self.conn.autocommit = True
        with self.conn.cursor() as c:
            c.execute("""CREATE TABLE IF NOT EXISTS po_ticks (
                            asset   text             NOT NULL,
                            ts      double precision NOT NULL,   -- UTC epoch seconds (server time - offset)
                            price   double precision NOT NULL,
                            recv_ms bigint           NOT NULL,
                            PRIMARY KEY (asset, ts))""")
            c.execute("""CREATE TABLE IF NOT EXISTS po_collector_state (
                            id int PRIMARY KEY, state text, updated_at timestamptz,
                            last_tick_at timestamptz, status text)""")

    def load_state(self):
        if not self.conn:
            return None
        with self.conn.cursor() as c:
            c.execute("SELECT state FROM po_collector_state WHERE id=1")
            r = c.fetchone()
        st = json.loads(r[0]) if r and r[0] else None
        if isinstance(st, dict):
            if st.get("authPayload") and not self.auth_payload:
                self.auth_payload = st["authPayload"]
            if st.get("tradingUrl") and not self.trading_url:
                self.trading_url = st["tradingUrl"]
        return st

    def merge_state(self, state, auth_payload=None, trading_url=None):
        merged = dict(state) if isinstance(state, dict) else {}
        existing = None
        if self.conn:
            try:
                with self.conn.cursor() as c:
                    c.execute("SELECT state FROM po_collector_state WHERE id=1")
                    r = c.fetchone()
                existing = json.loads(r[0]) if r and r[0] else None
            except Exception:
                existing = None
        if isinstance(existing, dict):
            if "authPayload" in existing and "authPayload" not in merged:
                merged["authPayload"] = existing["authPayload"]
            if "tradingUrl" in existing and "tradingUrl" not in merged:
                merged["tradingUrl"] = existing["tradingUrl"]
        if self.auth_payload and "authPayload" not in merged:
            merged["authPayload"] = self.auth_payload
        if self.trading_url and "tradingUrl" not in merged:
            merged["tradingUrl"] = self.trading_url
        if auth_payload:
            merged["authPayload"] = auth_payload
            self.auth_payload = auth_payload
        if trading_url:
            merged["tradingUrl"] = trading_url
            self.trading_url = trading_url
        return merged

    def save_state(self, state, auth_payload=None, trading_url=None):
        merged = self.merge_state(state, auth_payload, trading_url)
        if not self.conn:
            return merged
        with self.conn.cursor() as c:
            c.execute("""INSERT INTO po_collector_state (id, state, updated_at) VALUES (1, %s, now())
                         ON CONFLICT (id) DO UPDATE SET state=EXCLUDED.state, updated_at=now()""",
                      (json.dumps(merged),))
        return merged

    def heartbeat(self, status):
        if not self.conn:
            return
        last = dt.datetime.fromtimestamp(self.last_tick, dt.timezone.utc) if self.last_tick else None
        with self.conn.cursor() as c:
            c.execute("""INSERT INTO po_collector_state (id, last_tick_at, status) VALUES (1, %s, %s)
                         ON CONFLICT (id) DO UPDATE SET last_tick_at=EXCLUDED.last_tick_at, status=EXCLUDED.status""",
                      (last, status))

    def add_batch(self, raw):
        try:
            msgs = json.loads(raw)
        except Exception:
            return
        for m in msgs:
            ev = m.get("ev")
            if ev == "auth_captured":
                if m.get("authPayload"):
                    self.auth_payload = m["authPayload"]
                if m.get("tradingUrl"):
                    self.trading_url = m["tradingUrl"]
                continue
            if ev != "updateStream":
                continue
            try:
                rows = json.loads(m["d"])
            except Exception:
                continue
            for asset, srv_ts, price in rows:
                self.pending.append((asset, float(srv_ts) - self.srv_offset, float(price), int(m["t"])))
                self.per_asset[asset] = self.per_asset.get(asset, 0) + 1
                self.total += 1
                self.last_tick = time.time()

    def flush(self):
        if not self.pending:
            return
        rows, self.pending = self.pending, []
        if self.csv_dir:
            self.csv_dir.mkdir(parents=True, exist_ok=True)
            day = dt.datetime.utcnow().date().isoformat()
            with open(self.csv_dir / f"ticks_{day}.csv", "a", encoding="utf-8") as fh:
                for a, ts, p, r in rows:
                    fh.write(f"{r},{a},{ts + self.srv_offset:.3f},{p}\n")   # same format as the userscript collector
        if self.conn:
            from psycopg2.extras import execute_values
            try:
                with self.conn.cursor() as c:
                    execute_values(c, "INSERT INTO po_ticks (asset, ts, price, recv_ms) VALUES %s "
                                      "ON CONFLICT DO NOTHING", rows, page_size=1000)
            except Exception as e:
                log(f"DB insert failed ({e.__class__.__name__}); reconnecting, {len(rows)} ticks re-queued")
                self.pending = rows + self.pending
                try:
                    self._connect()
                except Exception:
                    pass


# ----------------------------------------------------------------------------- browser
async def new_context(pw, headed, state):
    # Playwright storage_state only accepts cookies/origins; strip extra keys like authPayload/tradingUrl
    pw_state = None
    if isinstance(state, dict) and ("cookies" in state or "origins" in state):
        pw_state = {"cookies": state.get("cookies", []), "origins": state.get("origins", [])}
    browser = await pw.chromium.launch(
        headless=not headed,
        args=["--disable-blink-features=AutomationControlled", "--no-sandbox", "--disable-dev-shm-usage"])
    ctx = await browser.new_context(
        storage_state=pw_state, user_agent=UA, locale="ru-RU", viewport={"width": 1366, "height": 768})
    await ctx.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined});")
    return browser, ctx


async def do_login(args, store):
    trading_page_url = os.environ.get("PO_URL", "https://pocketoption.com/ru/cabinet/demo-quick-high-low/")
    async with async_playwright() as pw:
        browser, ctx = await new_context(pw, True, None)
        await ctx.add_init_script(CAPTURE_JS)
        page = await ctx.new_page()
        try:
            await page.goto(LOGIN_URL, wait_until="commit", timeout=120000)
        except Exception as e:
            log(f"Login page is slow to open ({e.__class__.__name__}); keep the window open and wait / reload it by hand")
        log("Log in to Pocket Option in the opened window (you have 10 minutes)...")
        deadline = time.time() + 600
        while time.time() < deadline:
            if "/cabinet" in page.url:
                break
            await asyncio.sleep(2)
        else:
            log("Login not detected, aborting."); await browser.close(); return 1

        # Wait or navigate to trading page so capture.js intercepts the WebSocket 42["auth",...] frame
        if "demo" not in page.url and "quick-high-low" not in page.url:
            try:
                await page.goto(trading_page_url, wait_until="domcontentloaded", timeout=60000)
            except Exception:
                pass

        captured_auth = None
        captured_url = None
        for _ in range(30):
            try:
                res = await page.evaluate(
                    "() => ({ authPayload: window.__poAuthPayload || null, tradingUrl: window.__poTradingUrl || null })"
                )
                if res and res.get("authPayload"):
                    captured_auth = res["authPayload"]
                    captured_url = res.get("tradingUrl")
                    log(f"Captured WebSocket authPayload (uid={captured_auth.get('uid')}) from {captured_url}")
                    break
            except Exception:
                pass
            await asyncio.sleep(0.5)

        state = await ctx.storage_state()
        merged = store.save_state(state, captured_auth, captured_url)
        STATE_FILE.write_text(json.dumps(merged), encoding="utf-8")
        log(f"Session saved to {STATE_FILE.name}" + (" and to the database" if store.conn else ""))
        await browser.close()
    return 0


def load_initial_state(store):
    st = None
    try:
        st = store.load_state()
        if st:
            log("Using session from the database")
    except Exception as e:
        log(f"Could not read session from DB: {e.__class__.__name__}")
    if not st and os.environ.get("PO_STATE_B64"):
        st = json.loads(base64.b64decode(os.environ["PO_STATE_B64"]))
        log("Using session from PO_STATE_B64")
    if not st and STATE_FILE.exists():
        st = json.loads(STATE_FILE.read_text(encoding="utf-8"))
        log(f"Using session from {STATE_FILE.name}")
    return st


async def collect(args, store):
    assets = [a.strip() for a in os.environ.get("PO_ASSETS", DEFAULT_ASSETS).split(",") if a.strip()]
    url = os.environ.get("PO_URL", "https://pocketoption.com/ru/cabinet/demo-quick-high-low/")
    state = load_initial_state(store)
    if not state:
        log("No saved session. Run `po_collector.py --login` on your PC first."); return 2

    stop = asyncio.Event()
    for s in (signal.SIGINT, signal.SIGTERM):
        try:
            asyncio.get_running_loop().add_signal_handler(s, stop.set)
        except NotImplementedError:
            pass   # Windows

    async with async_playwright() as pw:
        backoff = 10
        while not stop.is_set():
            browser = None
            try:
                browser, ctx = await new_context(pw, args.headed, state)
                await ctx.expose_function("__poPush", store.add_batch)
                await ctx.add_init_script(f"window.__PO_ASSETS = {json.dumps(assets)};\n" + CAPTURE_JS)
                page = await ctx.new_page()
                log(f"Opening trading page, assets: {', '.join(assets)}")
                await page.goto(url, wait_until="domcontentloaded", timeout=90000)
                await asyncio.sleep(10)
                if "/login" in page.url or "/cabinet" not in page.url:
                    store.heartbeat("LOGIN_REQUIRED")
                    log("SESSION EXPIRED: Pocket Option asks to log in again. Run `po_collector.py --login` "
                        "on your PC; the collector retries every 5 min.")
                    await browser.close(); browser = None
                    try:
                        await asyncio.wait_for(stop.wait(), timeout=300)
                    except asyncio.TimeoutError:
                        pass
                    state = load_initial_state(store) or state
                    continue

                started = time.time(); store.last_tick = 0
                last_flush = last_report = last_state = time.time()
                while not stop.is_set():
                    await asyncio.sleep(1)
                    now = time.time()
                    if now - last_flush >= FLUSH_SEC:
                        store.flush(); last_flush = now
                    ref = store.last_tick or started
                    if now - ref > STALE_SEC:
                        log(f"No ticks for {int(now - ref)} s -> restarting browser")
                        store.heartbeat("STALE")
                        break
                    if now - last_report >= 60:
                        store.heartbeat("OK" if store.last_tick else "WAITING")
                        log(f"ticks total={store.total:,}  " +
                            "  ".join(f"{a}={n}" for a, n in sorted(store.per_asset.items())))
                        last_report = now
                    if now - last_state >= STATE_SAVE_SEC:
                        state = store.save_state(await ctx.storage_state())
                        if STATE_FILE.parent.exists() and not os.environ.get("RAILWAY_ENVIRONMENT"):
                            STATE_FILE.write_text(json.dumps(state), encoding="utf-8")
                        last_state = now
                backoff = 10
            except Exception as e:
                log(f"Browser error: {e.__class__.__name__}: {str(e)[:200]}  (retry in {backoff}s)")
                await asyncio.sleep(backoff); backoff = min(backoff * 2, 300)
            finally:
                store.flush()
                if browser:
                    try:
                        await browser.close()
                    except Exception:
                        pass
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--login", action="store_true", help="open a visible browser to log in and save the session")
    ap.add_argument("--headed", action="store_true", help="show the browser while collecting (debug)")
    ap.add_argument("--csv-dir", default=os.environ.get("PO_CSV_DIR"), help="also append ticks to CSV files here")
    ap.add_argument("--no-db", action="store_true", help="do not use DATABASE_URL")
    args = ap.parse_args()
    db_url = None if args.no_db else os.environ.get("DATABASE_URL")
    store = Store(db_url, args.csv_dir, float(os.environ.get("SRV_OFFSET", "7200")))
    log(f"storage: {'Postgres' if store.conn else 'no DB'}" + (f", CSV -> {args.csv_dir}" if args.csv_dir else ""))
    sys.exit(asyncio.run(do_login(args, store) if args.login else collect(args, store)))


if __name__ == "__main__":
    main()
