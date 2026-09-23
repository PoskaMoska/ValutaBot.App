import requests
import json
import time
import concurrent.futures

BASE_URL = "http://localhost:8765"

def test_predict(symbol, tf):
    # Dummy candles
    candles = [
        {"open": 1.1, "high": 1.105, "low": 1.095, "close": 1.1, "volume": 100}
    ] * 200
    
    payload = {
        "symbol": symbol,
        "interval": tf,
        "candles": candles,
        "is_forex": "OTC" in symbol or len(symbol) == 6
    }
    
    start = time.time()
    r = requests.post(f"{BASE_URL}/predict", json=payload, timeout=5)
    elapsed = time.time() - start
    
    return r.status_code, r.json() if r.status_code == 200 else r.text, elapsed

def test_feedback(symbol, tf):
    payload = {
        "asset": symbol,
        "timeframe": tf,
        "entry_price": 1.1000,
        "exit_price": 1.1050,
        "direction": "BUY",
        "was_win": True,
        "timestamp": "2026-08-22T19:00:00Z"
    }
    r = requests.post(f"{BASE_URL}/feedback", json=payload, timeout=5)
    return r.status_code, r.json() if r.status_code == 200 else r.text

print("\n--- Testing OTC Predict ---")
sc, data, ms = test_predict("EURUSD_OTC", "s5")
print(f"Status: {sc}, Time: {ms*1000:.0f}ms")
print(f"Direction: {data.get('direction', 'ERROR')}, Confidence: {data.get('confidence', 0)}")

print("\n--- Testing SGD Feedback ---")
sc, data = test_feedback("EURUSD", "s5")
print(f"Status: {sc}")
print(f"Response: {data}")

print("\n--- Testing Concurrency (30 parallel predicts) ---")
with concurrent.futures.ThreadPoolExecutor(max_workers=30) as executor:
    futures = [executor.submit(test_predict, "EURUSD", "s5") for _ in range(30)]
    results = [f.result() for f in concurrent.futures.as_completed(futures)]
    
successes = sum(1 for r in results if r[0] == 200)
avg_time = sum(r[2] for r in results) / len(results)
print(f"Success: {successes}/30, Avg Time: {avg_time*1000:.0f}ms")
if successes < 30:
    print("Example error: ", [r[1] for r in results if r[0] != 200][:1])

