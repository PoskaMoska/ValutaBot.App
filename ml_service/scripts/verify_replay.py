"""
verify_replay.py - Deep verification of the ~27.8h offset correlation.
Checks whether PO OTC actually replays real Forex data from yesterday at 12:24 UTC!
"""

import os
from pathlib import Path
import numpy as np
import pandas as pd
import psycopg2

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
DB_URL = os.environ.get("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")


def load_po(asset):
    import glob
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0
    d = df[df["asset"] == asset].copy()
    d["candle_ts"] = (d["ts"] // 5) * 5
    c = d.groupby("candle_ts")["price"].agg(open="first", high="max", low="min", close="last").reset_index()
    return c


def main():
    po = load_po("EURUSD_otc")
    print(f"PO candles: {len(po)}, start={pd.to_datetime(po['candle_ts'].min(), unit='s', utc=True)}, end={pd.to_datetime(po['candle_ts'].max(), unit='s', utc=True)}")
    
    # Best offset found: ~27.80 hours ago = ~100,080 seconds ago
    po_t0 = po["candle_ts"].min()
    ti_target_t0 = int(po_t0 - 27.80 * 3600)
    ti_target_t1 = int(ti_target_t0 + len(po) * 5)
    
    print(f"Target Tiingo window: {pd.to_datetime(ti_target_t0, unit='s', utc=True)} -> {pd.to_datetime(ti_target_t1, unit='s', utc=True)}")
    
    conn = psycopg2.connect(DB_URL)
    query = """
        SELECT open_time, open_price, high_price, low_price, close_price 
        FROM subminute_candles 
        WHERE asset = 'EURUSD' AND interval = 's5'
        ORDER BY open_time ASC
    """
    ti_df = pd.read_sql(query, conn)
    conn.close()
    
    ti_df["candle_ts"] = pd.to_datetime(ti_df["open_time"], utc=True, format="mixed").astype("int64") // 10**9
    ti_df = ti_df.drop_duplicates("candle_ts").sort_values("candle_ts")
    
    # Test a search window around 27.8 hours (+/- 30 minutes in 5s steps)
    best_corr = -2
    best_dt = 0
    
    po_rets = np.diff(np.log(po["close"].to_numpy()[:200]))
    
    for shift_s in range(int(26.5 * 3600), int(29.0 * 3600), 5):
        t_start = po_t0 - shift_s
        # slice tiingo
        sub = ti_df[(ti_df["candle_ts"] >= t_start) & (ti_df["candle_ts"] < t_start + 200 * 5)]
        if len(sub) == 200:
            ti_rets = np.diff(np.log(sub["close_price"].to_numpy()))
            c = np.corrcoef(po_rets, ti_rets)[0, 1]
            if np.abs(c) > np.abs(best_corr):
                best_corr = c
                best_dt = shift_s
                
    print(f"\nExact local segment search result:")
    print(f"Best exact Pearson Correlation: {best_corr:+.4f} at shift: {best_dt/3600:.4f} hours ago ({best_dt} seconds)")
    
    # If correlation is high, show candles!
    t_start = po_t0 - best_dt
    sub = ti_df[(ti_df["candle_ts"] >= t_start) & (ti_df["candle_ts"] < t_start + 200 * 5)].copy().reset_index(drop=True)
    if len(sub) == 200:
        po_sub = po.iloc[:200].copy().reset_index(drop=True)
        print("\n--- SAMPLE COMPARISON (First 10 candles) ---")
        for i in range(10):
            t_ti = pd.to_datetime(sub.loc[i, "candle_ts"], unit="s", utc=True).strftime("%H:%M:%S")
            t_po = pd.to_datetime(po_sub.loc[i, "candle_ts"], unit="s", utc=True).strftime("%H:%M:%S")
            print(f"PO {t_po} ({po_sub.loc[i, 'open']:.5f}->{po_sub.loc[i, 'close']:.5f})  vs  Tiingo {t_ti} ({sub.loc[i, 'open_price']:.5f}->{sub.loc[i, 'close_price']:.5f})")


if __name__ == "__main__":
    main()
