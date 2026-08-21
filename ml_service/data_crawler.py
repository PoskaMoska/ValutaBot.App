"""
Historical Data Crawler for ValutaBot ML Global Strategist.

Downloads large historical datasets (50k-100k candles) from TwelveData
and stores them in PostgreSQL (Railway) or SQLite (local fallback).

Usage:
  python data_crawler.py --symbol EURUSD --interval 1m --candles 100000
  python data_crawler.py --symbol GBPUSD --interval 1m --candles 100000
  python data_crawler.py --symbol USDJPY --interval 1m --candles 100000
  python data_crawler.py --all --interval 1m --candles 100000
"""

import argparse
import os
import sys
import time
import math
import requests
from datetime import datetime, timedelta

TWELVE_DATA_BASE = "https://api.twelvedata.com"
TWELVE_DATA_API_KEY = os.getenv("TwelveDataApiKey") or os.getenv("TWELVE_DATA_API_KEY", "")
DATABASE_URL = os.getenv("DATABASE_URL", "")
DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "models", "ValutaTicks.db")

TD_INTERVAL_MAP = {
    "s5": "1min", "s15": "1min", "s30": "1min",
    "m1": "1min", "1m": "1min",
    "m5": "5min", "5m": "5min",
    "m15": "15min", "15m": "15min",
    "m30": "30min", "30m": "30min",
    "h1": "1h", "1h": "1h",
}

FOREX_SYMBOLS = ["EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCHF", "USDCAD"]
MAX_PER_REQUEST = 5000
RATE_LIMIT_DELAY = 16.0  # seconds between requests (4 req/min to leave room for bot)


def to_twelvedata_symbol(symbol: str) -> str:
    sym = symbol.upper()
    if sym in ["GOLD", "XAUUSD"]: return "XAU/USD"
    if sym in ["SILVER", "XAGUSD"]: return "XAG/USD"
    if len(sym) == 6:
        return f"{sym[:3]}/{sym[3:]}"
    return sym


# ── PostgreSQL backend ────────────────────────────────────────────────────────

def pg_connect():
    import psycopg2
    return psycopg2.connect(DATABASE_URL)


def pg_ensure_table(conn):
    with conn.cursor() as cur:
        cur.execute("""
            CREATE TABLE IF NOT EXISTS historical_candles (
                id        SERIAL PRIMARY KEY,
                asset     TEXT   NOT NULL,
                interval  TEXT   NOT NULL,
                open_time TEXT   NOT NULL,
                open      DOUBLE PRECISION NOT NULL,
                high      DOUBLE PRECISION NOT NULL,
                low       DOUBLE PRECISION NOT NULL,
                close     DOUBLE PRECISION NOT NULL,
                volume    DOUBLE PRECISION NOT NULL DEFAULT 0,
                UNIQUE(asset, interval, open_time)
            )
        """)
        cur.execute("""
            CREATE INDEX IF NOT EXISTS idx_hist_candles_asset
            ON historical_candles(asset, interval, open_time)
        """)
    conn.commit()


def pg_get_existing_count(conn, symbol: str, interval: str) -> int:
    with conn.cursor() as cur:
        cur.execute(
            "SELECT COUNT(*) FROM historical_candles WHERE asset=%s AND interval=%s",
            (symbol, interval)
        )
        row = cur.fetchone()
    return row[0] if row else 0


def pg_get_oldest_time(conn, symbol: str, interval: str):
    with conn.cursor() as cur:
        cur.execute(
            "SELECT MIN(open_time) FROM historical_candles WHERE asset=%s AND interval=%s",
            (symbol, interval)
        )
        row = cur.fetchone()
    return row[0] if row and row[0] else None


def pg_save_batch(conn, symbol: str, interval: str, values: list) -> int:
    inserted = 0
    with conn.cursor() as cur:
        for v in values:
            try:
                cur.execute(
                    """INSERT INTO historical_candles
                       (asset, interval, open_time, open, high, low, close, volume)
                       VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
                       ON CONFLICT (asset, interval, open_time) DO NOTHING""",
                    (
                        symbol, interval,
                        v["datetime"],
                        float(v["open"]), float(v["high"]),
                        float(v["low"]), float(v["close"]),
                        float(v.get("volume", 0) or 0),
                    )
                )
                if cur.rowcount > 0:
                    inserted += 1
            except Exception:
                pass
    conn.commit()
    return inserted


def pg_get_final_count(conn, symbol: str, interval: str) -> int:
    return pg_get_existing_count(conn, symbol, interval)


# ── SQLite backend (local fallback) ──────────────────────────────────────────

def sq_connect():
    import sqlite3
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    conn = sqlite3.connect(DB_PATH, timeout=30.0)
    return conn


def sq_ensure_table(conn):
    conn.execute("""
        CREATE TABLE IF NOT EXISTS HistoricalCandles (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            Asset     TEXT    NOT NULL,
            Interval  TEXT    NOT NULL,
            OpenTime  TEXT    NOT NULL,
            Open      REAL    NOT NULL,
            High      REAL    NOT NULL,
            Low       REAL    NOT NULL,
            Close     REAL    NOT NULL,
            Volume    REAL    NOT NULL DEFAULT 0,
            UNIQUE(Asset, Interval, OpenTime)
        )
    """)
    conn.execute("CREATE INDEX IF NOT EXISTS idx_hist_asset_interval ON HistoricalCandles(Asset, Interval, OpenTime)")
    conn.commit()


def sq_get_existing_count(conn, symbol: str, interval: str) -> int:
    import sqlite3
    row = conn.execute(
        "SELECT COUNT(*) FROM HistoricalCandles WHERE Asset=? AND Interval=?",
        (symbol, interval)
    ).fetchone()
    return row[0] if row else 0


def sq_get_oldest_time(conn, symbol: str, interval: str):
    row = conn.execute(
        "SELECT MIN(OpenTime) FROM HistoricalCandles WHERE Asset=? AND Interval=?",
        (symbol, interval)
    ).fetchone()
    return row[0] if row and row[0] else None


def sq_save_batch(conn, symbol: str, interval: str, values: list) -> int:
    inserted = 0
    for v in values:
        try:
            conn.execute(
                """INSERT OR IGNORE INTO HistoricalCandles
                   (Asset, Interval, OpenTime, Open, High, Low, Close, Volume)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    symbol, interval,
                    v["datetime"],
                    float(v["open"]), float(v["high"]),
                    float(v["low"]), float(v["close"]),
                    float(v.get("volume", 0) or 0),
                )
            )
            if conn.execute("SELECT changes()").fetchone()[0] > 0:
                inserted += 1
        except Exception:
            pass
    conn.commit()
    return inserted


def sq_get_final_count(conn, symbol: str, interval: str) -> int:
    return sq_get_existing_count(conn, symbol, interval)


# ── TwelveData fetch ──────────────────────────────────────────────────────────

def fetch_batch(td_symbol: str, td_interval: str, end_date, limit: int) -> list:
    params = {
        "symbol": td_symbol,
        "interval": td_interval,
        "outputsize": min(limit, MAX_PER_REQUEST),
        "apikey": TWELVE_DATA_API_KEY,
        "order": "DESC",
    }
    if end_date:
        params["end_date"] = end_date

    try:
        resp = requests.get(f"{TWELVE_DATA_BASE}/time_series", params=params, timeout=30)
        data = resp.json()
    except Exception as e:
        print(f"  [ERR] Request failed: {e}")
        return []

    if data.get("status") == "error":
        print(f"  [ERR] TwelveData error: {data.get('message', 'unknown')}")
        return []

    return data.get("values", [])


# ── Main crawl logic ──────────────────────────────────────────────────────────

def crawl(symbol: str, interval: str, target_candles: int):
    if not TWELVE_DATA_API_KEY:
        print("[ERR] TwelveDataApiKey not set. Cannot crawl.")
        return

    td_symbol   = to_twelvedata_symbol(symbol)
    td_interval = TD_INTERVAL_MAP.get(interval, "1min")

    # Choose backend
    use_pg = bool(DATABASE_URL)
    if use_pg:
        print(f"[Crawler] Using PostgreSQL backend (DATABASE_URL found)")
        conn = pg_connect()
        pg_ensure_table(conn)
        get_existing  = lambda: pg_get_existing_count(conn, symbol, interval)
        get_oldest    = lambda: pg_get_oldest_time(conn, symbol, interval)
        save          = lambda vals: pg_save_batch(conn, symbol, interval, vals)
        get_final     = lambda: pg_get_final_count(conn, symbol, interval)
    else:
        print(f"[Crawler] DATABASE_URL not set — using local SQLite fallback")
        conn = sq_connect()
        sq_ensure_table(conn)
        get_existing  = lambda: sq_get_existing_count(conn, symbol, interval)
        get_oldest    = lambda: sq_get_oldest_time(conn, symbol, interval)
        save          = lambda vals: sq_save_batch(conn, symbol, interval, vals)
        get_final     = lambda: sq_get_final_count(conn, symbol, interval)

    existing = get_existing()
    need     = target_candles - existing

    print(f"\n[Crawler] {symbol} ({interval}) | Target: {target_candles} | Existing: {existing} | Need: {need}")

    if need <= 0:
        print("[Crawler] Already have enough data. Skipping.")
        conn.close()
        return

    total_inserted = 0
    oldest_time    = get_oldest()
    end_date       = None

    if oldest_time:
        try:
            dt       = datetime.strptime(str(oldest_time)[:19], "%Y-%m-%d %H:%M:%S")
            end_date = (dt - timedelta(minutes=1)).strftime("%Y-%m-%d %H:%M:%S")
            print(f"[Crawler] Fetching before: {end_date}")
        except Exception:
            end_date = None

    batches = math.ceil(need / MAX_PER_REQUEST)
    print(f"[Crawler] Fetching {need} candles in ~{batches} batches (pause {RATE_LIMIT_DELAY}s)...\n")

    for batch_num in range(batches):
        remaining = need - total_inserted
        if remaining <= 0:
            break

        print(f"  Batch {batch_num+1}/{batches} | end_date={end_date or 'latest'} ...", end=" ", flush=True)
        values = fetch_batch(td_symbol, td_interval, end_date, min(remaining, MAX_PER_REQUEST))

        if not values:
            print("No data returned. Stopping.")
            break

        inserted = save(values)
        total_inserted += inserted
        print(f"Inserted: {inserted} | Total new: {total_inserted}")

        oldest_in_batch = values[-1]["datetime"]
        try:
            dt       = datetime.strptime(oldest_in_batch, "%Y-%m-%d %H:%M:%S")
            end_date = (dt - timedelta(minutes=1)).strftime("%Y-%m-%d %H:%M:%S")
        except Exception:
            break

        if len(values) < MAX_PER_REQUEST:
            print("  [INFO] No more history available from API.")
            break

        if batch_num < batches - 1:
            time.sleep(RATE_LIMIT_DELAY)

    final_count = get_final()
    conn.close()
    print(f"\n[Crawler] Done. {symbol} ({interval}): {final_count} total candles in DB.")


def main():
    parser = argparse.ArgumentParser(description="ValutaBot Historical Data Crawler")
    parser.add_argument("--symbol",   default=None, help="Single symbol (e.g. EURUSD)")
    parser.add_argument("--all",      action="store_true", help="Crawl all default forex pairs")
    parser.add_argument("--interval", default="1m", help="Timeframe (e.g. 1m, 5m)")
    parser.add_argument("--candles",  type=int, default=100000, help="Target number of candles")
    args = parser.parse_args()

    if not TWELVE_DATA_API_KEY:
        print("[ERR] Set TwelveDataApiKey environment variable first.")
        sys.exit(1)

    symbols = []
    if args.all:
        symbols = FOREX_SYMBOLS
    elif args.symbol:
        symbols = [args.symbol.upper()]
    else:
        print("[ERR] Specify --symbol EURUSD or --all")
        sys.exit(1)

    for sym in symbols:
        crawl(sym, args.interval, args.candles)
        if len(symbols) > 1:
            print(f"\n[Crawler] Waiting 15s before next symbol...\n")
            time.sleep(15)

    print("\n[Crawler] All done.")


if __name__ == "__main__":
    main()
