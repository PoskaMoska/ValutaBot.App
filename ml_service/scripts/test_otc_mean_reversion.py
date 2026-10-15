"""
test_otc_mean_reversion.py - Test if PO OTC mean-reverts to its own moving averages or Bollinger Bands.
"""

import glob
from pathlib import Path
import numpy as np
import pandas as pd

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"


def load_po():
    files = sorted(glob.glob(str(DATA / "ticks_*.csv")))
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in files]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0
    return df


def build_candles(df, asset, interval_sec=5):
    d = df[df["asset"] == asset].copy()
    d["candle_ts"] = (d["ts"] // interval_sec) * interval_sec
    g = d.groupby("candle_ts")["price"].agg(open="first", high="max", low="min", close="last").reset_index()
    return g


def test_bollinger_reversion(candles, window=20, num_std=2.0):
    c = candles.copy()
    c["sma"] = c["close"].rolling(window).mean()
    c["std"] = c["close"].rolling(window).std()
    c["upper"] = c["sma"] + num_std * c["std"]
    c["lower"] = c["sma"] - num_std * c["std"]
    
    c["next_ret"] = c["close"].shift(-1) - c["close"]
    
    # Touch upper band
    touch_upper = c[c["high"] >= c["upper"]].dropna(subset=["next_ret"])
    touch_lower = c[c["low"] <= c["lower"]].dropna(subset=["next_ret"])
    
    p_down_upper = (touch_upper["next_ret"] < 0).mean() * 100 if len(touch_upper) > 0 else 0
    p_up_lower = (touch_lower["next_ret"] > 0).mean() * 100 if len(touch_lower) > 0 else 0
    
    return len(touch_upper), p_down_upper, len(touch_lower), p_up_lower


def main():
    po = load_po()
    assets = sorted(po["asset"].unique())
    print(f"Testing Mean-Reversion across {len(assets)} assets...")
    print(f"{'Asset':12s} | {'BB(20,2) Touch Upper':22s} | {'BB(20,2) Touch Lower':22s}")
    for a in assets:
        c = build_candles(po, a, 5)
        n_up, p_dn, n_dn, p_up = test_bollinger_reversion(c, 20, 2.0)
        print(f"{a:12s} | n={n_up:3d}, P(Down)={p_dn:5.1f}%     | n={n_dn:3d}, P(Up)={p_up:5.1f}%")


if __name__ == "__main__":
    main()
