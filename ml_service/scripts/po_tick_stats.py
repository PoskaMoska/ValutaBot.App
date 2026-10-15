"""
po_tick_stats.py - Is the Pocket Option OTC price a random walk? (PO ticks only, no Tiingo)

Per pair, on a 1-second grid built from ticks_*.csv:
  * tick rate, share of zero steps
  * return autocorrelation lags 1..5 (with 95% band)
  * Lo-MacKinlay variance ratio VR(q), q=5,15,30,60 and z-score (VR>1 trending, VR<1 mean-reverting)
  * sign persistence: P(next q-sec move has the same sign as the previous one), q = 5,15,60 (non-overlapping), with 95% band
  * after a LARGE move (top 10% |move|): continuation probability

Usage: py -3 po_tick_stats.py
"""
import glob
from pathlib import Path
import numpy as np
import pandas as pd

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"


def grid_series(d):
    t = d["ts"].to_numpy(); p = d["price"].to_numpy()
    g = np.arange(int(np.ceil(t[0])), int(t[-1]))
    idx = np.searchsorted(t, g, side="right") - 1
    return g, p[idx]


def autocorr(x, k):
    x = x - x.mean()
    return float((x[:-k] * x[k:]).sum() / (x * x).sum())


def variance_ratio(r, q):
    n = len(r); mu = r.mean()
    s1 = ((r - mu) ** 2).sum() / (n - 1)
    rq = np.convolve(r, np.ones(q), mode="valid")
    sq = ((rq - q * mu) ** 2).sum() / (len(rq) * q)
    vr = sq / s1
    z = (vr - 1) / np.sqrt(2 * (2 * q - 1) * (q - 1) / (3 * q * n))
    return vr, z


def persistence(lp, q):
    x = lp[::q]
    m = np.diff(x)
    a, b = m[:-1], m[1:]
    ok = (a != 0) & (b != 0)
    n = int(ok.sum())
    if n < 30:
        return None
    p = float((np.sign(a[ok]) == np.sign(b[ok])).mean())
    return p, 0.98 / np.sqrt(n), n, a, b, ok


def main():
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in sorted(glob.glob(str(DATA / "ticks_*.csv")))]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - 7200.0
    for asset, d in df.groupby("asset"):
        g, p = grid_series(d)
        lp = np.log(p)
        r = np.diff(lp)
        if len(r) < 200:
            print(f"{asset}: only {len(r)} s of data"); continue
        dts = np.diff(d["ts"].to_numpy())
        zero = float((np.diff(d["price"].to_numpy()) == 0).mean())
        print(f"\n== {asset}: {len(d):,} ticks, {len(r)/60:.0f} min | tick every {np.median(dts):.2f}s | zero-step share {zero*100:.0f}%")
        ac = [autocorr(r, k) for k in range(1, 6)]
        band = 1.96 / np.sqrt(len(r))
        print("  autocorr(1s ret) lags1-5: " + " ".join(f"{a:+.3f}" for a in ac) + f"   (95% band ±{band:.3f})")
        vrs = []
        for q in (5, 15, 30, 60):
            vr, z = variance_ratio(r, q)
            vrs.append(f"VR({q})={vr:.2f} z={z:+.1f}")
        print("  " + "  ".join(vrs))
        out = []
        for q in (5, 15, 60):
            res = persistence(lp, q)
            if res:
                pr, bd, n, a, b, ok = res
                big = ok & (np.abs(a) >= np.quantile(np.abs(a[ok]), 0.9))
                cont_big = (np.sign(a[big]) == np.sign(b[big])).mean() if big.sum() >= 20 else float("nan")
                out.append(f"q={q}s: same-sign {pr*100:.1f}% ±{bd*100:.1f} (n={n}), after big move {cont_big*100:.0f}% (n={int(big.sum())})")
        print("  " + " | ".join(out))


if __name__ == "__main__":
    main()
