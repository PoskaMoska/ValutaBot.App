import requests

ML_URL = "https://mlphythonservice-production.up.railway.app"

print("[1] Sync Test: force training EURUSD / 1m (checking n_train)...")
try:
    r = requests.post(
        f"{ML_URL}/train/sync",
        json={"symbol": "EURUSD", "interval": "1m"},
        timeout=120
    )
    data = r.json()
    print("EURUSD 1m:", data)
except Exception as e:
    print(e)
    
print("\n[2] Triggering background retrain for all other Forex pairs...")
for sym in ["GBPUSD", "USDJPY", "AUDUSD", "USDCHF", "USDCAD"]:
    try:
        r = requests.post(
            f"{ML_URL}/train",
            json={"symbol": sym, "interval": "1m"},
            timeout=10
        )
        print(f"Triggered {sym} 1m: {r.status_code}")
    except Exception as e:
        print(f"Error {sym}: {e}")
