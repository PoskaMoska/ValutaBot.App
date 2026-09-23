import os
import time
import requests
import psycopg2
from datetime import datetime

TWELVEDATA_PAIRS = ["EUR/USD", "GBP/USD", "AUD/USD", "USD/CAD", "USD/CHF", "USD/JPY"]
API_KEY = os.environ.get("TwelveDataApiKey", "")
DB_URL = os.environ.get("DATABASE_URL")

def get_latest_time(cursor, symbol):
    # Find the last recorded candle to avoid gaps
    clean_sym = symbol.replace("/", "")
    cursor.execute("SELECT MAX(open_time) FROM historical_candles WHERE asset=%s AND interval='1m'", (clean_sym,))
    row = cursor.fetchone()
    return row[0] if row and row[0] else None

def fetch_batch(symbol, start_date_str=None):
    url = f"https://api.twelvedata.com/time_series?symbol={symbol}&interval=1min&outputsize=5000&timezone=UTC&apikey={API_KEY}"
    if start_date_str:
        # Fetch data going forward from the last known date
        url += f"&start_date={start_date_str.replace(' ', '%20')}"
        
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
    print("Starting smart backfill for 100,000 target...")

    for symbol in TWELVEDATA_PAIRS:
        last_time = get_latest_time(cursor, symbol)
        start_str = str(last_time) if last_time else None
        
        print(f"Fetching {symbol} 1m history starting from {start_str or 'origin'}...")
        candles = fetch_batch(symbol, start_str)
        
        if not candles:
            print(f"No new data for {symbol}.")
            time.sleep(15)
            continue
            
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
        
        cursor.execute("SELECT COUNT(*) FROM historical_candles WHERE asset=%s AND interval='1m'", (symbol.replace("/", ""),))
        total = cursor.fetchone()[0]
        
        print(f"Saved {inserted} new candles. {symbol} now has {total}/100000 total candles.")
        print("Waiting 15s for rate limits...")
        time.sleep(15)

    conn.close()
    print("Backfill complete.")

if __name__ == "__main__":
    run_crawler()
