import os
from pathlib import Path
import numpy as np
import pandas as pd
import psycopg2

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
DB_URL = os.environ.get("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")

def main():
    import glob
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0
    d = df[df["asset"] == "EURUSD_otc"].copy()
    d["candle_ts"] = (d["ts"] // 5) * 5
    po = d.groupby("candle_ts")["price"].agg(open="first", high="max", low="min", close="last").reset_index()
    
    conn = psycopg2.connect(DB_URL)
    ti_df = pd.read_sql("SELECT open_time, close_price FROM subminute_candles WHERE asset='EURUSD' AND interval='s5'", conn)
    conn.close()
    ti_df["candle_ts"] = pd.to_datetime(ti_df["open_time"], utc=True, format="mixed").astype("int64") // 10**9
    ti_df = ti_df.drop_duplicates("candle_ts").set_index("candle_ts")["close_price"]
    
    po_t0 = int(po["candle_ts"].min())
    po_px = po.set_index("candle_ts")["close"]
    # 200 candles on 5s grid
    grid = np.arange(po_t0, po_t0 + 200 * 5, 5)
    po_series = po_px.reindex(grid).interpolate()
    po_rets = np.diff(np.log(po_series.to_numpy()))
    
    # Search across last 48 hours in 5-second steps
    print(f"Searching across 48 hours for true correlation matches (N=200)...")
    best_c = 0
    best_shift = 0
    
    shifts = np.arange(3600, 48 * 3600, 5)
    matches_above_05 = []
    
    for s in shifts:
        target_grid = grid - s
        if target_grid[0] < ti_df.index.min() or target_grid[-1] > ti_df.index.max():
            continue
        ti_series = ti_df.reindex(target_grid)
        if ti_series.isna().sum() > 20:
            continue
        ti_series = ti_series.interpolate()
        ti_rets = np.diff(np.log(ti_series.to_numpy()))
        
        c = np.corrcoef(po_rets, ti_rets)[0, 1]
        if np.isfinite(c) and np.abs(c) > np.abs(best_c):
            best_c = c
            best_shift = s
        if np.isfinite(c) and np.abs(c) > 0.35:
            matches_above_05.append((s, c))
            
    print(f"Global search complete over {len(shifts):,} offsets.")
    print(f"Max absolute Pearson correlation found: {best_c:+.4f} at shift {best_shift/3600:.2f} hours ago ({best_shift} s)")
    print(f"Total offsets with |corr| > 0.35: {len(matches_above_05)}")
    for s, c in sorted(matches_above_05, key=lambda x: -abs(x[1]))[:5]:
        print(f"  Shift {s/3600:+.2f}h: corr = {c:+.4f}")

if __name__ == "__main__":
    main()
