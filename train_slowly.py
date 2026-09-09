import requests
import time

ML_URL = "https://mlphythonservice-production.up.railway.app"

pairs = ["EURUSD", "GBPUSD", "USDJPY", "EURUSD_OTC", "AUDUSD", "USDCHF", "USDCAD"]
intervals = ["1m", "5m", "15m"]

for sym in pairs:
    for tf in intervals:
        print(f"[{sym} {tf}] Triggering sync retrain (will block until done)...")
        try:
            r = requests.post(
                f"{ML_URL}/train/sync",
                json={"symbol": sym, "interval": tf},
                timeout=180
            )
            data = r.json()
            print(f"[{sym} {tf}] Success: n_train={data.get('n_train')}, acc={data.get('accuracy')}")
        except Exception as e:
            print(f"[{sym} {tf}] ERROR: {e}")
            
        print("Waiting 15 seconds to free RAM...")
        time.sleep(15)

print("All done smoothly!")
