import threading
import time
import requests
import os
import sqlite3
import uvicorn
from main import app

def run_server():
    uvicorn.run(app, host="127.0.0.1", port=8765, log_level="warning")

server_thread = threading.Thread(target=run_server, daemon=True)
server_thread.start()

time.sleep(3) 

db_path = "data/ValutaTicks.db"
os.makedirs("data", exist_ok=True)
conn = sqlite3.connect(db_path)
cursor = conn.cursor()
cursor.execute('''
CREATE TABLE IF NOT EXISTS SubminuteCandles (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Asset TEXT NOT NULL,
    Interval TEXT NOT NULL,
    OpenTime TEXT NOT NULL,
    Open REAL, High REAL, Low REAL, Close REAL, Volume REAL
)
''')
ts_base = int(time.time()) - 3600
ts_str_base = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts_base))
for i in range(100):
    ts_c = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts_base - i*60))
    cursor.execute('''
        INSERT INTO SubminuteCandles (Asset, Interval, OpenTime, Open, High, Low, Close, Volume)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    ''', ("EURUSD", "1m", ts_c, 1.1, 1.1, 1.1, 1.1, 100))
for i in range(100):
    ts_c = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts_base - i*300))
    cursor.execute('''
        INSERT INTO SubminuteCandles (Asset, Interval, OpenTime, Open, High, Low, Close, Volume)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    ''', ("EURUSD", "5m", ts_c, 1.1, 1.1, 1.1, 1.1, 100))
conn.commit()

# Also insert into ValutaTicks just in case main.py _fetch_candles_at_entry looks there
cursor.execute('''
CREATE TABLE IF NOT EXISTS ValutaTicks (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Asset TEXT NOT NULL,
    Interval TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    Open REAL, High REAL, Low REAL, Close REAL, Volume REAL
)
''')
for i in range(100):
    ts_c = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts_base - i*60))
    cursor.execute('''
        INSERT INTO ValutaTicks (Asset, Interval, Timestamp, Open, High, Low, Close, Volume)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    ''', ("EURUSD", "1m", ts_c, 1.1, 1.1, 1.1, 1.1, 100))
for i in range(100):
    ts_c = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts_base - i*300))
    cursor.execute('''
        INSERT INTO ValutaTicks (Asset, Interval, Timestamp, Open, High, Low, Close, Volume)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    ''', ("EURUSD", "5m", ts_c, 1.1, 1.1, 1.1, 1.1, 100))
conn.commit()
conn.close()

asset = "EURUSD"
interval = "1m"
payload = {
    "asset": asset,
    "timeframe": interval,
    "entry_price": 1.1000,
    "exit_price": 1.0950,
    "direction": "BUY",
    "was_win": False,
    "timestamp": ts_str_base
}
headers = {
    "x-internal-secret": "default_secret"
}

model_path = f"models/sgd/{asset}_{interval}_sgd.pkl"
if os.path.exists(model_path):
    mtime_before = os.path.getmtime(model_path)
else:
    mtime_before = 0

requests.post("http://127.0.0.1:8765/feedback", json=payload, headers=headers)
time.sleep(4) 

if os.path.exists(model_path):
    mtime_after = os.path.getmtime(model_path)
    if mtime_after > mtime_before:
        print(f"SUCCESS: Model weights were UPDATED dynamically!")
    else:
        print(f"FAILED: File exists but was not updated")
else:
    print(f"FAILED: {model_path} still does not exist.")
