"""
audit_1m_otc.py - Statistical audit of 1-minute (s60) candles from the collected PO OTC dataset.
"""

import os
import glob
from pathlib import Path
import numpy as np
import pandas as pd

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"

def load_ticks():
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0
    return df

def build_1m_candles(df):
    out = []
    for a in sorted(df["asset"].unique()):
        sub = df[df["asset"] == a].copy()
        sub["candle_ts"] = (sub["ts"] // 60) * 60
        g = sub.groupby("candle_ts")["price"].agg(
            open="first", high="max", low="min", close="last", ticks="count"
        ).reset_index()
        g["asset"] = a
        g["ret"] = g["close"] - g["open"]
        g["range"] = g["high"] - g["low"]
        g["dir"] = np.sign(g["ret"])
        out.append(g)
    return pd.concat(out, ignore_index=True)

def calc_ci(p_pct, n):
    if n <= 0: return 0.0, 0.0
    p = p_pct / 100.0
    se = np.sqrt(max(0, p * (1 - p)) / n)
    ci = 1.96 * se * 100.0
    return max(0.0, p_pct - ci), min(100.0, p_pct + ci)

def main():
    ticks = load_ticks()
    c1m = build_1m_candles(ticks)
    assets = sorted(c1m["asset"].unique())
    
    print("=" * 95)
    print(f"AUDIT OF 1-MINUTE (s60 / 1M) POCKET OPTION OTC CANDLES")
    print(f"Total 1m candles: {len(c1m)} across {len(assets)} assets (~{len(c1m)//len(assets)} candles/asset)")
    print("=" * 95)
    
    # 1. Random walk & Autocorrelation on 1m
    print("\n1. RANDOM WALK & AUTOCORRELATION (1M CANDLES):")
    print(f"{'Asset':12s} | {'1m Candles':>10s} | {'Green/Red/Flat':>16s} | {'AR(1)':>9s} | {'AR(2)':>9s} | {'VR(1m->2m)':>11s} | {'Verdict':>12s}")
    print("-" * 90)
    for a in assets:
        sub = c1m[c1m["asset"] == a].sort_values("candle_ts")
        n_gr = (sub["dir"] == 1).sum()
        n_rd = (sub["dir"] == -1).sum()
        n_fl = (sub["dir"] == 0).sum()
        p = np.log(sub["close"].to_numpy())
        r1 = np.diff(p)
        rq2 = p[2:] - p[:-2]
        vr2 = (np.var(rq2, ddof=1) / (2 * np.var(r1, ddof=1))) if np.var(r1, ddof=1) > 0 else 1.0
        ac1 = np.corrcoef(r1[:-1], r1[1:])[0, 1] if len(r1) > 5 else 0
        ac2 = np.corrcoef(r1[:-2], r1[2:])[0, 1] if len(r1) > 5 else 0
        verd = "Random Walk" if 0.85 <= vr2 <= 1.15 else ("Mean-Revert" if vr2 < 0.85 else "Trending")
        print(f"{a:12s} | {len(sub):10d} | {n_gr:2d} / {n_rd:2d} / {n_fl:1d}     | {ac1:+9.3f} | {ac2:+9.3f} | {vr2:11.3f} | {verd:>12s}")
        
    # 2. Streaks on 1m
    print("\n" + "=" * 95)
    print("2. STREAKS ON 1-MINUTE CANDLES (Continuation vs Reversal):")
    print(f"{'Asset':12s} | {'Rule':22s} | {'Winrate':>10s} | {'[95% CI]':>15s} | {'Sample n':>9s}")
    print("-" * 75)
    
    all_continuation = []
    all_reversal_2 = []
    all_reversal_3 = []
    
    for a in assets + ["POOLED"]:
        if a == "POOLED":
            c = c1m[c1m["dir"] != 0].sort_values("candle_ts").reset_index(drop=True)
        else:
            c = c1m[(c1m["asset"] == a) & (c1m["dir"] != 0)].sort_values("candle_ts").reset_index(drop=True)
            
        dirs = c["dir"].to_numpy()
        # Streak 1: Trend continuation (P(same dir))
        same_cnt, tot_1 = 0, 0
        for i in range(len(dirs) - 1):
            tot_1 += 1
            if dirs[i+1] == dirs[i]:
                same_cnt += 1
        cont_wr = (same_cnt / tot_1 * 100) if tot_1 > 0 else 0
        ci_l1, ci_h1 = calc_ci(cont_wr, tot_1)
        
        # Streak 2: Reversal after 2 same
        rev_cnt_2, tot_2 = 0, 0
        for i in range(len(dirs) - 2):
            if dirs[i] == dirs[i+1]:
                tot_2 += 1
                if dirs[i+2] != dirs[i]:
                    rev_cnt_2 += 1
        rev_wr_2 = (rev_cnt_2 / tot_2 * 100) if tot_2 > 0 else 0
        ci_l2, ci_h2 = calc_ci(rev_wr_2, tot_2)

        # Streak 3: Reversal after 3 same
        rev_cnt_3, tot_3 = 0, 0
        for i in range(len(dirs) - 3):
            if dirs[i] == dirs[i+1] == dirs[i+2]:
                tot_3 += 1
                if dirs[i+3] != dirs[i]:
                    rev_cnt_3 += 1
        rev_wr_3 = (rev_cnt_3 / tot_3 * 100) if tot_3 > 0 else 0
        ci_l3, ci_h3 = calc_ci(rev_wr_3, tot_3)

        print(f"{a:12s} | Continuation after 1   | {cont_wr:9.1f}% | [{ci_l1:4.1f} - {ci_h1:4.1f}%] | n={tot_1:4d}")
        print(f"{a:12s} | Reversal after 2       | {rev_wr_2:9.1f}% | [{ci_l2:4.1f} - {ci_h2:4.1f}%] | n={tot_2:4d}")
        if tot_3 >= 5:
            print(f"{a:12s} | Reversal after 3       | {rev_wr_3:9.1f}% | [{ci_l3:4.1f} - {ci_h3:4.1f}%] | n={tot_3:4d}")
        print("-" * 75)

if __name__ == "__main__":
    main()
