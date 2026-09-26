#!/usr/bin/env python3
"""
Запусти после деплоя чтобы переобучить все модели с правильными именами признаков.

Usage:
    python retrain_all.py --url https://your-ml-service.railway.app
    python retrain_all.py --url http://localhost:8000
"""
import sys, time, json
import urllib.request
import urllib.error

def retrain_all(base_url: str):
    base_url = base_url.rstrip("/")
    
    # Check health first
    try:
        with urllib.request.urlopen(f"{base_url}/health", timeout=5) as r:
            print(f"[OK] ML service is UP: {r.read().decode()[:100]}")
    except Exception as e:
        print(f"[ERROR] ML service unavailable at {base_url}: {e}")
        sys.exit(1)

    PAIRS    = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
    SUB_TFS  = ["s5", "s10", "s15", "s30"]
    MAIN_TFS = ["1m"]

    symbols = []
    for pair in PAIRS:
        for tf in MAIN_TFS + SUB_TFS:
            symbols.append({"symbol": pair, "interval": tf, "is_forex": True, "limit": 5000})


    print(f"\nStarting retraining of {len(symbols)} symbol/interval pairs...")
    print("This uses background /train (non-blocking). Models ready in ~10-30 min.\n")

    for s in symbols:
        body = json.dumps(s).encode()
        req = urllib.request.Request(
            f"{base_url}/train",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST"
        )
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                resp = r.read().decode()
                print(f"[QUEUED] {s['symbol']}/{s['interval']} -> {resp[:120]}")
        except urllib.error.HTTPError as e:
            print(f"[ERROR]  {s['symbol']}/{s['interval']} -> HTTP {e.code}: {e.read().decode()[:100]}")
        except Exception as e:
            print(f"[ERROR]  {s['symbol']}/{s['interval']} -> {e}")
        time.sleep(0.3)

    print("\nAll training jobs queued.")
    print("Monitor logs for: '[Train] Training on X candles. Class balance: BUY Y% | PUT Z%'")
    print("Verify fix: SHAP should show 'raw_close_5', 'rsi14' — NOT 'Column_N'")

if __name__ == "__main__":
    if "--url" in sys.argv:
        idx = sys.argv.index("--url")
        url = sys.argv[idx + 1]
    else:
        url = input("Enter ML service URL: ").strip()
    retrain_all(url)
