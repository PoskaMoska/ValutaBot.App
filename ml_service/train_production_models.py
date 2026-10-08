# -*- coding: utf-8 -*-
"""
train_production_models.py - Trains clean production LightGBM models with current features
and saves them to data/models/{symbol}_{interval}_v2.pkl.
"""
import sys, os, time, pathlib

# Force UTF-8 output on Windows
if sys.stdout.encoding != 'utf-8':
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace', line_buffering=True)

SCRIPT_DIR = pathlib.Path(__file__).parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

os.environ.setdefault("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")

from model import ForexPredictor, MODEL_DIR

PAIRS = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
# Prioritize 1m (highest alpha / 40k candles) and core subminute intervals
INTERVALS = ["1m", "s5", "s10", "s15", "s30"]

def train_all():
    MODEL_DIR.mkdir(parents=True, exist_ok=True)
    total = len(PAIRS) * len(INTERVALS)
    print(f"=== Starting Production Training: {total} models ===")
    results = []
    
    for pair in PAIRS:
        for interval in INTERVALS:
            t0 = time.time()
            print(f"--> Training {pair} {interval}...")
            try:
                predictor = ForexPredictor(pair, interval, "ALL")
                res = predictor.train()
                elapsed = time.time() - t0
                if "error" in res:
                    print(f"    [ERR] {pair} {interval}: {res['error']}")
                    results.append({"key": f"{pair}_{interval}", "success": False, "error": res["error"]})
                else:
                    auc = res.get("auc", 0)
                    acc = res.get("accuracy", 0)
                    n = res.get("n_train", 0)
                    print(f"    [OK] {pair} {interval}: AUC={auc:.4f}, Acc={acc:.4f}, N={n:,} ({elapsed:.1f}s)")
                    results.append({"key": f"{pair}_{interval}", "success": True, "auc": auc, "acc": acc, "n": n})
            except Exception as e:
                elapsed = time.time() - t0
                print(f"    [EXC] {pair} {interval}: {e}")
                results.append({"key": f"{pair}_{interval}", "success": False, "error": str(e)})

    print("\n" + "="*50)
    print("TRAINING SUMMARY:")
    ok_count = sum(1 for r in results if r.get("success"))
    print(f"Success: {ok_count} / {len(results)}")
    for r in results:
        if r.get("success"):
            print(f"  {r['key']}: AUC={r['auc']:.4f}, Acc={r['acc']:.4f}, N={r['n']}")
        else:
            print(f"  {r['key']}: FAILED ({r.get('error')})")

if __name__ == "__main__":
    train_all()
