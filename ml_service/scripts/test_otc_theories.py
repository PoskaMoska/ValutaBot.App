"""
test_otc_theories.py - Comprehensive testing of the user's hypotheses about Pocket Option OTC:

Hypothesis 1: Historical Replay (Does PO OTC replay past real Forex candles from 1h, 12h, 24h, 48h ago)?
Hypothesis 2: Transformation / Inversion / Filter (Is PO OTC inverted, scaled, or smoothed real Forex)?
Hypothesis 3: Streak Exhaustion / "Hole" in Generator (Does PO OTC cap green/red streaks, forcing a reversal)?
Hypothesis 4: Microstructure / Quantization Patterns (Are there specific price endings, tick steps, or periodic patterns)?
"""

import os
import glob
from pathlib import Path
import numpy as np
import pandas as pd
import psycopg2
from scipy import signal

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
DB_URL = os.environ.get("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")


def load_po():
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0  # UTC seconds
    return df


def build_candles(df, asset, interval_sec=5):
    d = df[df["asset"] == asset].copy()
    if len(d) == 0:
        return pd.DataFrame()
    d["candle_ts"] = (d["ts"] // interval_sec) * interval_sec
    g = d.groupby("candle_ts")["price"].agg(open="first", high="max", low="min", close="last", count="count").reset_index()
    return g


def test_streak_exhaustion(po_candles, asset):
    """Test if long streaks of Green or Red candles in PO OTC are artificially cut off by the algorithm."""
    c = po_candles.copy()
    c["ret"] = c["close"] - c["open"]
    c["dir"] = np.sign(c["ret"])
    # filter out flats
    c = c[c["dir"] != 0].reset_index(drop=True)
    
    dirs = c["dir"].to_numpy()
    runs = []
    current_dir = dirs[0]
    current_len = 1
    
    for i in range(1, len(dirs)):
        if dirs[i] == current_dir:
            current_len += 1
        else:
            runs.append((current_dir, current_len))
            current_dir = dirs[i]
            current_len = 1
    runs.append((current_dir, current_len))
    
    # Analyze probability of reversal after streak of length 1, 2, 3, 4, 5
    streak_counts = {}
    streak_reversals = {}
    
    for streak_len in range(1, 7):
        # find every instance where dirs has streak of streak_len
        n_streak = 0
        n_reversed = 0
        for i in range(len(dirs) - streak_len):
            sub = dirs[i:i+streak_len]
            if np.all(sub == sub[0]):  # all same direction
                n_streak += 1
                next_d = dirs[i + streak_len]
                if next_d != sub[0]:
                    n_reversed += 1
        if n_streak > 0:
            streak_counts[streak_len] = n_streak
            streak_reversals[streak_len] = (n_reversed / n_streak) * 100
            
    print(f"\n--- [HYPOTHESIS 3: STREAK EXHAUSTION / REVERSAL] {asset} ---")
    print(f"Total non-flat candles: {len(dirs)}")
    for sl in sorted(streak_counts.keys()):
        print(f"  After streak of {sl} candles in same dir: P(REVERSAL) = {streak_reversals[sl]:.1f}% (sample n={streak_counts[sl]})")
    

def test_historical_replay(po_candles, asset):
    """Test if PO OTC is a replay of past real Forex candles across the last 3 days."""
    base_asset = asset.replace("_otc", "").replace("_OTC", "")
    print(f"\n--- [HYPOTHESIS 1: HISTORICAL REPLAY SCAN] {asset} vs Tiingo {base_asset} ---")
    
    conn = psycopg2.connect(DB_URL)
    # Load all 3 days of s5 candles from DB
    query = """
        SELECT open_time, close_price 
        FROM subminute_candles 
        WHERE asset = %(asset)s AND interval = 's5' 
        ORDER BY open_time ASC
    """
    ti_df = pd.read_sql(query, conn, params={"asset": base_asset})
    conn.close()
    
    if len(ti_df) < 1000:
        print(f"Not enough Tiingo history ({len(ti_df)} candles)")
        return
        
    ti_df["ts"] = pd.to_datetime(ti_df["open_time"], utc=True, format="mixed").astype("int64") // 10**9
    ti_df = ti_df.drop_duplicates("ts").sort_values("ts")
    
    # Compute returns on regular 5s grid
    ti_ts = ti_df["ts"].to_numpy()
    ti_px = ti_df["close_price"].to_numpy()
    
    po_ts = po_candles["candle_ts"].to_numpy()
    po_px = po_candles["close"].to_numpy()
    
    # Let's take a 30-minute block of PO returns (360 candles)
    n_sample = min(len(po_candles) - 1, 300)
    po_sample_ret = np.diff(np.log(po_px[:n_sample]))
    po_sample_ret = (po_sample_ret - np.mean(po_sample_ret)) / (np.std(po_sample_ret) + 1e-9)
    
    ti_ret = np.diff(np.log(ti_px))
    ti_ret = (ti_ret - np.mean(ti_ret)) / (np.std(ti_ret) + 1e-9)
    
    # Cross-correlation with Tiingo history across all possible time shifts!
    corr = signal.correlate(ti_ret, po_sample_ret, mode="valid") / len(po_sample_ret)
    max_idx = np.argmax(np.abs(corr))
    max_corr = corr[max_idx]
    
    best_ti_ts = ti_ts[max_idx]
    offset_hours = (po_ts[0] - best_ti_ts) / 3600.0
    
    print(f"Scanned {len(ti_ret):,} historical 5s candles (~{len(ti_ret)*5/3600:.1f} hours of real Forex history).")
    print(f"Max absolute correlation across all historical offsets: {max_corr:.4f}")
    print(f"Best match offset: {offset_hours:.2f} hours ago (ts: {pd.to_datetime(best_ti_ts, unit='s', utc=True)})")
    
    # Top 5 correlations:
    top5_idx = np.argsort(np.abs(corr))[-5:][::-1]
    print("Top 5 correlation peaks in history:")
    for rank, idx in enumerate(top5_idx, 1):
        c_val = corr[idx]
        t_shift = (po_ts[0] - ti_ts[idx]) / 3600.0
        print(f"  #{rank}: corr = {c_val:+.4f} at offset {t_shift:+.2f} hours")


def test_transformation(po_candles, asset):
    """Test if PO OTC is an inverted, smoothed, or power-law transformation of concurrent Tiingo price."""
    base_asset = asset.replace("_otc", "").replace("_OTC", "")
    t0, t1 = po_candles["candle_ts"].min(), po_candles["candle_ts"].max()
    
    conn = psycopg2.connect(DB_URL)
    query = """
        SELECT open_time, open_price, close_price 
        FROM subminute_candles 
        WHERE asset = %(asset)s AND interval = 's5' 
        ORDER BY open_time ASC
    """
    ti_df = pd.read_sql(query, conn, params={"asset": base_asset})
    conn.close()
    
    ti_df["candle_ts"] = pd.to_datetime(ti_df["open_time"], utc=True, format="mixed").astype("int64") // 10**9
    ti_df = ti_df.drop_duplicates("candle_ts").sort_values("candle_ts")
    ti_df = ti_df[(ti_df["candle_ts"] >= t0) & (ti_df["candle_ts"] <= t1)]
    
    merged = pd.merge(po_candles, ti_df, on="candle_ts")
    if len(merged) < 50:
        print("Not enough merged candles for transformation test.")
        return
        
    print(f"\n--- [HYPOTHESIS 2: MATHEMATICAL TRANSFORMATION TEST] {asset} (n={len(merged)}) ---")
    po_r = merged["close"] - merged["open"]
    ti_r = merged["close_price"] - merged["open_price"]
    
    # 1. Inverted market? (PO = -Tiingo)
    inv_corr = np.corrcoef(-ti_r, po_r)[0, 1]
    # 2. Moving average smoothed Tiingo (EMA 3, 5, 10)?
    ti_ema3 = ti_r.ewm(span=3).mean()
    ti_ema5 = ti_r.ewm(span=5).mean()
    ema3_corr = np.corrcoef(ti_ema3, po_r)[0, 1]
    ema5_corr = np.corrcoef(ti_ema5, po_r)[0, 1]
    # 3. Non-linear rank correlation (Spearman)
    from scipy.stats import spearmanr
    sp_corr, sp_p = spearmanr(ti_r, po_r)
    
    print(f"  Standard Return Correlation:   {np.corrcoef(ti_r, po_r)[0, 1]:+.4f}")
    print(f"  Inverted Market (-Tiingo):      {inv_corr:+.4f}")
    print(f"  Tiingo EMA-3 Smoothed:         {ema3_corr:+.4f}")
    print(f"  Tiingo EMA-5 Smoothed:         {ema5_corr:+.4f}")
    print(f"  Spearman Non-linear Rank Corr: {sp_corr:+.4f} (p-value={sp_p:.3f})")


def main():
    po = load_po()
    assets = sorted(po["asset"].unique())
    print(f"Loaded {len(po):,} ticks across {assets}")
    
    for a in ["EURUSD_otc", "GBPUSD_otc"]:
        c = build_candles(po, a, 5)
        print(f"\n=======================================================")
        print(f"ASSET: {a} (Total 5s candles: {len(c)})")
        print(f"=======================================================")
        test_streak_exhaustion(c, a)
        test_transformation(c, a)
        test_historical_replay(c, a)


if __name__ == "__main__":
    main()
