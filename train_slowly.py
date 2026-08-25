import requests
import time

ML_URL = "https://mlphythonservice-production.up.railway.app"

pairs = ["GBPUSD", "USDJPY", "AUDUSD", "USDCHF", "USDCAD"]

for sym in pairs:
    print(f"[{sym}] Triggering sync retrain (will block until done)...")
    try:
        r = requests.post(
            f"{ML_URL}/train/sync",
            json={"symbol": sym, "interval": "1m"},
            timeout=180
        )
        data = r.json()
        print(f"[{sym}] Success: n_train={data.get('n_train')}, acc={data.get('accuracy')}")
    except Exception as e:
        print(f"[{sym}] ERROR: {e}")
        
    print("Waiting 15 seconds to free RAM...")
    time.sleep(15)

print("All done smoothly!")
