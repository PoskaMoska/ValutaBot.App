import requests
import time
import argparse

PAIRS = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
INTERVALS = ["1m", "s5", "s10", "s15", "s30"]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://localhost:8000")
    args = parser.parse_args()
    
    url = args.url.rstrip("/")
    print(f"Starting sequential retraining on {url}...")
    
    for pair in PAIRS:
        for interval in INTERVALS:
            for regime in ["ALL", "FLAT", "TREND", "CHAOS"]:
                print(f"\n--- Training {pair} {interval} [{regime}] ---")
                try:
                    # Limit to 100k candles to prevent Out-Of-Memory on Railway 512MB RAM
                    res = requests.post(f"{url}/train/sync", json={
                        "symbol": pair,
                        "interval": interval,
                        "regime": regime,
                        "max_candles": 100000
                    }, timeout=600)  # wait up to 10 mins for each job to finish synchronously
                    
                    if res.status_code == 200:
                        data = res.json()
                        print(f"[OK] {pair} {interval} [{regime}]: AUC = {data.get('auc', 0):.3f}")
                    else:
                        print(f"[ERROR] HTTP {res.status_code}: {res.text}")
                        
                except Exception as e:
                    print(f"[FAILED] {pair} {interval} [{regime}]: {e}")
                    
                # Sleep briefly to let the server breathe
                time.sleep(5)

if __name__ == "__main__":
    main()
