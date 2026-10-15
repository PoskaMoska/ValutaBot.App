"""
candle_forensics.py - Candle-by-candle comparison between Pocket Option OTC and Tiingo (real market).

Aligns individual OHLC candles (5s, 15s, 60s) from both sources to test:
1. Direction agreement (green vs red).
2. Direct candle-by-candle table sample.
3. Lagged correlation (does PO lag Tiingo by N seconds?).
4. "Rubber-band" hypothesis (does PO mean-revert to Tiingo when gap widens?).
5. Volatility coupling (do news/spikes in real market reflect in PO?).
6. Internal OTC dynamics (does PO candle follow its own statistical rules?).
"""

import os
import glob
import argparse
from pathlib import Path
import numpy as np
import pandas as pd
import psycopg2

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
DEFAULT_DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"


def pip_size(asset):
    return 0.01 if "JPY" in asset else 0.0001


def load_po_ticks(offset=7200.0):
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    if not files:
        raise FileNotFoundError(f"No tick files found in {DATA}")
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - offset  # convert to UTC timestamp in seconds
    return df


def build_po_candles(po_df, asset, interval_sec):
    d = po_df[po_df["asset"] == asset].copy()
    if len(d) == 0:
        return pd.DataFrame()
    d["candle_ts"] = (d["ts"] // interval_sec) * interval_sec
    grouped = d.groupby("candle_ts")
    candles = grouped["price"].agg(
        open_po="first",
        high_po="max",
        low_po="min",
        close_po="last",
        ticks_po="count"
    ).reset_index()
    candles["asset"] = asset
    candles["interval"] = f"s{interval_sec}"
    return candles


def load_tiingo_candles(asset, interval_str, t_start, t_end):
    db_url = os.environ.get("DATABASE_URL", DEFAULT_DB_URL)
    conn = psycopg2.connect(db_url)
    base_asset = asset.replace("_otc", "").replace("_OTC", "")
    query = """
        SELECT open_time, open_price, high_price, low_price, close_price, volume
        FROM subminute_candles
        WHERE asset = %(asset)s AND interval = %(interval)s
        ORDER BY open_time ASC
    """
    df = pd.read_sql(query, conn, params={"asset": base_asset, "interval": interval_str})
    conn.close()
    if df.empty:
        return pd.DataFrame()
    df["candle_ts"] = pd.to_datetime(df["open_time"], utc=True, format="mixed").astype("int64") // 10**9
    df = df.drop_duplicates("candle_ts").sort_values("candle_ts")
    df = df[(df["candle_ts"] >= t_start) & (df["candle_ts"] <= t_end)]
    return df


def analyze_asset_candles(po_df, asset, interval_sec=5):
    interval_str = f"s{interval_sec}"
    po_candles = build_po_candles(po_df, asset, interval_sec)
    if po_candles.empty:
        print(f"[{asset}] No PO candles.")
        return

    t_start, t_end = po_candles["candle_ts"].min(), po_candles["candle_ts"].max()
    ti_candles = load_tiingo_candles(asset, interval_str, t_start, t_end)
    if ti_candles.empty:
        print(f"[{asset}] No Tiingo candles for interval {interval_str} in range {t_start}..{t_end}.")
        return

    # Merge on exact candle timestamp
    merged = pd.merge(po_candles, ti_candles, on="candle_ts", suffixes=("_po", "_ti"))
    if len(merged) < 20:
        print(f"[{asset}] Too few overlapping candles: {len(merged)}")
        return

    pip = pip_size(asset)
    merged["po_dir"] = np.sign(merged["close_po"] - merged["open_po"])
    merged["ti_dir"] = np.sign(merged["close_price"] - merged["open_price"])
    merged["gap"] = (merged["close_po"] - merged["close_price"]) / pip
    merged["po_return"] = (merged["close_po"] - merged["open_po"]) / pip
    merged["ti_return"] = (merged["close_price"] - merged["open_price"]) / pip
    merged["po_range"] = (merged["high_po"] - merged["low_po"]) / pip
    merged["ti_range"] = (merged["high_price"] - merged["low_price"]) / pip

    non_zero = merged[(merged["po_dir"] != 0) & (merged["ti_dir"] != 0)]
    match_pct = (non_zero["po_dir"] == non_zero["ti_dir"]).mean() * 100 if len(non_zero) > 0 else 0

    print("=" * 80)
    print(f"PAIR: {asset} | Interval: {interval_str} | Overlapping Candles: {len(merged)}")
    print(f"Time Range: {pd.to_datetime(t_start, unit='s', utc=True)} -> {pd.to_datetime(t_end, unit='s', utc=True)}")
    print("=" * 80)

    # 1. Individual Candle Breakdown (Sample of 12 consecutive candles)
    sample = merged.tail(12)
    print("\n--- SAMPLE OF LAST 12 CONSECUTIVE CANDLES ---")
    print(f"{'Time (UTC)':<10} | {'Tiingo O->C':<16} {'Dir':<4} | {'PO OTC O->C':<16} {'Dir':<4} | {'Match?':<6} | {'Gap (pips)':<10}")
    print("-" * 75)
    for _, row in sample.iterrows():
        t_str = pd.to_datetime(row["candle_ts"], unit="s", utc=True).strftime("%H:%M:%S")
        ti_str = f"{row['open_price']:.5f}->{row['close_price']:.5f}"
        po_str = f"{row['open_po']:.5f}->{row['close_po']:.5f}"
        ti_d = "UP" if row["ti_dir"] > 0 else ("DOWN" if row["ti_dir"] < 0 else "FLAT")
        po_d = "UP" if row["po_dir"] > 0 else ("DOWN" if row["po_dir"] < 0 else "FLAT")
        match = "YES" if row["ti_dir"] == row["po_dir"] and row["ti_dir"] != 0 else ("NO" if row["ti_dir"] != 0 and row["po_dir"] != 0 else "FLAT")
        print(f"{t_str:<10} | {ti_str:<16} {ti_d:<4} | {po_str:<16} {po_d:<4} | {match:<6} | {row['gap']:+9.2f}")

    # 2. Contingency Table (Directional agreement)
    print("\n--- DIRECTIONAL CONTINGENCY TABLE ---")
    ct = pd.crosstab(
        merged["ti_dir"].map({1: "Tiingo UP", -1: "Tiingo DOWN", 0: "Tiingo FLAT"}),
        merged["po_dir"].map({1: "PO UP", -1: "PO DOWN", 0: "PO FLAT"}),
        margins=True
    )
    print(ct)
    print(f"\nExact Direction Match (excluding flats): {match_pct:.2f}% (Total non-flat pairs: {len(non_zero)})")

    # 3. Correlation of returns and ranges
    r_corr = np.corrcoef(merged["po_return"], merged["ti_return"])[0, 1]
    vol_corr = np.corrcoef(merged["po_range"], merged["ti_range"])[0, 1]
    print(f"Candle Return Correlation: {r_corr:.4f}")
    print(f"Candle Volatility (Range) Correlation: {vol_corr:.4f}")

    # 4. Rubber-band / Mean-reversion Hypothesis:
    # If PO is far above Tiingo (gap > median), does PO next candle go DOWN?
    # If PO is far below Tiingo (gap < median), does PO next candle go UP?
    gap_median = merged["gap"].median()
    merged["next_po_dir"] = merged["po_dir"].shift(-1)
    high_gap = merged[merged["gap"] > gap_median].dropna(subset=["next_po_dir"])
    low_gap = merged[merged["gap"] < gap_median].dropna(subset=["next_po_dir"])

    p_down_when_high = (high_gap["next_po_dir"] == -1).mean() * 100 if len(high_gap) > 0 else 0
    p_up_when_low = (low_gap["next_po_dir"] == 1).mean() * 100 if len(low_gap) > 0 else 0
    print("\n--- 'RUBBER-BAND' SPREAD REVERSION TEST ---")
    print(f"Median Gap: {gap_median:.2f} pips")
    print(f"When PO > Median Gap, next PO candle is DOWN: {p_down_when_high:.1f}% (n={len(high_gap)})")
    print(f"When PO < Median Gap, next PO candle is UP:   {p_up_when_low:.1f}% (n={len(low_gap)})")

    # 5. Lead/Lag Shift Search:
    print("\n--- LEAD/LAG SHIFT TEST (-30s to +30s) ---")
    best_shift = 0
    best_corr = -1
    for shift_s in range(-30, 35, interval_sec):
        shifted_po = po_candles.copy()
        shifted_po["candle_ts"] += shift_s
        m_s = pd.merge(shifted_po, ti_candles, on="candle_ts")
        if len(m_s) > 20:
            c = np.corrcoef(m_s["close_po"] - m_s["open_po"], m_s["close_price"] - m_s["open_price"])[0, 1]
            if c > best_corr:
                best_corr = c
                best_shift = shift_s
            print(f"  Shift {shift_s:+3d}s: return corr = {c:+.4f} (n={len(m_s)})")
    print(f"Best shift: {best_shift:+d}s (corr = {best_corr:.4f})")

    # 6. Internal OTC Autocorrelation (Is PO predictable on its own?)
    po_c = po_candles.copy()
    po_c["dir"] = np.sign(po_c["close_po"] - po_c["open_po"])
    po_c["prev_dir"] = po_c["dir"].shift(1)
    nz = po_c[(po_c["dir"] != 0) & (po_c["prev_dir"] != 0)]
    continuation_rate = (nz["dir"] == nz["prev_dir"]).mean() * 100 if len(nz) > 0 else 0
    print("\n--- INTERNAL OTC CANDLE MOMENTUM ---")
    print(f"PO OTC Candle Continuation Rate (same direction as previous): {continuation_rate:.1f}% (n={len(nz)})")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--srv-offset", type=float, default=7200.0)
    parser.add_argument("--interval", type=int, default=5, help="Candle interval in seconds (5, 15, 60)")
    parser.add_argument("--asset", type=str, default="EURUSD_otc")
    args = parser.parse_args()

    po_ticks = load_po_ticks(args.srv_offset)
    print(f"Loaded {len(po_ticks):,} PO ticks total across assets: {sorted(po_ticks['asset'].unique())}")

    if args.asset == "ALL":
        for a in sorted(po_ticks["asset"].unique()):
            analyze_asset_candles(po_ticks, a, args.interval)
            print("\n")
    else:
        analyze_asset_candles(po_ticks, args.asset, args.interval)


if __name__ == "__main__":
    main()
