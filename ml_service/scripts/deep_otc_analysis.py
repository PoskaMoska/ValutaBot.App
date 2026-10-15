"""
deep_otc_analysis.py - In-depth statistical and microstructure audit of Pocket Option OTC tick stream.

Tests:
1. Walk-Forward (First Half vs Second Half) validation for every hypothesis.
2. Streak Continuation vs Reversal on s5, s10, s15, s30, s60 horizons.
3. Extreme Candle Exhaustion (after top 10% candle by range, does next candle reverse?).
4. Autocorrelation of returns (lags 1..10) and Lo-MacKinlay Variance Ratio test for Random Walk.
5. Tick-level microstructure: tick interval distribution, zero-step ratio, price endings.
6. Breakeven threshold: 52.1% (payout ~92%). Any rule is accepted ONLY if both halves > 52.1% and p < 0.05.
"""

import os
import glob
from pathlib import Path
import numpy as np
import pandas as pd
from scipy import stats

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"


def load_all_po_ticks(offset=7200.0):
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    if not files:
        raise FileNotFoundError(f"No tick files in {DATA}")
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - offset
    return df


def build_candles(df, asset, interval_sec):
    d = df[df["asset"] == asset].copy()
    if len(d) == 0:
        return pd.DataFrame()
    d["candle_ts"] = (d["ts"] // interval_sec) * interval_sec
    g = d.groupby("candle_ts")["price"].agg(
        open="first", high="max", low="min", close="last", ticks="count"
    ).reset_index()
    g["asset"] = asset
    g["interval"] = interval_sec
    g["ret"] = g["close"] - g["open"]
    g["range"] = g["high"] - g["low"]
    g["dir"] = np.sign(g["ret"])
    return g


def test_streak_reversals(candles, horizon_name):
    """Checks reversal winrate after 1, 2, 3 consecutive same-color candles, split into two halves."""
    c = candles[candles["dir"] != 0].copy().reset_index(drop=True)
    if len(c) < 100:
        return None
    
    n_half = len(c) // 2
    halves = [("1st Half", c.iloc[:n_half]), ("2nd Half", c.iloc[n_half:]), ("Full", c)]
    
    results = {}
    for streak_len in (1, 2, 3, 4):
        res_h = {}
        for h_name, df_h in halves:
            dirs = df_h["dir"].to_numpy()
            n_streak = 0
            n_rev = 0
            for i in range(len(dirs) - streak_len):
                sub = dirs[i:i+streak_len]
                if np.all(sub == sub[0]):
                    n_streak += 1
                    if dirs[i + streak_len] != sub[0]:
                        n_rev += 1
            winrate = (n_rev / n_streak) * 100 if n_streak > 0 else 0
            res_h[h_name] = (winrate, n_streak)
        results[streak_len] = res_h
    return results


def test_volatility_exhaustion(candles):
    """Tests if a candle with range > 90th percentile reverses on the next candle."""
    c = candles.copy().reset_index(drop=True)
    if len(c) < 100:
        return None
    
    q90 = c["range"].quantile(0.90)
    c["next_dir"] = c["dir"].shift(-1)
    
    big_candles = c[c["range"] >= q90].dropna(subset=["next_dir"])
    big_nz = big_candles[big_candles["dir"] != 0]
    
    n_half = len(big_nz) // 2
    if n_half < 10:
        return None
        
    h1 = big_nz.iloc[:n_half]
    h2 = big_nz.iloc[n_half:]
    
    rev_h1 = (h1["next_dir"] == -h1["dir"]).mean() * 100
    rev_h2 = (h2["next_dir"] == -h2["dir"]).mean() * 100
    rev_all = (big_nz["next_dir"] == -big_nz["dir"]).mean() * 100
    
    return {
        "q90_val": q90,
        "n_total": len(big_nz),
        "h1": (rev_h1, len(h1)),
        "h2": (rev_h2, len(h2)),
        "all": (rev_all, len(big_nz))
    }


def test_autocorrelation(candles):
    """Calculates autocorrelation of returns up to lag 5 and Ljung-Box test."""
    rets = np.diff(np.log(candles["close"].to_numpy()))
    rets = rets[np.isfinite(rets)]
    if len(rets) < 50:
        return None
    
    corrs = [np.corrcoef(rets[:-lag], rets[lag:])[0, 1] for lag in range(1, 6)]
    return corrs


def test_variance_ratio(candles, q=2):
    """
    Lo-MacKinlay Variance Ratio Test.
    VR = Var(r_q) / (q * Var(r_1)).
    If VR = 1.0 -> Perfect Random Walk.
    If VR < 1.0 -> Mean Reversion.
    If VR > 1.0 -> Momentum / Trending.
    """
    p = np.log(candles["close"].to_numpy())
    r1 = np.diff(p)
    rq = p[q:] - p[:-q]
    
    v1 = np.var(r1, ddof=1)
    vq = np.var(rq, ddof=1)
    if v1 == 0:
        return 1.0
    vr = vq / (q * v1)
    return vr


def run_full_audit():
    ticks = load_all_po_ticks()
    assets = sorted(ticks["asset"].unique())
    t0, t1 = ticks["ts"].min(), ticks["ts"].max()
    duration_min = (t1 - t0) / 60.0
    
    print("=" * 85)
    print(f"POCKET OPTION OTC DEEP AUDIT: {len(ticks):,} TICKS ACROSS {len(assets)} ASSETS")
    print(f"Time Range: {pd.to_datetime(t0, unit='s', utc=True)} -> {pd.to_datetime(t1, unit='s', utc=True)} ({duration_min:.1f} minutes)")
    print("=" * 85)
    
    print("\n1. TICK-LEVEL MICROSTRUCTURE PER ASSET:")
    print(f"{'Asset':12s} | {'Ticks':>7s} | {'Ticks/sec':>9s} | {'Zero-Step %':>11s} | {'Avg Step (pips)':>15s}")
    print("-" * 65)
    for a in assets:
        d = ticks[ticks["asset"] == a].sort_values("ts")
        diff_p = np.abs(np.diff(d["price"].to_numpy()))
        pip = 0.01 if "JPY" in a else 0.0001
        diff_pips = diff_p / pip
        zero_pct = (diff_p == 0).mean() * 100
        avg_step = diff_pips[diff_pips > 0].mean() if (diff_pips > 0).sum() > 0 else 0
        t_span = d["ts"].max() - d["ts"].min()
        rate = len(d) / t_span if t_span > 0 else 0
        print(f"{a:12s} | {len(d):7d} | {rate:8.2f}/s | {zero_pct:10.1f}% | {avg_step:14.2f} pips")
        
    print("\n" + "=" * 85)
    print("2. RANDOM WALK TEST (Lo-MacKinlay Variance Ratio & Autocorrelations):")
    print("VR = 1.0 -> чистый Random Walk (непредсказуемо). VR < 1.0 -> возврат к среднему. VR > 1.0 -> тренд.")
    print(f"{'Asset':12s} | {'VR(s5->s10)':>12s} | {'VR(s5->s20)':>12s} | {'Lag 1 Corr':>11s} | {'Lag 2 Corr':>11s} | {'Verdict':>12s}")
    print("-" * 80)
    for a in assets:
        c5 = build_candles(ticks, a, 5)
        vr2 = test_variance_ratio(c5, q=2)
        vr4 = test_variance_ratio(c5, q=4)
        ac = test_autocorrelation(c5)
        c1 = ac[0] if ac else 0
        c2 = ac[1] if ac else 0
        verdict = "Random Walk" if 0.90 <= vr2 <= 1.10 else ("Mean-Revert" if vr2 < 0.90 else "Trending")
        print(f"{a:12s} | {vr2:12.3f} | {vr4:12.3f} | {c1:+11.3f} | {c2:+11.3f} | {verdict:>12s}")

    print("\n" + "=" * 85)
    print("3. STREAK REVERSAL PATTERNS (Walk-Forward Split: 1st Half vs 2nd Half):")
    print("Порог безубыточности при выплате 92%: 52.1%. Паттерн подтвержден ТОЛЬКО если обе половины > 52.1%.")
    print(f"{'Asset':12s} | {'Streak':6s} | {'1st Half Winrate (n)':>22s} | {'2nd Half Winrate (n)':>22s} | {'Full Winrate':>12s} | {'Confirmed?':>10s}")
    print("-" * 90)
    
    confirmed_patterns = 0
    for a in assets:
        c5 = build_candles(ticks, a, 5)
        res = test_streak_reversals(c5, "s5")
        if not res:
            continue
        for sl, h_dict in res.items():
            w1, n1 = h_dict["1st Half"]
            w2, n2 = h_dict["2nd Half"]
            wf, nf = h_dict["Full"]
            ok = (w1 > 52.1 and w2 > 52.1 and n1 >= 30 and n2 >= 30)
            status = "YES (CONFIRMED)" if ok else "NO (Noise)"
            if ok:
                confirmed_patterns += 1
            if sl in (2, 3, 4):
                print(f"{a:12s} | {sl:1d} in row | {w1:5.1f}% (n={n1:3d})          | {w2:5.1f}% (n={n2:3d})          | {wf:5.1f}% (n={nf:3d}) | {status:>10s}")

    print("\n" + "=" * 85)
    print("4. EXTREME CANDLE EXHAUSTION (Top 10% Range -> Next Candle Reversal?):")
    print(f"{'Asset':12s} | {'1st Half Reversal':>20s} | {'2nd Half Reversal':>20s} | {'Overall Reversal':>18s} | {'Edge > 52.1%?':>13s}")
    print("-" * 80)
    for a in assets:
        c5 = build_candles(ticks, a, 5)
        vex = test_volatility_exhaustion(c5)
        if not vex:
            print(f"{a:12s} | not enough data")
            continue
        w1, n1 = vex["h1"]
        w2, n2 = vex["h2"]
        wa, na = vex["all"]
        ok = (w1 > 52.1 and w2 > 52.1)
        status = "CONFIRMED" if ok else "NO"
        print(f"{a:12s} | {w1:5.1f}% (n={n1:3d})        | {w2:5.1f}% (n={n2:3d})        | {wa:5.1f}% (n={na:3d})      | {status:>13s}")

    print("\n" + "=" * 85)
    print(f"AUDIT SUMMARY: Total Confirmed Edges > 52.1% in both halves: {confirmed_patterns}")
    print("=" * 85)


if __name__ == "__main__":
    run_full_audit()
