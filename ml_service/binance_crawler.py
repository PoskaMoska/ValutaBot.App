import sqlite3
import os
import time
import argparse
import requests
from datetime import datetime

# Binance API
BINANCE_BASE = "https://api.binance.com"
DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "models", "ValutaTicks.db")

# We only fetch these proxy pairs for the weekend OTC models
# Note: USDJPY proxy doesn't exist on Binance, so we skip it.
BINANCE_PAIRS = {
    "EURUSD_OTC": "EURUSDT",
    "GBPUSD_OTC": "GBPUSDT",
    "AUDUSD_OTC": "AUDUSDT"
}

def sq_connect():
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    return sqlite3.connect(DB_PATH)

def sq_ensure_table(conn):
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS HistoricalCandles (
            Asset TEXT,
            Interval TEXT,
            OpenTime INTEGER,
            Open REAL,
            High REAL,
            Low REAL,
            Close REAL,
            Volume REAL,
            PRIMARY KEY (Asset, Interval, OpenTime)
        )
    ''')
    conn.commit()

def fetch_binance_klines(symbol, interval, limit=1000, end_time=None):
    url = f"{BINANCE_BASE}/api/v3/klines"
    params = {
        "symbol": symbol,
        "interval": interval,
        "limit": limit
    }
    if end_time:
        params["endTime"] = end_time

    # FIX W-09: no backoff on 429 — a single rate-limit killed the whole crawl.
    # Added exponential backoff: up to 3 retries with 5s → 10s → 20s waits.
    for attempt in range(3):
        response = requests.get(url, params=params, timeout=10.0)
        if response.status_code == 200:
            return response.json()
        if response.status_code == 429:
            wait = 5 * (2 ** attempt)
            print(f"  [WARN] 429 rate-limit from Binance (attempt {attempt+1}/3). Waiting {wait}s...")
            time.sleep(wait)
            continue
        print(f"  [ERR] Binance error {response.status_code}: {response.text}")
        return []

    print(f"  [ERR] Binance 429 after 3 retries. Giving up on this batch.")
    return []

def crawl_binance(target_asset, binance_symbol, interval="1m", target_candles=100000):
    conn = sq_connect()
    sq_ensure_table(conn)
    cursor = conn.cursor()

    cursor.execute("SELECT COUNT(*) FROM HistoricalCandles WHERE Asset = ? AND Interval = ?", (target_asset, interval))
    existing_count = cursor.fetchone()[0]

    need = target_candles - existing_count
    print(f"[BinanceCrawler] {target_asset} ({interval}) | Target: {target_candles} | Existing: {existing_count} | Need: {max(need, 0)}")

    if need <= 0:
        print(f"[BinanceCrawler] Already have {target_candles} candles for {target_asset}, skipping.")
        conn.close()
        return

    # Start from latest if no data, or end exactly at the oldest candle we have
    cursor.execute("SELECT MIN(OpenTime) FROM HistoricalCandles WHERE Asset = ? AND Interval = ?", (target_asset, interval))
    oldest_ts = cursor.fetchone()[0]
    
    end_time = int(oldest_ts) * 1000 - 1 if oldest_ts else None # Binance expects ms

    total_inserted = 0
    batches_needed = (need // 1000) + 1
    
    for i in range(batches_needed):
        time_str = datetime.fromtimestamp(end_time / 1000).strftime("%Y-%m-%d %H:%M:%S") if end_time else "latest"
        print(f"  Batch {i+1}/{batches_needed} | end_time={time_str} ...", end="", flush=True)

        data = fetch_binance_klines(binance_symbol, interval, limit=1000, end_time=end_time)
        if not data:
            print(" No data returned. Stopping.")
            break

        rows = []
        oldest_in_batch = float('inf')
        for kline in data:
            # kline = [Open time, Open, High, Low, Close, Volume, Close time, ...]
            open_ts = int(kline[0] / 1000)
            rows.append((target_asset, interval, open_ts, float(kline[1]), float(kline[2]), float(kline[3]), float(kline[4]), float(kline[5])))
            if kline[0] < oldest_in_batch:
                oldest_in_batch = kline[0]

        try:
            cursor.executemany('''
                INSERT OR IGNORE INTO HistoricalCandles (Asset, Interval, OpenTime, Open, High, Low, Close, Volume)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            ''', rows)
            conn.commit()
            # FIX C-11: len(rows) counted ALL rows including silent INSERT OR IGNORE duplicates.
            # This made total_inserted reach the target too early, stopping the crawler
            # before actually downloading enough unique candles.
            actual_new = cursor.rowcount  # only counts rows actually inserted
            total_inserted += actual_new
            print(f" Inserted: {actual_new}/{len(rows)} new | Total new: {total_inserted}")
        except Exception as e:
            print(f" DB Error: {e}")
            break

        end_time = oldest_in_batch - 1
        time.sleep(0.5) # Binance allows up to 1200 weight per minute, this is very safe

    print(f"[BinanceCrawler] Done. {target_asset} ({interval}): fetched {total_inserted} new candles.")
    conn.close()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Download Binance history for weekend OTC models.")
    parser.add_argument("--interval", type=str, default="all", help="Specific interval (e.g. 1m) or 'all' for dynamic limits")
    parser.add_argument("--candles", type=int, default=0, help="Override candle count (0 = use dynamic limits)")
    args = parser.parse_args()

    # Dynamic limits for Time-Constant Window
    dynamic_limits = {
        "1m": 100000,
        "5m": 25000,
        "15m": 20000
    }

    for asset, binance_sym in BINANCE_PAIRS.items():
        if args.interval.lower() == "all":
            for interval, limit in dynamic_limits.items():
                target = args.candles if args.candles > 0 else limit
                crawl_binance(asset, binance_sym, interval=interval, target_candles=target)
        else:
            target = args.candles if args.candles > 0 else dynamic_limits.get(args.interval, 100000)
            crawl_binance(asset, binance_sym, interval=args.interval, target_candles=target)
    
    print("[BinanceCrawler] All done.")