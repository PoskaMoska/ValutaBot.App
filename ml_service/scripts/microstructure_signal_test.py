"""
microstructure_signal_test.py - Is there ANY predictable direction in s5 data at 5s..60s horizons?

Dense sampling (every s5 candle is a sample, not only bot trades), strictly causal features,
chronological train/test split, LightGBM. Reports AUC / accuracy / top-confidence accuracy per horizon,
and the same with an entry delay of 1 candle (5s) to mimic human execution latency.

Usage:  set DATABASE_URL=postgresql://...   &&   py -3 microstructure_signal_test.py [--interval s5]
"""
import os
import argparse
import numpy as np
import pandas as pd
import psycopg2
import lightgbm as lgb
from sklearn.metrics import roc_auc_score

HORIZONS = (1, 2, 3, 6, 12)      # candles of 5s -> 5s,10s,15s,30s,60s
DELAYS = (0, 1)                  # entry delay in candles (0 = ideal, 1 = ~5s human latency)
LAGS = (1, 2, 3, 6, 12, 24, 60)


def load(interval):
    conn = psycopg2.connect(os.environ["DATABASE_URL"])
    df = pd.read_sql(
        "SELECT asset, open_time, open_price o, high_price h, low_price l, close_price c, volume v "
        "FROM subminute_candles WHERE interval = %(i)s", conn, params={"i": interval})
    conn.close()
    df["t"] = pd.to_datetime(df["open_time"], utc=True, format="mixed")
    df = df.drop(columns=["open_time"]).drop_duplicates(["asset", "t"]).sort_values(["asset", "t"])
    return df.reset_index(drop=True)


def build(df, step_sec):
    out = []
    for asset, g in df.groupby("asset"):
        g = g.copy().reset_index(drop=True)
        grid = (g["t"].astype("int64") // 10**9 // step_sec).to_numpy()
        c = g["c"].to_numpy(float); o = g["o"].to_numpy(float)
        h = g["h"].to_numpy(float); l = g["l"].to_numpy(float); v = g["v"].to_numpy(float)
        r1 = np.r_[0.0, np.diff(np.log(c))]
        sd = pd.Series(r1).rolling(120, min_periods=60).std().to_numpy()
        sd = np.where(sd > 0, sd, np.nan)
        f = {"asset": np.full(len(g), asset), "t": g["t"].to_numpy(), "grid": grid}
        s = pd.Series(c)
        for k in LAGS:
            f[f"ret{k}"] = (np.log(c) - np.log(s.shift(k).to_numpy())) / (sd * np.sqrt(k))
        rng = np.where(h > l, h - l, np.nan)
        f["body"] = (c - o) / rng
        f["upw"] = (h - np.maximum(o, c)) / rng
        f["loww"] = (np.minimum(o, c) - l) / rng
        f["rng_z"] = np.log(rng / c) - np.log(pd.Series(rng / c).rolling(120, min_periods=60).mean().to_numpy())
        f["vol_z"] = v / pd.Series(v).rolling(120, min_periods=60).mean().to_numpy()
        f["tickcnt"] = v
        f["sd"] = sd
        for k in (60, 360):
            m = s.rolling(k, min_periods=k // 2).mean().to_numpy()
            f[f"dev{k}"] = (c - m) / (c * sd)
        f["up_streak"] = pd.Series(np.sign(r1)).groupby((np.sign(r1) != pd.Series(np.sign(r1)).shift()).cumsum()).cumsum().to_numpy()
        tt = pd.DatetimeIndex(g["t"])
        f["hsin"] = np.sin(2 * np.pi * (tt.hour + tt.minute / 60) / 24)
        f["hcos"] = np.cos(2 * np.pi * (tt.hour + tt.minute / 60) / 24)
        # gap-free lookback (60 candles) validity
        ok_back = np.r_[np.zeros(60, bool), (grid[60:] - grid[:-60]) == 60]
        for d in DELAYS:
            for hz in HORIZONS:
                n = len(g); a = d; b = d + hz
                y = np.full(n, np.nan)
                if n > b:
                    m_ = n - b
                    fut = np.full(n, np.nan); fut[:m_] = c[b:] - c[a:a + m_]
                    gap_ok = np.zeros(n, bool); gap_ok[:m_] = (grid[b:] - grid[a:a + m_]) == hz
                    fut = np.where(gap_ok, fut, np.nan)
                    y = fut
                f[f"y_d{d}_h{hz}"] = y
        f["ok_back"] = ok_back
        out.append(pd.DataFrame(f))
    return pd.concat(out, ignore_index=True)


def evaluate(data, feats, d, hz, cutoff):
    col = f"y_d{d}_h{hz}"
    x = data[data["ok_back"] & data[col].notna()].copy()
    flat = (x[col] == 0).mean()
    x = x[x[col] != 0]
    x["lab"] = (x[col] > 0).astype(int)
    tr = x[x["t"] < cutoff]; te = x[x["t"] >= cutoff]
    # inner validation tail for early stopping (last 15% of train, with embargo)
    vcut = tr["t"].quantile(0.85)
    emb = pd.Timedelta(seconds=5 * (hz + d + 2))
    fit = tr[tr["t"] < vcut - emb]; val = tr[tr["t"] >= vcut]
    X = lambda z: z[feats].assign(asset=z["asset"].astype("category"))
    cats = sorted(data["asset"].unique())
    def prep(z):
        zz = z[feats].copy(); zz["asset"] = pd.Categorical(z["asset"], categories=cats); return zz
    m = lgb.LGBMClassifier(n_estimators=400, learning_rate=0.03, num_leaves=15, min_child_samples=300,
                           subsample=0.8, subsample_freq=1, colsample_bytree=0.8, reg_lambda=5.0, verbose=-1)
    m.fit(prep(fit), fit["lab"], eval_set=[(prep(val), val["lab"])], callbacks=[lgb.early_stopping(30, verbose=False)])
    p = m.predict_proba(prep(te))[:, 1]
    auc = roc_auc_score(te["lab"], p)
    conf = np.abs(p - 0.5); thr = np.quantile(conf, 0.9)
    top = conf >= thr
    acc_all = ((p > 0.5) == te["lab"].to_numpy()).mean()
    acc_top = ((p[top] > 0.5) == te["lab"].to_numpy()[top]).mean()
    n = len(te)
    return dict(delay=d * 5, horizon=hz * 5, n_test=n, flat_share=round(flat, 3), auc=round(auc, 4),
                auc_se=round(0.5 / np.sqrt(n / 2) * 0.58, 4), acc=round(acc_all * 100, 2),
                acc_top10=round(acc_top * 100, 2), n_top=int(top.sum()), best_iter=m.best_iteration_ or 0)


def add_cross(data):
    """Lead-lag: returns of the OTHER assets over the same last k candles (own asset column left NaN)."""
    assets = sorted(data["asset"].unique())
    for k in (1, 3, 6):
        piv = data.pivot_table(index="grid", columns="asset", values=f"ret{k}", aggfunc="last")
        for a in assets:
            col = f"x{k}_{a}"
            data[col] = data["grid"].map(piv[a])
            data.loc[data["asset"] == a, col] = np.nan
    return data


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--interval", default="s5")
    ap.add_argument("--cross", action="store_true", help="add cross-asset lead-lag features")
    args = ap.parse_args()
    step = int("".join(ch for ch in args.interval if ch.isdigit()))
    df = load(args.interval)
    print(f"loaded {len(df):,} rows, {df['asset'].nunique()} assets, {df['t'].min()} -> {df['t'].max()}")
    data = build(df, step)
    if args.cross:
        data = add_cross(data)
    feats = [c for c in data.columns if c not in ("asset", "t", "ok_back", "grid") and not c.startswith("y_")]
    cutoff = data["t"].quantile(0.7)
    print(f"features={len(feats)}  test starts {cutoff}")
    rows = []
    for d in DELAYS:
        for hz in HORIZONS:
            r = evaluate(data, feats, d, hz, cutoff)
            rows.append(r); print(r, flush=True)
    res = pd.DataFrame(rows)
    print("\n", res.to_string(index=False))
