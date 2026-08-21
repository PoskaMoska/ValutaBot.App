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

    response = requests.get(url, params=params, timeout=10.0)
    if response.status_code != 200:
        print(f"  [ERR] Binance error: {response.text}")
        return []
    
    return response.json()

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
    
    end_time = oldest_ts * 1000 - 1 if oldest_ts else None # Binance expects ms

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
            total_inserted += len(rows)
            print(f" Inserted: {len(rows)} | Total new: {total_inserted}")
        except Exception as e:
            print(f" DB Error: {e}")
            break

        end_time = oldest_in_batch - 1
        time.sleep(0.5) # Binance allows up to 1200 weight per minute, this is very safe

    print(f"[BinanceCrawler] Done. {target_asset} ({interval}): fetched {total_inserted} new candles.")
    conn.close()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Download Binance history for weekend OTC models.")
    parser.add_argument("--interval", type=str, default="1m")
    parser.add_argument("--candles", type=int, default=100000)
    args = parser.parse_args()

    for asset, binance_sym in BINANCE_PAIRS.items():
        crawl_binance(asset, binance_sym, args.interval, args.candles)
    
    print("[BinanceCrawler] All done.")