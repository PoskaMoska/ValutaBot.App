import os
import time
import requests
import psycopg2
from datetime import datetime

TWELVEDATA_PAIRS = ["EUR/USD", "GBP/USD", "AUD/USD", "USD/CAD", "USD/CHF", "USD/JPY"]
API_KEY = os.environ.get("TwelveDataApiKey", "")
DB_URL = os.environ.get("DATABASE_URL")

def get_latest_time(cursor, symbol):
    clean_sym = symbol.replace("/", "")
    cursor.execute("SELECT MAX(open_time) FROM historical_candles WHERE asset=%s AND interval='1m'", (clean_sym,))
    row = cursor.fetchone()
    return row[0] if row and row[0] else None

def get_earliest_time(cursor, symbol):
    clean_sym = symbol.replace("/", "")
    cursor.execute("SELECT MIN(open_time) FROM historical_candles WHERE asset=%s AND interval='1m'", (clean_sym,))
    row = cursor.fetchone()
    return row[0] if row and row[0] else None

def fetch_batch(symbol, date_str=None, mode="forward"):
    url = f"https://api.twelvedata.com/time_series?symbol={symbol}&interval=1min&outputsize=5000&timezone=UTC&apikey={API_KEY}"
    if date_str:
        if mode == "forward":
            url += f"&start_date={str(date_str).replace(' ', '%20')}"
        else:
            url += f"&end_date={str(date_str).replace(' ', '%20')}"
        
    try:
        response = requests.get(url, timeout=15)
        data = response.json()
        if "error" in data.get("status", ""):
            print(f"API Error [{symbol}]: {data.get('message')}")
            return []
            
        values = data.get("values", [])
        candles = []
        for v in values:
            try:
                dt = datetime.strptime(v["datetime"], "%Y-%m-%d %H:%M:%S")
                candles.append((
                    symbol.replace("/", ""),
                    "1m",
                    dt.strftime("%Y-%m-%d %H:%M:%S"),
                    float(v["open"]),
                    float(v["high"]),
                    float(v["low"]),
                    float(v["close"]),
                    0.0
                ))
            except Exception: pass
        return candles[::-1] # return chronological
    except Exception as e:
        print(f"HTTP Error: {e}")
        return []

def run_crawler():
    if not API_KEY or not DB_URL:
        print("ERROR: TwelveDataApiKey or DATABASE_URL is missing!")
        return
        
    conn = psycopg2.connect(DB_URL)
    cursor = conn.cursor()
    print("Starting smart backfill for 180,000 target (~6 months)...")

    for symbol in TWELVEDATA_PAIRS:
        clean_sym = symbol.replace("/", "")
        
        # 1. FORWARD SYNC (keep up to date)
        last_time = get_latest_time(cursor, symbol)
        if last_time:
            print(f"[{symbol}] Forward sync from {last_time}...")
            candles = fetch_batch(symbol, last_time, mode="forward")
            _insert_candles(conn, cursor, candles)
            if candles:
                time.sleep(12) # Rate limit padding
        
        # 2. BACKWARD SYNC (fetch deep history)
        cursor.execute("SELECT COUNT(*) FROM historical_candles WHERE asset=%s AND interval='1m'", (clean_sym,))
        total = cursor.fetchone()[0]
        
        while total < 250000:
            earliest_time = get_earliest_time(cursor, symbol)
            print(f"[{symbol}] Backward sync from {earliest_time or 'NOW'} (Total: {total}/250000)...")
            
            candles = fetch_batch(symbol, earliest_time, mode="backward")
            if not candles:
                print(f"[{symbol}] No more historical data available backwards.")
                time.sleep(12)
                break
                
            inserted = _insert_candles(conn, cursor, candles)
            if inserted == 0:
                print(f"[{symbol}] Reached end of historical data or overlap.")
                time.sleep(12)
                break
                
            cursor.execute("SELECT COUNT(*) FROM historical_candles WHERE asset=%s AND interval='1m'", (clean_sym,))
            total = cursor.fetchone()[0]
            print(f"[{symbol}] Saved {inserted} older candles. Total now {total}/250000.")
            time.sleep(12) # TwelveData 8 req/min (7.5s) limit, 12s is very safe

    conn.close()
    print("Backfill complete.")

def _insert_candles(conn, cursor, candles):
    inserted = 0
    for c in candles:
        try:
            cursor.execute("""
                INSERT INTO historical_candles (asset, interval, open_time, open, high, low, close, volume)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
                ON CONFLICT (asset, interval, open_time) DO NOTHING
            """, c)
            if cursor.rowcount > 0: inserted += 1
        except Exception:
            conn.rollback()
    conn.commit()
    return inserted

if __name__ == "__main__":
    run_crawler()
