# -*- coding: utf-8 -*-
"""
local_train_all.py - full local model training and upload to Railway.

Usage:
    cd ml_service
    py ../local_train_all.py [--upload] [--pairs EURUSD GBPUSD ...] [--intervals s5 1m ...]
"""
import sys
# Force UTF-8 output on Windows
if sys.stdout.encoding != 'utf-8':
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace', line_buffering=True)

import os, time, argparse, pathlib

PAIRS     = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
INTERVALS = ["1m", "s5", "s10", "s15", "s30"]
REGIMES   = ["ALL", "FLAT", "TREND", "CHAOS"]

RAILWAY_ML_URL = "https://mlphythonservice-production.up.railway.app"
DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

SCRIPT_DIR = pathlib.Path(__file__).parent
ML_DIR     = SCRIPT_DIR / "ml_service"
MODEL_DIR  = ML_DIR / "data" / "models"

if str(ML_DIR) not in sys.path:
    sys.path.insert(0, str(ML_DIR))


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--pairs",       nargs="+", default=PAIRS)
    p.add_argument("--intervals",   nargs="+", default=INTERVALS)
    p.add_argument("--regimes",     nargs="+", default=REGIMES)
    p.add_argument("--upload",      action="store_true", help="Upload to Railway after training")
    p.add_argument("--upload-only", action="store_true", help="Only upload existing .pkl files")
    return p.parse_args()


def upload_model_to_railway(pkl_path: pathlib.Path, symbol: str, interval: str, regime: str) -> bool:
    import requests
    url = f"{RAILWAY_ML_URL}/upload_model"
    try:
        with open(pkl_path, "rb") as f:
            data = f.read()
        resp = requests.post(url,
            data={"symbol": symbol, "interval": interval, "regime": regime},
            files={"model_file": (pkl_path.name, data, "application/octet-stream")},
            timeout=60
        )
        if resp.status_code == 200:
            j = resp.json()
            print(f"  [UPLOAD OK] {pkl_path.name} -> Railway (AUC={j.get('auc')})")
            return True
        else:
            print(f"  [UPLOAD ERR] {pkl_path.name}: {resp.status_code} {resp.text[:100]}")
            return False
    except Exception as e:
        print(f"  [UPLOAD ERR] {pkl_path.name}: {e}")
        return False


def train_one(symbol: str, interval: str, regime: str) -> dict:
    from model import ForexPredictor
    key = f"{symbol}_{interval}" + (f"_{regime}" if regime != "ALL" else "")
    
    pkl_path = MODEL_DIR / f"{key}.pkl"
    if pkl_path.exists():
        print(f"\n[{time.strftime('%H:%M:%S')}] Skipping {key} (already trained)")
        return {"key": key, "success": True, "auc": 0, "n_train": 0, "elapsed": 0, "skipped": True}

    print(f"\n{'='*55}")
    print(f"[{time.strftime('%H:%M:%S')}] Training: {key}")
    print(f"{'='*55}")
    t0 = time.time()
    try:
        predictor = ForexPredictor(symbol, interval, regime)
        result = predictor.train(candles=None)
        elapsed = time.time() - t0
        if "error" in result:
            print(f"  [ERROR] {result['error']}")
            return {"key": key, "success": False, "error": result["error"], "elapsed": elapsed}
        auc = result.get("auc", 0)
        n   = result.get("n_train", 0)
        print(f"  [OK] AUC={auc:.4f} | n={n:,} | {elapsed:.0f}s")
        return {"key": key, "success": True, "auc": auc, "n_train": n, "elapsed": elapsed}
    except Exception as e:
        elapsed = time.time() - t0
        print(f"  [EXCEPTION] {e}")
        return {"key": key, "success": False, "error": str(e), "elapsed": elapsed}


def main():
    args = parse_args()

    os.environ.setdefault("DATABASE_URL", DB_URL)
    os.environ.setdefault("TwelveDataApiKey", "3e0d610500f0414282d471471f59504e")
    os.environ.setdefault("TWELVE_DATA_API_KEY", os.environ["TwelveDataApiKey"])
    os.environ.setdefault("TARGET_HORIZON_CANDLES", "3")
    os.environ.setdefault("MIN_CONFIDENCE", "0.48")
    os.environ.pop("MAX_HISTORICAL_CANDLES", None)  # use full 250k

    MODEL_DIR.mkdir(parents=True, exist_ok=True)

    if args.upload_only:
        print("=== Upload-only mode: pushing existing .pkl to Railway ===")
        for pkl in sorted(MODEL_DIR.glob("*.pkl")):
            name = pkl.stem
            parts = name.split("_")
            symbol   = parts[0]
            interval = parts[1] if len(parts) > 1 else "1m"
            regime   = parts[2] if len(parts) > 2 else "ALL"
            upload_model_to_railway(pkl, symbol, interval, regime)
        return

    total = len(args.pairs) * len(args.intervals) * len(args.regimes)
    print(f"\n=== LOCAL TRAIN ALL: {total} models ===")
    print(f"Pairs:     {args.pairs}")
    print(f"Intervals: {args.intervals}")
    print(f"Regimes:   {args.regimes}")
    print(f"Upload:    {args.upload}\n")

    results  = []
    done     = 0
    failed   = 0

    for symbol in args.pairs:
        for interval in args.intervals:
            for regime in args.regimes:
                result = train_one(symbol, interval, regime)
                results.append(result)

                if result["success"]:
                    done += 1
                    if args.upload:
                        key      = f"{symbol}_{interval}" + (f"_{regime}" if regime != "ALL" else "")
                        pkl_path = MODEL_DIR / f"{key}.pkl"
                        if pkl_path.exists():
                            upload_model_to_railway(pkl_path, symbol, interval, regime)
                else:
                    failed += 1

                remaining    = total - done - failed
                elapsed_sum  = sum(r.get("elapsed", 0) for r in results)
                avg_time     = elapsed_sum / len(results)
                eta_min      = avg_time * remaining / 60
                print(f"\n  Progress: {done+failed}/{total} | OK={done} | FAIL={failed} | ETA ~{eta_min:.0f} min")

    print(f"\n{'='*55}")
    print(f"DONE: {done} OK / {failed} failed / {total} total")
    ok = [r for r in results if r["success"]]
    if ok:
        avg_auc = sum(r["auc"] for r in ok) / len(ok)
        print(f"Avg AUC: {avg_auc:.4f}")
        print("\nTop-5 by AUC:")
        for r in sorted(ok, key=lambda x: x["auc"], reverse=True)[:5]:
            print(f"  {r['key']}: AUC={r['auc']:.4f}")
    if not args.upload and done > 0:
        print(f"\nModels saved to: {MODEL_DIR}")
        print("To upload to Railway: py local_train_all.py --upload-only")


if __name__ == "__main__":
    main()
