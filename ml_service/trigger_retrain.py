import requests
import os

def retrain():
    pairs = ["EURUSD", "GBPUSD", "USDJPY", "EURUSD_OTC"]
    intervals = ["1m", "5m", "15m"]
    
    port = os.getenv("PORT", "8765")
    
    for pair in pairs:
        for interval in intervals:
            print(f"Triggering global retrain for {pair} {interval} on port {port}...")
            try:
                res = requests.post(f"http://localhost:{port}/train/sync", json={"asset": pair, "interval": interval}, timeout=300)
                if res.status_code == 200:
                    print(f"SUCCESS: {res.json()}")
                else:
                    print(f"FAILED: {res.status_code} - {res.text}")
            except Exception as e:
                print(f"ERROR: {e}")

if __name__ == '__main__':
    retrain()
