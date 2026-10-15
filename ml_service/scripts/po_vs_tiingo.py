"""
po_vs_tiingo.py - How closely does Pocket Option's OTC price follow the Tiingo feed the bot uses?

Reads ml_service/data/po_ticks/ticks_*.csv (recv_ms, asset, srv_ts, price), aligns with Tiingo s5 candles from the DB and reports, per pair:
  * level gap PO - Tiingo (pips): mean / std  (is OTC anchored to the real market or wandering on its own?)
  * return correlation on 5s grid for time shifts k = -10..+10 s (k>0: PO lags Tiingo, i.e. Tiingo LEADS) -> best lag
  * direction agreement of PO vs Tiingo returns over 5s / 15s / 60s at the best lag
  * lead-lag edge: accuracy of predicting the NEXT PO move from the last Tiingo move

Usage:  set DATABASE_URL=...   &&   py -3 po_vs_tiingo.py [--srv-offset 7200]
"""
import os
import glob
import argparse
from pathlib import Path
import numpy as np
import pandas as pd
import psycopg2

DATA = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
STEP = 5


def pip(asset):
    return 0.01 if "JPY" in asset else 0.0001


def load_po(offset):
    parts = [pd.read_csv(f, header=None, names=["recv", "asset", "srv", "price"]) for f in sorted(glob.glob(str(DATA / "ticks_*.csv")))]
    df = pd.concat(parts, ignore_index=True).drop_duplicates(["asset", "srv"]).sort_values(["asset", "srv"])
    df["ts"] = df["srv"] - offset          # server clock is shifted by +2h
    return df


def load_tiingo(assets, t0, t1):
    db_url = os.environ.get("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")
    conn = psycopg2.connect(db_url)
    out = {}
    for a in assets:
        d = pd.read_sql("SELECT open_time, close_price c FROM subminute_candles WHERE asset=%(a)s AND interval='s5'",
                        conn, params={"a": a})
        d["t"] = pd.to_datetime(d["open_time"], utc=True, format="mixed").astype("int64") // 10**9
        d = d.drop_duplicates("t").sort_values("t")
        d = d[(d["t"] >= t0 - 60) & (d["t"] <= t1 + 60)]
        # price AT grid time g = close of the candle that started at g-5
        out[a] = pd.Series(d["c"].to_numpy(), index=d["t"].to_numpy() + STEP)
    conn.close()
    return out


def po_price_at(po_ts, po_px, grid):
    idx = np.searchsorted(po_ts, grid, side="right") - 1
    ok = idx >= 0
    px = np.full(len(grid), np.nan)
    px[ok] = po_px[idx[ok]]
    # drop points where the last tick is older than 3 s (stale)
    age = np.full(len(grid), np.inf)
    age[ok] = grid[ok] - po_ts[idx[ok]]
    px[age > 3] = np.nan
    return px


def corr(a, b):
    m = np.isfinite(a) & np.isfinite(b)
    if m.sum() < 50 or a[m].std() == 0 or b[m].std() == 0:
        return np.nan, int(m.sum())
    return float(np.corrcoef(a[m], b[m])[0, 1]), int(m.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--srv-offset", type=float, default=7200.0)
    args = ap.parse_args()
    po = load_po(args.srv_offset)
    pairs = sorted(po["asset"].unique())
    t0, t1 = int(po["ts"].min()), int(po["ts"].max())
    print(f"PO ticks: {len(po):,}  span {(t1 - t0) / 60:.1f} min  ({pd.to_datetime(t0, unit='s', utc=True)} -> {pd.to_datetime(t1, unit='s', utc=True)})")
    base = [p.replace("_otc", "") for p in pairs]
    ti = load_tiingo(sorted(set(base)), t0, t1)
    print(f"{'pair':8s} {'ticks':>7s} {'gap_mean':>9s} {'gap_std':>8s} | {'best_k':>6s} {'corr@k':>7s} {'corr@0':>7s} | agree5s agree15s agree60s | lead:Tiingo->next PO (acc, n)")
    for p in pairs:
        b = p.replace("_otc", "")
        d = po[po["asset"] == p]
        pts = d["ts"].to_numpy(); ppx = d["price"].to_numpy()
        s = ti[b]
        grid = np.arange(int(np.ceil(max(pts[0], s.index.min()) / STEP) * STEP), int(min(pts[-1], s.index.max())), STEP)
        if len(grid) < 40:
            print(f"{p:8s} not enough overlap"); continue
        tp = s.reindex(grid).to_numpy()
        gap = (po_price_at(pts, ppx, grid) - tp) / pip(b)
        gm, gs = np.nanmean(gap), np.nanstd(gap)
        r_ti = np.log(tp[1:]) - np.log(tp[:-1])
        best = (None, -2)
        c0 = np.nan
        for k in range(-10, 11):
            pk = po_price_at(pts, ppx, grid + k)
            r_po = np.log(pk[1:]) - np.log(pk[:-1])
            c, n = corr(r_po, r_ti)
            if k == 0:
                c0 = c
            if np.isfinite(c) and c > best[1]:
                best = (k, c)
        k = best[0] if best[0] is not None else 0
        pk = po_price_at(pts, ppx, grid + k)
        agree = []
        for h in (1, 3, 12):
            a = np.log(pk[h:]) - np.log(pk[:-h]); t = np.log(tp[h:]) - np.log(tp[:-h])
            m = np.isfinite(a) & np.isfinite(t) & (a != 0) & (t != 0)
            agree.append(f"{(np.sign(a[m]) == np.sign(t[m])).mean() * 100:5.1f}%" if m.sum() > 30 else "  n/a")
        # lead-lag edge: sign of last Tiingo 5s move vs next PO 5s move (PO sampled at the same instants)
        p0 = po_price_at(pts, ppx, grid)
        nxt = np.log(p0[2:]) - np.log(p0[1:-1])
        lastti = np.log(tp[1:-1]) - np.log(tp[:-2])
        m = np.isfinite(nxt) & np.isfinite(lastti) & (nxt != 0) & (lastti != 0)
        lead = f"{(np.sign(nxt[m]) == np.sign(lastti[m])).mean() * 100:5.1f}% n={int(m.sum())}" if m.sum() > 30 else "n/a"
        print(f"{p:8s} {len(d):7d} {gm:9.2f} {gs:8.2f} | {k:6d} {best[1]:7.3f} {c0:7.3f} | {'  '.join(agree)} | {lead}")


if __name__ == "__main__":
    main()
