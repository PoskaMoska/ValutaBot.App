"""
retrain_all.py v3 — poll-based sequential retraining.
Avoids Railway HTTP idle timeout by using /train (non-blocking) + polling /health.
"""
import requests
import time
import argparse

PAIRS     = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
INTERVALS = ["1m", "s5", "s10", "s15", "s30"]
REGIMES   = ["ALL", "FLAT", "TREND", "CHAOS"]

def get_trained_keys(url):
    try:
        r = requests.get(f"{url}/health", timeout=15).json()
        return {m["key"] for m in r.get("models", []) if m.get("status") != "not-trained"}
    except Exception as e:
        print(f"  [WARN] /health failed: {e}")
        return set()

def queue_one(url, symbol, interval, regime):
    """Queue a single model for background training."""
    try:
        r = requests.post(f"{url}/train", json={
            "symbol": symbol,
            "interval": interval,
            "regime": regime,
        }, timeout=30)
        return r.status_code == 200
    except Exception as e:
        print(f"  [ERROR] POST /train failed: {e}")
        return False

def wait_for_key(url, key, timeout_min=15):
    """Poll /health until 'key' appears as trained."""
    deadline = time.time() + timeout_min * 60
    dots = 0
    while time.time() < deadline:
        trained = get_trained_keys(url)
        if key in trained:
            return True
        time.sleep(20)
        dots += 1
        if dots % 3 == 0:
            print(f"  ... waiting ({dots*20}s)")
    return False

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://localhost:8000")
    parser.add_argument("--skip-trained", action="store_true", default=True,
                        help="Skip models already in /health (default: True)")
    args = parser.parse_args()

    url = args.url.rstrip("/")
    print(f"=== Sequential Retrain v3 (poll-based) ===")
    print(f"Target: {url}")
    print(f"Total jobs: {len(PAIRS)} pairs x {len(INTERVALS)} intervals x {len(REGIMES)} regimes = "
          f"{len(PAIRS)*len(INTERVALS)*len(REGIMES)} models\n")

    already_trained = get_trained_keys(url)
    print(f"Already trained: {len(already_trained)} models — will skip those.\n")

    total = 0
    done  = 0
    skipped = 0

    for pair in PAIRS:
        for interval in INTERVALS:
            for regime in REGIMES:
                total += 1
                # Key format matches what /health returns
                suffix = "" if regime == "ALL" else f"_{regime}"
                key = f"{pair}_{interval}{suffix}"

                if key in already_trained:
                    skipped += 1
                    print(f"[SKIP] {key} — already trained")
                    continue

                print(f"\n[{done+1}/?] Queuing: {key}")
                ok = queue_one(url, pair, interval, regime)
                if not ok:
                    print(f"  [FAIL] Could not queue {key}")
                    continue

                print(f"  Waiting for {key} to appear in /health (max 15 min)...")
                found = wait_for_key(url, key, timeout_min=15)

                if found:
                    done += 1
                    print(f"  [OK] {key} trained! ({done} done so far)")
                else:
                    print(f"  [TIMEOUT] {key} not trained within 15 min — skipping")

                # Brief pause before next job
                time.sleep(5)

    print(f"\n=== DONE ===")
    print(f"Trained: {done} | Skipped (already existed): {skipped} | Failed/Timeout: {total-done-skipped}")

if __name__ == "__main__":
    main()
