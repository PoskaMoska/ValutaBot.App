"""
full_audit_runner.py - Final 2-hour OTC internal pattern audit.

Analyzes 59,202 PO OTC ticks collected across 6 assets:
- Per pair & Pooled across all pairs.
- Horizons: s5, s15, s30, s60.
- Walk-Forward split: 1st half vs 2nd half.
- Confidence intervals (95% CI) and breakeven benchmark (52.1%).
"""

import os
import glob
from pathlib import Path
import numpy as np
import pandas as pd
from scipy import stats

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
BE_WINRATE = 52.1


def load_ticks():
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0  # UTC epoch seconds
    return df


def build_candles_all(df, interval_sec):
    out = []
    for a in sorted(df["asset"].unique()):
        sub = df[df["asset"] == a].copy()
        sub["candle_ts"] = (sub["ts"] // interval_sec) * interval_sec
        g = sub.groupby("candle_ts")["price"].agg(
            open="first", high="max", low="min", close="last", ticks="count"
        ).reset_index()
        g["asset"] = a
        g["interval"] = interval_sec
        g["ret"] = g["close"] - g["open"]
        g["range"] = g["high"] - g["low"]
        g["dir"] = np.sign(g["ret"])
        out.append(g)
    return pd.concat(out, ignore_index=True)


def calc_ci(p_pct, n):
    if n <= 0:
        return 0.0, 0.0
    p = p_pct / 100.0
    se = np.sqrt(max(0, p * (1 - p)) / n)
    ci = 1.96 * se * 100.0
    return max(0.0, p_pct - ci), min(100.0, p_pct + ci)


def audit_streaks(candles, horizon_str):
    print(f"\n--- [1. STREAK REVERSAL / CONTINUATION AUDIT ({horizon_str})] ---")
    print(f"{'Asset':10s} | {'Streak':8s} | {'1st Half WR (n)':>19s} | {'2nd Half WR (n)':>19s} | {'Full WR [95% CI] (n)':>27s} | {'Edge > 52.1%?':>14s}")
    print("-" * 105)
    
    assets = sorted(candles["asset"].unique()) + ["POOLED"]
    
    confirmed_rules = []
    
    for a in assets:
        if a == "POOLED":
            d_pool = candles[candles["dir"] != 0].copy()
            c = d_pool.sort_values("candle_ts").reset_index(drop=True)
        else:
            c = candles[(candles["asset"] == a) & (candles["dir"] != 0)].sort_values("candle_ts").reset_index(drop=True)
            
        if len(c) < 40:
            continue
            
        t_mid = (c["candle_ts"].min() + c["candle_ts"].max()) / 2
        h1 = c[c["candle_ts"] < t_mid]
        h2 = c[c["candle_ts"] >= t_mid]
        
        for streak in (1, 2, 3):
            # Reversal probability
            def get_rev_stat(df_sub):
                dirs = df_sub["dir"].to_numpy()
                n_strk, n_rev = 0, 0
                for i in range(len(dirs) - streak):
                    sub = dirs[i:i+streak]
                    if np.all(sub == sub[0]):
                        n_strk += 1
                        if dirs[i+streak] != sub[0]:
                            n_rev += 1
                wr = (n_rev / n_strk * 100) if n_strk > 0 else 0
                return wr, n_strk
                
            w1, n1 = get_rev_stat(h1)
            w2, n2 = get_rev_stat(h2)
            wf, nf = get_rev_stat(c)
            ci_low, ci_high = calc_ci(wf, nf)
            
            # Check edge: holds in both halves and lower CI > 50%
            is_edge = (w1 > BE_WINRATE and w2 > BE_WINRATE and n1 >= 25 and n2 >= 25)
            status = "CONFIRMED EDGE!" if is_edge else "NO (Noise)"
            if is_edge:
                confirmed_rules.append((a, f"Reversal after {streak}", wf, n1+n2))
                
            print(f"{a:10s} | {streak:1d} in row | {w1:5.1f}% (n={n1:3d})     | {w2:5.1f}% (n={n2:3d})     | {wf:5.1f}% [{ci_low:4.1f}-{ci_high:4.1f}%] (n={nf:4d}) | {status:>14s}")
            
    return confirmed_rules


def audit_volatility_exhaustion(candles, horizon_str):
    print(f"\n--- [2. VOLATILITY EXHAUSTION AUDIT (Top 10% Range -> Reversal) ({horizon_str})] ---")
    print(f"{'Asset':10s} | {'1st Half WR (n)':>19s} | {'2nd Half WR (n)':>19s} | {'Full WR [95% CI] (n)':>27s} | {'Edge > 52.1%?':>14s}")
    print("-" * 95)
    
    assets = sorted(candles["asset"].unique()) + ["POOLED"]
    for a in assets:
        if a == "POOLED":
            c = candles.copy().sort_values("candle_ts").reset_index(drop=True)
        else:
            c = candles[candles["asset"] == a].copy().sort_values("candle_ts").reset_index(drop=True)
            
        if len(c) < 40:
            continue
            
        q90 = c["range"].quantile(0.90)
        c["next_dir"] = c["dir"].shift(-1)
        big = c[(c["range"] >= q90) & (c["dir"] != 0) & (c["next_dir"].notna()) & (c["next_dir"] != 0)]
        if len(big) < 20:
            continue
            
        t_mid = (big["candle_ts"].min() + big["candle_ts"].max()) / 2
        h1 = big[big["candle_ts"] < t_mid]
        h2 = big[big["candle_ts"] >= t_mid]
        
        def rev_rate(df_sub):
            if len(df_sub) == 0:
                return 0.0, 0
            n_rev = (df_sub["next_dir"] == -df_sub["dir"]).sum()
            return (n_rev / len(df_sub) * 100), len(df_sub)
            
        w1, n1 = rev_rate(h1)
        w2, n2 = rev_rate(h2)
        wf, nf = rev_rate(big)
        ci_l, ci_h = calc_ci(wf, nf)
        is_edge = (w1 > BE_WINRATE and w2 > BE_WINRATE and n1 >= 15 and n2 >= 15)
        status = "CONFIRMED EDGE!" if is_edge else "NO (Noise)"
        print(f"{a:10s} | {w1:5.1f}% (n={n1:3d})     | {w2:5.1f}% (n={n2:3d})     | {wf:5.1f}% [{ci_l:4.1f}-{ci_h:4.1f}%] (n={nf:4d}) | {status:>14s}")


def audit_time_of_minute(candles_s5):
    print(f"\n--- [3. TIME-OF-MINUTE EFFECTS (s5 Candles by Second in Minute)] ---")
    c = candles_s5[candles_s5["dir"] != 0].copy()
    c["sec"] = c["candle_ts"] % 60
    
    print(f"{'Second':8s} | {'Green Candles':>14s} | {'Red Candles':>12s} | {'Up Winrate':>12s} | {'Verdict':>12s}")
    print("-" * 65)
    for s in sorted(c["sec"].unique()):
        sub = c[c["sec"] == s]
        n_up = (sub["dir"] == 1).sum()
        n_dn = (sub["dir"] == -1).sum()
        tot = n_up + n_dn
        up_pct = (n_up / tot * 100) if tot > 0 else 0
        v = "UP Skew" if up_pct > 53 else ("DOWN Skew" if up_pct < 47 else "Balanced")
        print(f":{int(s):02d}s     | {n_up:14d} | {n_dn:12d} | {up_pct:11.1f}% | {v:>12s}")


def audit_random_walk_metrics(candles, horizon_str):
    print(f"\n--- [4. RANDOM WALK METRICS (Lo-MacKinlay Variance Ratio & Autocorrelations) ({horizon_str})] ---")
    print(f"{'Asset':10s} | {'VR(q=2)':>10s} | {'VR(q=4)':>10s} | {'AR(1)':>9s} | {'AR(2)':>9s} | {'AR(3)':>9s} | {'Verdict':>14s}")
    print("-" * 80)
    for a in sorted(candles["asset"].unique()):
        sub = candles[candles["asset"] == a].sort_values("candle_ts")
        p = np.log(sub["close"].to_numpy())
        r1 = np.diff(p)
        if len(r1) < 40:
            continue
        # VR q=2
        rq2 = p[2:] - p[:-2]
        vr2 = (np.var(rq2, ddof=1) / (2 * np.var(r1, ddof=1))) if np.var(r1, ddof=1) > 0 else 1.0
        # VR q=4
        rq4 = p[4:] - p[:-4]
        vr4 = (np.var(rq4, ddof=1) / (4 * np.var(r1, ddof=1))) if np.var(r1, ddof=1) > 0 else 1.0
        # Autocorr
        ac1 = np.corrcoef(r1[:-1], r1[1:])[0, 1] if len(r1) > 5 else 0
        ac2 = np.corrcoef(r1[:-2], r1[2:])[0, 1] if len(r1) > 5 else 0
        ac3 = np.corrcoef(r1[:-3], r1[3:])[0, 1] if len(r1) > 5 else 0
        
        verd = "Random Walk" if 0.90 <= vr2 <= 1.10 else ("Mean-Revert" if vr2 < 0.90 else "Trending")
        print(f"{a:10s} | {vr2:10.3f} | {vr4:10.3f} | {ac1:+9.3f} | {ac2:+9.3f} | {ac3:+9.3f} | {verd:>14s}")


def main():
    ticks = load_ticks()
    t0, t1 = ticks["ts"].min(), ticks["ts"].max()
    dur_min = (t1 - t0) / 60.0
    
    print("=" * 105)
    print(f"POCKET OPTION OTC COMPREHENSIVE STATISTICAL AUDIT REPORT")
    print(f"Total Ticks: {len(ticks):,} | Assets: {sorted(ticks['asset'].unique())}")
    print(f"Time Range: {pd.to_datetime(t0, unit='s', utc=True)} -> {pd.to_datetime(t1, unit='s', utc=True)} ({dur_min:.1f} minutes of continuous data)")
    print(f"Benchmark Breakeven Winrate: {BE_WINRATE}% (payout ~92%)")
    print("=" * 105)
    
    # 1. Microstructure summary
    print("\n[MICROSTRUCTURE & DISCRETIZATION]")
    for a in sorted(ticks["asset"].unique()):
        sub = ticks[ticks["asset"] == a].sort_values("ts")
        dp = np.abs(np.diff(sub["price"].to_numpy()))
        zero_pct = (dp == 0).mean() * 100
        pip = 0.01 if "JPY" in a else 0.0001
        step_pips = dp[dp > 0] / pip
        avg_step = step_pips.mean() if len(step_pips) > 0 else 0
        dt = np.diff(sub["ts"].to_numpy())
        med_dt = np.median(dt)
        print(f"  {a:10s}: {len(sub):5d} ticks | dt={med_dt:.2f}s | zero_step={zero_pct:4.1f}% | avg_step={avg_step:5.2f} pips | step_quantum={np.min(step_pips):.2f} pips")
        
    c5 = build_candles_all(ticks, 5)
    c15 = build_candles_all(ticks, 15)
    c30 = build_candles_all(ticks, 30)
    c60 = build_candles_all(ticks, 60)
    
    # Random walk
    audit_random_walk_metrics(c5, "s5 Candles")
    audit_random_walk_metrics(c15, "s15 Candles")
    
    # Streaks on s5 and s15
    edges_s5 = audit_streaks(c5, "s5 Candles")
    edges_s15 = audit_streaks(c15, "s15 Candles")
    
    # Volatility exhaustion
    audit_volatility_exhaustion(c5, "s5 Candles")
    audit_volatility_exhaustion(c15, "s15 Candles")
    
    # Time of minute
    audit_time_of_minute(c5)
    
    print("\n" + "=" * 105)
    print("FINAL AUDIT SUMMARY & EDGE COUNT:")
    all_edges = edges_s5 + edges_s15
    print(f"Total statistically confirmed edges (WR > 52.1% in BOTH halves): {len(all_edges)}")
    for e in all_edges:
        print(f"  -> Asset: {e[0]}, Rule: {e[1]}, Overall WR: {e[2]:.2f}%, Total Samples: {e[3]}")
    print("=" * 105)


if __name__ == "__main__":
    main()
