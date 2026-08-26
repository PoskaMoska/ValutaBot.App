import sqlite3
import os
import time
import requests
from datetime import datetime

# TwelveData API
# 8 requests per minute limit on free tier. We must wait 8-10 seconds between requests.
DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "models", "ValutaTicks.db")

TWELVEDATA_PAIRS = [
    "EUR/USD",
    "GBP/USD",
    "AUD/USD"
]

def get_api_key():
    return os.environ.get("TwelveDataApiKey", "")

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

def fetch_twelvedata(symbol, interval, outputsize=5000):
    api_key = get_api_key()
    if not api_key:
        print("ERROR: TwelveDataApiKey environment variable is not set.")
        return []

    url = f"https://api.twelvedata.com/time_series?symbol={symbol}&interval={interval}&outputsize={outputsize}&timezone=UTC&apikey={api_key}"
    
    try:
        response = requests.get(url, timeout=10)
        data = response.json()
        
        if data.get("status") == "error":
            print(f"API Error for {symbol}: {data.get('message')}")
            return []
            
        values = data.get("values", [])
        
        parsed_candles = []
        for v in values:
            try:
                # Format: 2024-03-12 15:30:00
                dt = datetime.strptime(v["datetime"], "%Y-%m-%d %H:%M:%S")
                timestamp = int(dt.timestamp() * 1000)
                
                parsed_candles.append((
                    symbol.replace("/", ""),
                    interval,
                    timestamp,
                    float(v["open"]),
                    float(v["high"]),
                    float(v["low"]),
                    float(v["close"]),
                    0.0 # Forex often has 0 volume on TwelveData unless requested explicitly
                ))
            except Exception as e:
                pass
                
        # TwelveData returns newest first. Reverse to chronological order.
        return parsed_candles[::-1]
    except Exception as e:
        print(f"Request failed: {e}")
        return []

def main():
    print("Starting TwelveData historical crawler...")
    conn = sq_connect()
    sq_ensure_table(conn)
    cursor = conn.cursor()

    intervals = ["1min", "5min", "15min"]

    for symbol in TWELVEDATA_PAIRS:
        for interval in intervals:
            print(f"Fetching {symbol} {interval}...")
            
            candles = fetch_twelvedata(symbol, interval)
            if not candles:
                print(f"No data returned for {symbol} {interval}. Sleeping 10s...")
                time.sleep(10)
                continue
                
            inserted = 0
            for c in candles:
                # TwelveData mapped to our internal format
                internal_interval = interval.replace("min", "m")
                
                try:
                    cursor.execute('''
                        INSERT INTO HistoricalCandles (Asset, Interval, OpenTime, Open, High, Low, Close, Volume)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    ''', (c[0], internal_interval, c[2], c[3], c[4], c[5], c[6], c[7]))
                    inserted += 1
                except sqlite3.IntegrityError:
                    pass # Already exists
            
            conn.commit()
            print(f"Inserted {inserted} new candles for {symbol} {interval}.")
            
            # Rate limit protection (8 per minute -> wait ~8.5 seconds)
            print("Waiting 8.5s for rate limit...")
            time.sleep(8.5)

    conn.close()
    print("Crawler finished successfully.")

if __name__ == "__main__":
    main()