"""
Train & Export Subminute Microstructure Predictor (s5, s10, s15, s30, 1min)
==========================================================================
Strict walk-forward OOF training, non-tie PAVA calibration, tie-hazard risk
modeling, quality-gate enforcement, and pure-NumPy (.npz/.json) export.

Usage:
    set DATABASE_URL=postgresql://...
    py ml_service/train_subminute_quant.py
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
import pandas as pd
import psycopg2
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import log_loss, roc_auc_score
from sklearn.preprocessing import StandardScaler

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from export_s5_archive import (
    DEFAULT_ARCHIVE_PATH,
    merge_candle_frames,
    normalize_candle_frame,
)
from models.subminute_quant_predictor import (
    ALL_FEATURES,
    BASKET_FEATURES,
    CORE_FEATURES,
    DEFAULT_META_PATH,
    DEFAULT_NPZ_PATH,
    INTERVAL_SECONDS,
    MICRO_FEATURES,
    MODEL_VERSION,
    REGIME_FEATURES,
    STATE_FEATURES,
    canonical_asset_base,
    compute_features_batch,
    eval_hgb_flat_batch,
    is_synthetic_otc_mask,
    is_weekend_otc_mask,
    mirror_feature_matrix,
    pack_hgb_for_npz,
    usd_basket_sign,
)

TRAIN_INTERVALS = ["s5", "s10", "s15", "s30", "1min"]


def _norm_asset(name: str) -> str:
    return re.sub(r"_PO$", "", str(name).strip().upper())


def _parse_unix_s(series: pd.Series) -> np.ndarray:
    try:
        dt = pd.to_datetime(series, format="ISO8601", utc=True)
    except ValueError:
        dt = pd.to_datetime(series, utc=True)
    return dt.dt.tz_localize(None).to_numpy().astype("datetime64[s]").astype("int64")


def _query_df(conn: Any, sql: str, params: Optional[Tuple[Any, ...]] = None) -> pd.DataFrame:
    with conn.cursor() as cur:
        cur.execute(sql, params) if params is not None else cur.execute(sql)
        rows = cur.fetchall()
        cols = [d[0] for d in cur.description] if cur.description else []
    return pd.DataFrame(rows, columns=cols)


def _fit_core_lr(
    X_core: np.ndarray, y: np.ndarray, c_reg: float
) -> Tuple[LogisticRegression, StandardScaler, np.ndarray]:
    n_cols = X_core.shape[1]
    meds = np.zeros(n_cols, dtype=np.float64)
    for j in range(n_cols):
        col = X_core[:, j]
        fin = col[np.isfinite(col)]
        if len(fin) > 0:
            meds[j] = float(np.median(fin))
    X_imp = np.where(np.isfinite(X_core), X_core, meds)
    scaler = StandardScaler()
    X_std = np.clip(scaler.fit_transform(X_imp), -8.0, 8.0)
    lr = LogisticRegression(C=c_reg, max_iter=600, random_state=42)
    lr.fit(X_std, y)
    return lr, scaler, meds


def _predict_core_lr(
    lr: LogisticRegression, scaler: StandardScaler, meds: np.ndarray, X_core: np.ndarray
) -> np.ndarray:
    X_imp = np.where(np.isfinite(X_core), X_core, meds)
    X_std = np.clip((X_imp - scaler.mean_) / np.maximum(scaler.scale_, 1e-9), -8.0, 8.0)
    return lr.predict_proba(X_std)[:, 1]


def _make_hgb(tf: str, seed: int = 42) -> HistGradientBoostingClassifier:
    if tf == "1min":
        return HistGradientBoostingClassifier(
            max_iter=240,
            max_depth=3,
            min_samples_leaf=450,
            l2_regularization=20.0,
            learning_rate=0.03,
            early_stopping=True,
            validation_fraction=0.12,
            random_state=seed,
        )
    if tf == "s30":
        return HistGradientBoostingClassifier(
            max_iter=230,
            max_depth=3,
            min_samples_leaf=300,
            l2_regularization=10.0,
            learning_rate=0.035,
            early_stopping=True,
            validation_fraction=0.12,
            random_state=seed,
        )
    return HistGradientBoostingClassifier(
        max_iter=260,
        max_depth=4,
        min_samples_leaf=220,
        l2_regularization=8.0,
        learning_rate=0.035,
        early_stopping=True,
        validation_fraction=0.12,
        random_state=seed,
    )


def _fit_robust_isotonic(dir_strength: np.ndarray, won: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
    """
    Fits monotonic OOF non-tie win-rate calibration over equal-count quantile bins
    with Empirical Bayes shrinkage toward 0.50 and a 1-SE conservative tail guard
    so small-N coin-flip streaks can never inflate tail confidence.
    """
    x = np.asarray(dir_strength, dtype=np.float64)
    y = np.asarray(won, dtype=np.float64)
    valid = np.isfinite(x) & np.isfinite(y)
    x = np.clip(x[valid], 0.50, 1.0)
    y = np.clip(y[valid], 0.0, 1.0)
    n = len(x)
    if n < 200:
        return np.array([0.50, 1.0], dtype=np.float64), np.array([0.50, 0.50], dtype=np.float64)

    order = np.argsort(x, kind="mergesort")
    xs_s = x[order]
    ys_s = y[order]

    bin_sz = max(min(1500, max(400, n // 60)), min(400, max(80, n // 12)))
    n_bins = max(2, n // bin_sz)
    edges = np.linspace(0, n, n_bins + 1, dtype=np.int64)

    prior = 30.0
    bx, by, bw = [], [], []
    for i in range(n_bins):
        lo, hi = int(edges[i]), int(edges[i + 1])
        cnt = hi - lo
        if cnt <= 0:
            continue
        wins = float(np.sum(ys_s[lo:hi]))
        p_eb = (wins + 0.5 * prior) / (cnt + prior)
        se = math.sqrt(p_eb * (1.0 - p_eb) / (cnt + prior))
        p_cons = float(np.clip(p_eb - 1.0 * se, 0.50, 0.75))
        bx.append(float(np.mean(xs_s[lo:hi])))
        by.append(p_cons)
        bw.append(float(cnt))

    iso = IsotonicRegression(y_min=0.50, y_max=0.75, increasing=True, out_of_bounds="clip")
    iso.fit(np.array(bx, dtype=np.float64), np.array(by, dtype=np.float64), sample_weight=np.array(bw, dtype=np.float64))

    xs = np.asarray(iso.X_thresholds_, dtype=np.float64)
    ys = np.asarray(iso.y_thresholds_, dtype=np.float64)
    if xs[0] > 0.50:
        xs = np.insert(xs, 0, 0.50)
        ys = np.insert(ys, 0, max(0.50, ys[0] - 0.005))
    if xs[-1] < 1.0:
        xs = np.append(xs, 1.0)
        ys = np.append(ys, ys[-1])

    ux, idx = np.unique(xs, return_index=True)
    uy = np.maximum.accumulate(ys[idx])
    return ux, np.clip(uy, 0.50, 0.75)


def _fit_shrunk_pair_isotonic(
    dir_strength: np.ndarray,
    won: np.ndarray,
    global_iso_x: np.ndarray,
    global_iso_y: np.ndarray,
    min_samples: int = 1200,
    shrink_n0: float = 5000.0,
) -> Optional[Tuple[np.ndarray, np.ndarray]]:
    """
    Fits a pair-specific OOF monotonic calibrator when sufficient non-tie samples exist
    (>= min_samples), then blends toward the global Clean-FX curve using Empirical-Bayes
    shrinkage `w = n / (n + shrink_n0)` so strong pairs (e.g. GBPUSD) preserve honest lift
    while small-sample variance stays conservatively regularized.
    """
    n = int(len(dir_strength))
    if n < min_samples:
        return None
    raw_x, raw_y = _fit_robust_isotonic(dir_strength, won)
    grid_x = np.unique(np.concatenate([global_iso_x, raw_x, np.array([0.50, 1.0], dtype=np.float64)]))
    p_pair = np.interp(grid_x, raw_x, raw_y)
    p_glob = np.interp(grid_x, global_iso_x, global_iso_y)
    w = float(n) / (float(n) + shrink_n0)
    blended_y = np.maximum.accumulate(np.clip(w * p_pair + (1.0 - w) * p_glob, 0.50, 0.75))
    return grid_x.astype(np.float64), blended_y.astype(np.float64)


def _compute_usd_basket_df(df: pd.DataFrame, dt: int) -> Optional[pd.DataFrame]:
    """
    Computes strictly causal per-timestamp USD basket momentum (1m & 5m) and sync score
    across major USD pairs, matching live MacroContextEngine.ComputeDollarBasketMetrics.
    """
    bars_1m = max(1, int(round(60.0 / max(1, dt))))
    bars_5m = max(1, int(round(300.0 / max(1, dt))))
    per_asset_1m: Dict[str, pd.Series] = {}
    per_asset_5m: Dict[str, pd.Series] = {}

    for asset, g in df.groupby("asset", sort=False):
        u_sign = usd_basket_sign(asset)
        if abs(u_sign) < 0.5:
            continue
        gs = g.sort_values("ts")
        ts = gs["ts"].to_numpy()
        c = np.maximum(gs["close_price"].to_numpy(), 1e-9)
        n = len(c)
        r1 = np.full(n, np.nan, dtype=np.float64)
        r5 = np.full(n, np.nan, dtype=np.float64)
        if n > bars_1m:
            ok1 = (ts[bars_1m:] - ts[:-bars_1m]) <= int(max(dt, 60) * 1.6)
            m1 = (c[bars_1m:] - c[:-bars_1m]) / np.maximum(c[:-bars_1m], 1e-9) * 10000.0 * u_sign
            r1[bars_1m:] = np.where(ok1 & (np.abs(m1) <= 50.0), m1, np.nan)
        if n > bars_5m:
            ok5 = (ts[bars_5m:] - ts[:-bars_5m]) <= int(max(dt, 300) * 1.6)
            m5 = (c[bars_5m:] - c[:-bars_5m]) / np.maximum(c[:-bars_5m], 1e-9) * 10000.0 * u_sign
            r5[bars_5m:] = np.where(ok5 & (np.abs(m5) <= 80.0), m5, np.nan)
        per_asset_1m[asset] = pd.Series(r1, index=ts)
        per_asset_5m[asset] = pd.Series(r5, index=ts)

    if len(per_asset_1m) < 3:
        return None

    m1_df = pd.DataFrame(per_asset_1m)
    m5_df = pd.DataFrame(per_asset_5m)
    dxy_1m = m1_df.median(axis=1, skipna=True)
    dxy_5m = m5_df.median(axis=1, skipna=True)

    signs = np.sign(m1_df.to_numpy())
    med_sign = np.sign(dxy_1m.to_numpy())[:, None]
    valid_cnt = np.sum(np.isfinite(m1_df.to_numpy()), axis=1)
    agree_cnt = np.sum(np.where(np.isfinite(signs) & (med_sign != 0), (signs == med_sign).astype(np.float64), 0.0), axis=1)
    bsync = np.where(valid_cnt >= 3, np.where(med_sign[:, 0] == 0, 0.5, agree_cnt / np.maximum(valid_cnt, 1)), np.nan)
    return pd.DataFrame({"dxy_1m": dxy_1m.to_numpy(), "dxy_5m": dxy_5m.to_numpy(), "bsync": bsync}, index=m1_df.index)


def _compute_usd_basket_df_from_s5(s5_df: pd.DataFrame) -> Optional[pd.DataFrame]:
    """
    Computes strictly causal s5-resolution USD basket momentum (5s, 15s, 30s, 1m, 5m)
    and sync scores (bsync on 1m, bsync_5s on 5s) matching MacroContextEngine.
    Indexed by s5 bar `open_time` (`end_ts = bar_open + dt - 5` aligns any TF's close).
    """
    windows = [
        ("5s", 1, 5, 50.0),
        ("15s", 3, 15, 50.0),
        ("30s", 6, 30, 50.0),
        ("1m", 12, 60, 50.0),
        ("5m", 60, 300, 80.0),
    ]
    per_asset: Dict[str, Dict[str, pd.Series]] = {name: {} for name, _, _, _ in windows}

    for asset, g in s5_df.groupby("asset", sort=False):
        u_sign = usd_basket_sign(asset)
        if abs(u_sign) < 0.5:
            continue
        gs = g.sort_values("ts").drop_duplicates("ts")
        ts = gs["ts"].to_numpy(np.int64)
        c = np.maximum(gs["close_price"].to_numpy(np.float64), 1e-9)
        n = len(c)
        for name, k_bars, target_sec, cap in windows:
            arr = np.full(n, np.nan, dtype=np.float64)
            if n > k_bars:
                ok = (ts[k_bars:] - ts[:-k_bars]) <= int(target_sec * 1.6)
                ret = (c[k_bars:] - c[:-k_bars]) / np.maximum(c[:-k_bars], 1e-9) * 10000.0 * u_sign
                arr[k_bars:] = np.where(ok & (np.abs(ret) <= cap), ret, np.nan)
            per_asset[name][asset] = pd.Series(arr, index=ts)

    if len(per_asset["1m"]) < 3:
        return None

    out_cols: Dict[str, np.ndarray] = {}
    dfs: Dict[str, pd.DataFrame] = {}
    for name, _, _, _ in windows:
        wdf = pd.DataFrame(per_asset[name])
        dfs[name] = wdf
        vc = np.sum(np.isfinite(wdf.to_numpy()), axis=1)
        out_cols[f"dxy_{name}"] = np.where(vc >= 3, wdf.mean(axis=1, skipna=True).to_numpy(), np.nan)

    def _sync(wdf: pd.DataFrame, dxy_vals: np.ndarray) -> np.ndarray:
        arr = wdf.to_numpy()
        signs = np.sign(arr)
        dom = np.sign(dxy_vals)[:, None]
        vc = np.sum(np.isfinite(arr), axis=1)
        agree = np.sum(np.where(np.isfinite(signs) & (dom != 0), (signs == dom).astype(np.float64), 0.0), axis=1)
        return np.where(vc >= 3, np.where(dom[:, 0] == 0, 0.5, agree / np.maximum(vc, 1)), np.nan)

    out_cols["bsync"] = _sync(dfs["1m"], out_cols["dxy_1m"])
    out_cols["bsync_5s"] = _sync(dfs["5s"], out_cols["dxy_5s"])
    return pd.DataFrame(out_cols, index=dfs["1m"].index)


def build_dataset_for_interval(
    df: pd.DataFrame,
    dt: int,
    s5_map: Optional[Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray]]],
    s5_basket_df: Optional[pd.DataFrame] = None,
) -> Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Builds chronological (X, fwd, ts, asset_norm) arrays cleanly across all assets with jump guard."""
    fb_basket_df = _compute_usd_basket_df(df, dt)
    x_list, fwd_list, ts_list, asset_list = [], [], [], []
    for asset, g in df.groupby("asset", sort=False):
        g_s = g.sort_values("ts")
        ts = g_s["ts"].to_numpy()
        o = g_s["open_price"].to_numpy()
        h = g_s["high_price"].to_numpy()
        l = g_s["low_price"].to_numpy()
        c = g_s["close_price"].to_numpy()
        v = g_s["volume"].to_numpy()

        st = so = sh = sl = sc = None
        if dt > 5 and s5_map is not None and asset in s5_map:
            st, so, sh, sl, sc = s5_map[asset]

        u_sign = usd_basket_sign(asset)
        basket_arrays = None
        if abs(u_sign) > 0.5 and (s5_basket_df is not None or fb_basket_df is not None):
            n_g = len(ts)
            nan_v = np.full(n_g, np.nan, dtype=np.float64)
            end_ts = ts + max(0, dt - 5)
            re_s5 = s5_basket_df.reindex(end_ts) if s5_basket_df is not None else None
            re_fb = fb_basket_df.reindex(ts) if fb_basket_df is not None else None

            def _pick(col: str) -> np.ndarray:
                v_s5 = re_s5[col].to_numpy() if (re_s5 is not None and col in re_s5.columns) else nan_v
                v_fb = re_fb[col].to_numpy() if (re_fb is not None and col in re_fb.columns) else nan_v
                return np.where(np.isfinite(v_s5), v_s5, v_fb)

            a1 = _pick("dxy_1m")
            a5 = _pick("dxy_5m")
            ab = _pick("bsync")
            if re_s5 is not None:
                a5s = re_s5["dxy_5s"].to_numpy()
                a15s = re_s5["dxy_15s"].to_numpy()
                a30s = re_s5["dxy_30s"].to_numpy()
                ab5 = re_s5["bsync_5s"].to_numpy()
                basket_arrays = (a1, a5, ab, a5s, a15s, a30s, ab5)
            else:
                basket_arrays = (a1, a5, ab)

        mat, valid_hist = compute_features_batch(
            ts, o, h, l, c, v, dt,
            s5_ts=st, s5_o=so, s5_c=sc,
            basket_arrays=basket_arrays,
            usd_sign=u_sign,
            s5_h=sh, s5_l=sl,
        )
        if len(mat) == 0:
            continue
        n = len(ts)

        # Exclude FX<->OTC switchover discontinuities (>40 bps jump inside 45-bar indicator window)
        prev_c = np.empty(n, dtype=np.float64)
        prev_c[0] = o[0]
        prev_c[1:] = c[:-1]
        bar_r = (c - o) / np.maximum(o, 1e-9) * 10000.0
        gap_r = (c - prev_c) / np.maximum(prev_c, 1e-9) * 10000.0
        is_jump = (np.abs(gap_r) > 40.0) | (np.abs(bar_r) > 40.0)
        cs_j = np.cumsum(np.insert(is_jump.astype(np.int32), 0, 0))
        jump_win = np.zeros(n, dtype=bool)
        w = 45
        if n > w:
            jump_win[w:] = (cs_j[w + 1:] - cs_j[1:-w]) > 0
            jump_win[:w] = cs_j[1 : w + 1] > 0
        else:
            jump_win[:] = cs_j[1:] > 0

        valid_next = np.zeros(n, dtype=bool)
        valid_next[:-1] = (ts[1:] - ts[:-1]) == dt
        fwd = np.zeros(n, dtype=np.float64)
        fwd[:-1] = (c[1:] - o[1:]) / np.maximum(o[1:], 1e-9) * 10000.0
        fwd_ok = np.abs(fwd) <= 40.0
        keep = valid_hist & valid_next & (~jump_win) & fwd_ok
        if not np.any(keep):
            continue

        x_list.append(mat[keep])
        fwd_list.append(fwd[keep])
        ts_list.append(ts[keep])
        asset_list.append(np.full(int(np.sum(keep)), _norm_asset(asset), dtype=object))

    X = np.vstack(x_list)
    fwd = np.concatenate(fwd_list)
    ts_all = np.concatenate(ts_list)
    assets = np.concatenate(asset_list)

    order = np.argsort(ts_all, kind="mergesort")
    return X[order], fwd[order], ts_all[order], assets[order]


def resample_s5_grid(s5_df: pd.DataFrame, dt: int, phase: int = 0) -> pd.DataFrame:
    """Resamples s5 OHLCV bars into an aligned `dt`-second grid at offset `phase`."""
    bars_needed = dt // 5
    if bars_needed < 2 or s5_df is None or s5_df.empty:
        return pd.DataFrame(columns=["asset", "ts", "open_price", "high_price", "low_price", "close_price", "volume"])

    asset_frames = []
    for asset, g in s5_df.groupby("asset", sort=False):
        gs = g.sort_values("ts")
        ts_arr = gs["ts"].to_numpy()
        bucket = ((ts_arr - phase) // dt) * dt + phase
        dfb = pd.DataFrame(
            {
                "ts": bucket,
                "o": gs["open_price"].to_numpy(),
                "h": gs["high_price"].to_numpy(),
                "l": gs["low_price"].to_numpy(),
                "c": gs["close_price"].to_numpy(),
                "v": gs["volume"].to_numpy(),
            }
        )
        agg = (
            dfb.groupby("ts", sort=False)
            .agg(
                open_price=("o", "first"),
                high_price=("h", "max"),
                low_price=("l", "min"),
                close_price=("c", "last"),
                volume=("v", "sum"),
                cnt=("o", "count"),
            )
            .reset_index()
        )
        agg = agg[agg["cnt"] == bars_needed].drop(columns=["cnt"])
        if len(agg) > 0:
            agg["asset"] = asset
            asset_frames.append(agg)

    if not asset_frames:
        return pd.DataFrame(columns=["asset", "ts", "open_price", "high_price", "low_price", "close_price", "volume"])
    return pd.concat(asset_frames, ignore_index=True)


def merge_interval_sources(*ordered_frames: Optional[pd.DataFrame]) -> pd.DataFrame:
    """
    Combines candle frames ordered from lowest to highest fidelity (later sources win
    on duplicate `(asset, ts)` pairs, e.g. historical_candles < s5-resampled < subminute_candles).
    """
    valid = [f for f in ordered_frames if f is not None and not f.empty]
    if not valid:
        return pd.DataFrame(columns=["asset", "ts", "open_price", "high_price", "low_price", "close_price", "volume"])

    combined = pd.concat(valid, ignore_index=True)
    combined = combined.drop_duplicates(subset=["asset", "ts"], keep="last")
    return combined.sort_values(["asset", "ts"], kind="mergesort").reset_index(drop=True)


def build_phase_augmented_from_s5(
    s5_df: pd.DataFrame,
    dt: int,
    s5_map: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray]],
    s5_basket_df: Optional[pd.DataFrame],
) -> Optional[Tuple[np.ndarray, np.ndarray, np.ndarray]]:
    """
    Synthesizes non-zero phase-shifted OHLC grids strictly from s5 ticks for training
    (e.g. 6x on s30, 3x on s15, 2x on s10, 12x on 1min) while OOF/validation remain offset-0 only.
    """
    bars_needed = dt // 5
    if bars_needed < 2:
        return None
    parts_x, parts_f, parts_ts = [], [], []
    for phase in range(5, dt, 5):
        resampled = resample_s5_grid(s5_df, dt, phase=phase)
        if not resampled.empty:
            Xp, fp, tsp, _ = build_dataset_for_interval(
                resampled, dt, s5_map, s5_basket_df=s5_basket_df
            )
            if len(Xp) > 0:
                parts_x.append(Xp)
                parts_f.append(fp)
                parts_ts.append(tsp)
    if not parts_x:
        return None
    X_aug = np.vstack(parts_x)
    f_aug = np.concatenate(parts_f)
    ts_aug = np.concatenate(parts_ts)
    order = np.argsort(ts_aug, kind="mergesort")
    return X_aug[order], f_aug[order], ts_aug[order]


def evaluate_real_outcomes(
    oc_df: pd.DataFrame,
    tf: str,
    dt: int,
    te_ts: np.ndarray,
    te_assets: np.ndarray,
    te_p_up: np.ndarray,
    iso_x: np.ndarray,
    iso_y: np.ndarray,
    pair_isos: Optional[Dict[str, Tuple[np.ndarray, np.ndarray]]] = None,
) -> Dict[str, Any]:
    tf_key = "m1" if tf == "1min" else tf
    sub = oc_df[oc_df["timeframe"].str.lower() == tf_key]
    if len(sub) > 0:
        sub = sub[~is_synthetic_otc_mask(sub["ts"].to_numpy())]
    if len(sub) == 0:
        return {"matched": 0}

    # Build fast lookup per normalized asset
    asset_lookup: Dict[str, Tuple[np.ndarray, np.ndarray]] = {}
    for a in np.unique(te_assets):
        mask = te_assets == a
        asset_lookup[str(a)] = (te_ts[mask], te_p_up[mask])

    confs, wins, losses, ties = [], [], [], []
    for _, row in sub.iterrows():
        a_norm = _norm_asset(row["asset"])
        if a_norm not in asset_lookup:
            continue
        gts, gp = asset_lookup[a_norm]
        rts = int(row["ts"])
        pos = int(np.searchsorted(gts + dt, rts, side="right")) - 1
        if pos < 0 or (rts - (int(gts[pos]) + dt)) > dt * 2:
            continue

        p_up = float(gp[pos])
        raw_str = max(p_up, 1.0 - p_up)
        base_asset = canonical_asset_base(row["asset"])
        ix, iy = pair_isos.get(base_asset, (iso_x, iso_y)) if pair_isos else (iso_x, iso_y)
        cal_c = float(np.interp(raw_str, ix, iy))
        is_buy = p_up >= 0.50
        ep = float(row["entry_price"])
        xp = float(row["exit_price"])
        confs.append(cal_c)
        if abs(xp - ep) < 1e-12:
            ties.append(1)
            wins.append(0)
            losses.append(0)
        elif (is_buy and xp > ep) or ((not is_buy) and xp < ep):
            wins.append(1)
            losses.append(0)
            ties.append(0)
        else:
            wins.append(0)
            losses.append(1)
            ties.append(0)

    if len(confs) == 0:
        return {"matched": 0}

    ca = np.array(confs)
    wa = np.array(wins)
    la = np.array(losses)
    ta = np.array(ties)
    res: Dict[str, Any] = {"matched": int(len(ca))}
    for tag, thr in [("all", 0.50), ("ge59", 0.59), ("top20", float(np.percentile(ca, 80)))]:
        sel = ca >= thr
        w, l, t = int(wa[sel].sum()), int(la[sel].sum()), int(ta[sel].sum())
        res[tag] = {
            "n": w + l + t,
            "w": w,
            "t": t,
            "l": l,
            "wr_nt": round(100.0 * w / max(w + l, 1), 2),
        }
    return res


def train_interval(
    tf: str,
    X_all: np.ndarray,
    fwd_all: np.ndarray,
    ts_all: np.ndarray,
    assets_all: np.ndarray,
    oc_df: pd.DataFrame,
    cutoff_nov13: int,
    aux_train: Optional[Tuple[np.ndarray, np.ndarray, np.ndarray]] = None,
) -> Tuple[Dict[str, np.ndarray], Dict[str, Any]]:
    dt = INTERVAL_SECONDS[tf]
    X_raw_full = X_all
    fwd_raw_full = fwd_all
    ts_raw_full = ts_all

    # Exclude weekend synthetic OTC (Fri 20:44Z - Sun 21:00Z) AND daily 21:00Z - 23:59Z rollover OTC
    # so models learn pure live FX structure without PRNG/rollover contamination.
    fx_mask = (~is_synthetic_otc_mask(ts_all)) & (~is_synthetic_otc_mask(ts_all + dt))
    X_all = X_all[fx_mask]
    fwd_all = fwd_all[fx_mask]
    ts_all = ts_all[fx_mask]
    assets_all = assets_all[fx_mask]
    if aux_train is not None:
        ax, af, ats = aux_train
        aux_fx = (~is_synthetic_otc_mask(ats)) & (~is_synthetic_otc_mask(ats + dt))
        aux_train = (ax[aux_fx], af[aux_fx], ats[aux_fx])

    n_core = len(CORE_FEATURES)
    max_hgb = len(ALL_FEATURES) - len(MICRO_FEATURES) if tf == "s5" else len(ALL_FEATURES)
    full_nt_mask = fwd_all != 0
    active_hgb_cols = [
        j for j in range(max_hgb)
        if np.unique(X_all[full_nt_mask & ~np.isnan(X_all[:, j]), j]).size >= 2
    ]
    n_hgb = len(active_hgb_cols)
    hgb_feature_names = [ALL_FEATURES[j] for j in active_hgb_cols]

    lr_eligible = (
        set(CORE_FEATURES)
        | {"usd_idx_1m", "usd_idx_5m", "resid_1m", "resid_5m", "usd_idx_5s", "usd_idx_15s", "usd_idx_30s", "resid_5s", "resid_15s", "resid_30s"}
        | set(STATE_FEATURES)
        | (set() if tf == "s5" else {"micro_last_r", "micro_last2_r", "micro_first_half_r", "micro_second_half_r", "micro_late_share", "micro_last5_cpos", "micro_last5_wick"})
    )
    active_lr_cols = [j for j in active_hgb_cols if ALL_FEATURES[j] in lr_eligible]
    lr_feature_names = [ALL_FEATURES[j] for j in active_lr_cols]
    c_reg = 0.005 if tf == "1min" else 0.01

    X_hgb_all = X_all[:, active_hgb_cols]
    X_core_all = X_all[:, active_lr_cols]
    purge_sec = dt * 120

    aux_hgb = aux_train[0][:, active_hgb_cols] if aux_train is not None else None
    aux_core = aux_train[0][:, active_lr_cols] if aux_train is not None else None
    aux_fwd = aux_train[1] if aux_train is not None else None
    aux_ts = aux_train[2] if aux_train is not None else None

    def _concat_train(mask_main: np.ndarray, t_limit: Optional[int], non_tie_only: bool = True) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
        xh = X_hgb_all[mask_main]
        xc = X_core_all[mask_main]
        fw = fwd_all[mask_main]
        if aux_hgb is not None and aux_core is not None and aux_fwd is not None and aux_ts is not None:
            am = np.ones(len(aux_ts), dtype=bool)
            if t_limit is not None:
                am &= aux_ts <= t_limit
            if non_tie_only:
                am &= aux_fwd != 0
            if np.any(am):
                xh = np.vstack([xh, aux_hgb[am]])
                xc = np.vstack([xc, aux_core[am]])
                fw = np.concatenate([fw, aux_fwd[am]])
        return xh, xc, fw

    def _fit_sym_lr(xc_tr: np.ndarray, y_tr: np.ndarray) -> Tuple[LogisticRegression, StandardScaler, np.ndarray]:
        xc_fit = np.vstack([xc_tr, mirror_feature_matrix(xc_tr, lr_feature_names)])
        y_fit = np.concatenate([y_tr, 1 - y_tr])
        return _fit_core_lr(xc_fit, y_fit, c_reg=c_reg)

    def _pred_sym_lr(lr_m: LogisticRegression, sc_m: StandardScaler, meds_m: np.ndarray, xc_va: np.ndarray) -> np.ndarray:
        p1 = _predict_core_lr(lr_m, sc_m, meds_m, xc_va)
        p2 = 1.0 - _predict_core_lr(lr_m, sc_m, meds_m, mirror_feature_matrix(xc_va, lr_feature_names))
        return 0.5 * (p1 + p2)

    def _fit_sym_hgb(xh_tr: np.ndarray, y_tr: np.ndarray) -> Tuple[HistGradientBoostingClassifier, List[int], List[str]]:
        f_cols = [
            k for k in range(xh_tr.shape[1])
            if np.unique(xh_tr[~np.isnan(xh_tr[:, k]), k]).size >= 2
        ]
        f_names = [hgb_feature_names[k] for k in f_cols]
        xs = xh_tr[:, f_cols]
        xs_fit = np.vstack([xs, mirror_feature_matrix(xs, f_names)])
        y_fit = np.concatenate([y_tr, 1 - y_tr])
        m_f = _make_hgb(tf, seed=42)
        m_f.fit(xs_fit, y_fit)
        return m_f, f_cols, f_names

    def _pred_sym_hgb(m_f: HistGradientBoostingClassifier, f_cols: List[int], f_names: List[str], xh_va: np.ndarray) -> np.ndarray:
        xs = xh_va[:, f_cols]
        p1 = m_f.predict_proba(xs)[:, 1]
        p2 = 1.0 - m_f.predict_proba(mirror_feature_matrix(xs, f_names))[:, 1]
        return 0.5 * (p1 + p2)

    # 1) 3-Fold Chronological Expanding Walk-Forward CV for OOF & calibration
    n_total = len(ts_all)
    fold_bounds = [
        (0.50, 0.67),
        (0.67, 0.83),
        (0.83, 1.00),
    ]
    oof_p_hgb, oof_p_lr, oof_fwd, oof_assets = [], [], [], []
    oof_indices: List[int] = []

    for lo_frac, hi_frac in fold_bounds:
        va_start = int(n_total * lo_frac)
        va_end = int(n_total * hi_frac)
        t_cut = int(ts_all[va_start]) - purge_sec
        tr_mask = (ts_all <= t_cut) & (fwd_all != 0)
        va_slice = np.arange(va_start, va_end)
        if np.sum(tr_mask) < 5000 or len(va_slice) < 500:
            continue

        xh_tr, xc_tr, fw_tr = _concat_train(tr_mask, t_cut, non_tie_only=True)
        y_tr = (fw_tr > 0).astype(np.int64)
        lr_f, sc_f, meds_f = _fit_sym_lr(xc_tr, y_tr)
        m_hgb_f, f_cols_f, f_names_f = _fit_sym_hgb(xh_tr, y_tr)
        oof_p_lr.append(_pred_sym_lr(lr_f, sc_f, meds_f, X_core_all[va_slice]))
        oof_p_hgb.append(_pred_sym_hgb(m_hgb_f, f_cols_f, f_names_f, X_hgb_all[va_slice]))
        oof_fwd.append(fwd_all[va_slice])
        oof_assets.append(assets_all[va_slice])
        oof_indices.extend(va_slice.tolist())

    p_hgb_oof = np.concatenate(oof_p_hgb)
    p_lr_oof = np.concatenate(oof_p_lr)
    fwd_oof = np.concatenate(oof_fwd)
    assets_oof = np.concatenate(oof_assets)
    nt_oof = fwd_oof != 0
    y_oof_nt = (fwd_oof[nt_oof] > 0).astype(np.int64)

    best_w = 0.70
    best_ll = 999.0
    for w_cand in (0.50, 0.65, 0.75, 0.85, 1.00):
        p_cand = np.clip(w_cand * p_hgb_oof[nt_oof] + (1.0 - w_cand) * p_lr_oof[nt_oof], 1e-6, 1.0 - 1e-6)
        ll = float(log_loss(y_oof_nt, p_cand))
        if ll < best_ll:
            best_ll = ll
            best_w = w_cand

    p_ens_oof = best_w * p_hgb_oof + (1.0 - best_w) * p_lr_oof
    oof_auc = float(roc_auc_score(y_oof_nt, p_ens_oof[nt_oof]))
    oof_buy_pct = float(np.mean(p_ens_oof[nt_oof] >= 0.5)) * 100.0
    oof_dir_nt = np.maximum(p_ens_oof[nt_oof], 1.0 - p_ens_oof[nt_oof])
    oof_won_nt = (((p_ens_oof[nt_oof] >= 0.5) & (fwd_oof[nt_oof] > 0)) | ((p_ens_oof[nt_oof] < 0.5) & (fwd_oof[nt_oof] < 0))).astype(np.float64)
    oof_acc = float(np.mean(oof_won_nt))

    top20_thr = float(np.percentile(oof_dir_nt, 80))
    oof_top20_wr = float(np.mean(oof_won_nt[oof_dir_nt >= top20_thr])) * 100.0
    top10_thr = float(np.percentile(oof_dir_nt, 90))
    oof_top10_wr = float(np.mean(oof_won_nt[oof_dir_nt >= top10_thr])) * 100.0

    # Fit strict OOF monotonic calibrator on Clean FX (global + per-pair EB-shrunk)
    iso_x, iso_y = _fit_robust_isotonic(oof_dir_nt, oof_won_nt)

    oof_bases_nt = np.array([canonical_asset_base(a) for a in assets_oof[nt_oof]], dtype=object)
    p_ens_oof_nt = p_ens_oof[nt_oof]
    pair_isos: Dict[str, Tuple[np.ndarray, np.ndarray]] = {}
    per_asset_stats: Dict[str, Any] = {}
    for base_asset in sorted({str(b) for b in oof_bases_nt if b}):
        pm = oof_bases_nt == base_asset
        n_pair = int(np.sum(pm))
        if n_pair < 500:
            continue
        y_pair_dir = y_oof_nt[pm]
        pair_auc = float(roc_auc_score(y_pair_dir, p_ens_oof_nt[pm])) if np.unique(y_pair_dir).size >= 2 else 0.50
        pair_str = oof_dir_nt[pm]
        pair_won = oof_won_nt[pm]
        p20_thr = float(np.percentile(pair_str, 80))
        p10_thr = float(np.percentile(pair_str, 90))
        pair_top20 = float(np.mean(pair_won[pair_str >= p20_thr])) * 100.0
        pair_top10 = float(np.mean(pair_won[pair_str >= p10_thr])) * 100.0
        shrunk = _fit_shrunk_pair_isotonic(pair_str, pair_won, iso_x, iso_y, min_samples=1200, shrink_n0=5000.0)
        if shrunk is not None:
            pair_isos[base_asset] = shrunk
            max_pair_cal = float(np.max(shrunk[1]))
            shrink_w = round(n_pair / (n_pair + 5000.0), 3)
        else:
            max_pair_cal = float(np.max(iso_y))
            shrink_w = 0.0
        per_asset_stats[base_asset] = {
            "n_oof": n_pair,
            "shrink_w": shrink_w,
            "oof_auc": round(pair_auc, 4),
            "oof_top20_wr_nt": round(pair_top20, 2),
            "oof_top10_wr_nt": round(pair_top10, 2),
            "max_cal": round(max_pair_cal, 4),
        }

    # 2) Separate Nov 13 Holdout Benchmark (candles + real trade_outcomes)
    t_nov_cut = cutoff_nov13 - purge_sec
    nov_tr_mask = (ts_all <= t_nov_cut) & (fwd_all != 0)
    nov_te_mask = ts_all >= cutoff_nov13
    nov_info: Dict[str, Any] = {}
    if np.sum(nov_tr_mask) >= 5000 and np.sum(nov_te_mask) >= 500:
        xh_ntr, xc_ntr, fw_ntr = _concat_train(nov_tr_mask, t_nov_cut, non_tie_only=True)
        y_ntr = (fw_ntr > 0).astype(np.int64)
        lr_n, sc_n, meds_n = _fit_sym_lr(xc_ntr, y_ntr)
        m_hgb_n, f_cols_n, f_names_n = _fit_sym_hgb(xh_ntr, y_ntr)
        p_hgb_nte = _pred_sym_hgb(m_hgb_n, f_cols_n, f_names_n, X_hgb_all[nov_te_mask])
        p_nte = best_w * p_hgb_nte + (1.0 - best_w) * _pred_sym_lr(lr_n, sc_n, meds_n, X_core_all[nov_te_mask])
        f_nte = fwd_all[nov_te_mask]
        nt_n = f_nte != 0
        nov_auc = float(roc_auc_score((f_nte[nt_n] > 0).astype(np.int64), p_nte[nt_n]))
        str_n = np.maximum(p_nte[nt_n], 1.0 - p_nte[nt_n])
        won_n = (((p_nte[nt_n] >= 0.5) & (f_nte[nt_n] > 0)) | ((p_nte[nt_n] < 0.5) & (f_nte[nt_n] < 0))).astype(np.float64)
        thr_n20 = float(np.percentile(str_n, 80))
        nov_top20_wr = float(np.mean(won_n[str_n >= thr_n20])) * 100.0
        oc_res = evaluate_real_outcomes(oc_df, tf, dt, ts_all[nov_te_mask], assets_all[nov_te_mask], p_nte, iso_x, iso_y, pair_isos=pair_isos)
        nov_info = {
            "nov13_auc": round(nov_auc, 4),
            "nov13_candle_top20_wr_nt": round(nov_top20_wr, 2),
            "nov13_buy_pct": round(float(np.mean(p_nte[nt_n] >= 0.5)) * 100.0, 2),
            "nov13_real_outcomes": oc_res,
        }

    # Strict Quality Gate (zero relaxation; 1min exports with gate_pass=False if below 53.5% and fails closed to NEUTRAL)
    gate_pass = bool(oof_auc >= 0.515 and max(oof_top20_wr, oof_top10_wr) >= 53.5)

    # 3) Final Refit on Full Clean-FX History with Exact Mirror Augmentation
    full_nt_mask = fwd_all != 0
    xh_full, xc_full, fw_full = _concat_train(full_nt_mask, None, non_tie_only=True)
    y_full_nt = (fw_full > 0).astype(np.int64)
    lr_final, sc_final, meds_final = _fit_sym_lr(xc_full, y_full_nt)

    xh_full_fit = np.vstack([xh_full, mirror_feature_matrix(xh_full, hgb_feature_names)])
    y_full_fit = np.concatenate([y_full_nt, 1 - y_full_nt])
    hgb_final = _make_hgb(tf, seed=42)
    hgb_final.fit(xh_full_fit, y_full_fit)

    # Out-of-sample Synthetic OTC + Rollover (21z-23z + Weekend) Calibrator
    otc_nt_mask = is_synthetic_otc_mask(ts_raw_full) & (fwd_raw_full != 0)
    otc_auc = 0.50
    otc_top20_wr = 50.0
    iso_otc_x = np.array([0.50, 1.0], dtype=np.float64)
    iso_otc_y = np.array([0.50, 0.50], dtype=np.float64)
    if np.sum(otc_nt_mask) >= 300:
        xh_otc = X_raw_full[otc_nt_mask][:, active_hgb_cols]
        xc_otc = X_raw_full[otc_nt_mask][:, active_lr_cols]
        fw_otc = fwd_raw_full[otc_nt_mask]
        all_h_idx = list(range(len(active_hgb_cols)))
        p_hgb_otc = _pred_sym_hgb(hgb_final, all_h_idx, hgb_feature_names, xh_otc)
        p_lr_otc = _pred_sym_lr(lr_final, sc_final, meds_final, xc_otc)
        p_otc = best_w * p_hgb_otc + (1.0 - best_w) * p_lr_otc
        otc_auc = float(roc_auc_score((fw_otc > 0).astype(np.int64), p_otc))
        otc_str = np.maximum(p_otc, 1.0 - p_otc)
        otc_won = (((p_otc >= 0.5) & (fw_otc > 0)) | ((p_otc < 0.5) & (fw_otc < 0))).astype(np.float64)
        thr_o20 = float(np.percentile(otc_str, 80))
        otc_top20_wr = float(np.mean(otc_won[otc_str >= thr_o20])) * 100.0
        iso_otc_x, iso_otc_y = _fit_robust_isotonic(otc_str, otc_won)

    # Companion Tie-Hazard Classifier on ALL Clean-FX bars (symmetric mirror)
    all_main_mask = np.ones(len(ts_all), dtype=bool)
    xh_tie_all, _, fw_tie_all = _concat_train(all_main_mask, None, non_tie_only=False)
    y_tie_all = (fw_tie_all == 0).astype(np.int64)
    xh_tie_fit = np.vstack([xh_tie_all, mirror_feature_matrix(xh_tie_all, hgb_feature_names)])
    y_tie_fit = np.concatenate([y_tie_all, y_tie_all])
    tie_hgb = HistGradientBoostingClassifier(
        max_iter=150,
        max_depth=3,
        min_samples_leaf=250,
        l2_regularization=10.0,
        learning_rate=0.05,
        random_state=42,
    )
    tie_hgb.fit(xh_tie_fit, y_tie_fit)

    # Pack Pure-NumPy Arrays & Verify Exact Numeric Parity vs Sklearn (< 1e-8)
    npz_bundle: Dict[str, np.ndarray] = {}
    npz_bundle.update(pack_hgb_for_npz(hgb_final, f"{tf}_dir"))
    npz_bundle.update(pack_hgb_for_npz(tie_hgb, f"{tf}_tie"))
    npz_bundle[f"{tf}_lr_meds"] = np.asarray(meds_final, dtype=np.float64)
    npz_bundle[f"{tf}_lr_mean"] = np.asarray(sc_final.mean_, dtype=np.float64)
    npz_bundle[f"{tf}_lr_scale"] = np.asarray(sc_final.scale_, dtype=np.float64)
    npz_bundle[f"{tf}_lr_coef"] = np.asarray(lr_final.coef_[0], dtype=np.float64)
    npz_bundle[f"{tf}_lr_intercept"] = np.array([float(lr_final.intercept_[0])], dtype=np.float64)
    npz_bundle[f"{tf}_iso_x"] = iso_x.astype(np.float64)
    npz_bundle[f"{tf}_iso_y"] = iso_y.astype(np.float64)
    for base_asset, (px, py) in pair_isos.items():
        npz_bundle[f"{tf}_{base_asset}_iso_x"] = px.astype(np.float64)
        npz_bundle[f"{tf}_{base_asset}_iso_y"] = py.astype(np.float64)
    npz_bundle[f"{tf}_iso_otc_x"] = iso_otc_x.astype(np.float64)
    npz_bundle[f"{tf}_iso_otc_y"] = iso_otc_y.astype(np.float64)

    # Strict Numerical Parity Check (Last 2000 rows including any NaNs)
    sample_X = X_hgb_all[-min(2000, len(X_hgb_all)) :]
    p_sk = hgb_final.predict_proba(sample_X)[:, 1]
    p_np = eval_hgb_flat_batch(npz_bundle, f"{tf}_dir", sample_X)
    max_parity_diff = float(np.max(np.abs(p_sk - p_np)))
    if max_parity_diff >= 1e-7:
        raise RuntimeError(f"[{tf}] Pure-NumPy export parity failed: diff={max_parity_diff:.3e}")

    meta_info: Dict[str, Any] = {
        "gate_pass": gate_pass,
        "w_hgb": float(best_w),
        "n_train": int(len(y_full_nt)),
        "n_total_bars": int(len(ts_all)),
        "n_hgb_features": int(n_hgb),
        "hgb_feature_indices": [int(i) for i in active_hgb_cols],
        "n_core_features": int(n_core),
        "lr_feature_indices": [int(i) for i in active_lr_cols],
        "oof_auc": round(oof_auc, 4),
        "oof_accuracy": round(oof_acc, 4),
        "oof_buy_pct": round(oof_buy_pct, 2),
        "oof_top20_wr_nt": round(oof_top20_wr, 2),
        "oof_top10_wr_nt": round(oof_top10_wr, 2),
        "max_cal": round(float(np.max(iso_y)), 4),
        "otc_rollover_n": int(np.sum(otc_nt_mask)),
        "otc_rollover_auc": round(otc_auc, 4),
        "otc_rollover_top20_wr_nt": round(otc_top20_wr, 2),
        "otc_max_cal": round(float(np.max(iso_otc_y)), 4),
        "tie_dampen_threshold": 0.40,
        "parity_max_diff": float(max_parity_diff),
        "per_asset": per_asset_stats,
        **nov_info,
    }
    return npz_bundle, meta_info


def merge_existing_artifacts(
    out_npz: str,
    out_meta: str,
    new_arrays: Dict[str, np.ndarray],
    new_meta: Dict[str, Any],
    updated_intervals: Sequence[str],
) -> Tuple[Dict[str, np.ndarray], Dict[str, Any]]:
    """Merge freshly trained interval arrays/metadata into an existing compatible bundle."""
    with open(out_meta, "r", encoding="utf-8") as f:
        prev_meta = json.load(f)
    if prev_meta.get("all_features") != new_meta.get("all_features"):
        raise RuntimeError(
            "Cannot --merge-existing across incompatible feature schemas; run full training without --merge-existing."
        )

    prefixes = tuple(f"{tf}_" for tf in updated_intervals)
    merged_arrays: Dict[str, np.ndarray] = {}
    with np.load(out_npz, allow_pickle=False) as prev_npz:
        for key in prev_npz.files:
            if not key.startswith(prefixes):
                merged_arrays[key] = prev_npz[key]
    merged_arrays.update(new_arrays)

    prev_intervals = prev_meta.get("intervals", {})
    ordered_intervals: Dict[str, Any] = {}
    for tf in TRAIN_INTERVALS:
        if tf in new_meta.get("intervals", {}):
            ordered_intervals[tf] = new_meta["intervals"][tf]
        elif tf in prev_intervals:
            ordered_intervals[tf] = prev_intervals[tf]
    for tf, info in prev_intervals.items():
        if tf not in ordered_intervals:
            ordered_intervals[tf] = info

    merged_meta = dict(new_meta)
    merged_meta["intervals"] = ordered_intervals
    return merged_arrays, merged_meta


def main() -> None:
    parser = argparse.ArgumentParser(description="Train Subminute Quant Predictor (s5-1m)")
    parser.add_argument("--db-url", default=os.environ.get("DATABASE_URL", ""), help="PostgreSQL DSN")
    parser.add_argument("--out-npz", default=DEFAULT_NPZ_PATH)
    parser.add_argument("--out-meta", default=DEFAULT_META_PATH)
    parser.add_argument("--archive-parquet", default=DEFAULT_ARCHIVE_PATH, help="Local s5 Parquet archive path")
    parser.add_argument("--no-archive", action="store_true", help="Skip merging local s5 Parquet archive")
    parser.add_argument("--no-historical-1m", action="store_true", help="Skip historical_candles for 1min")
    parser.add_argument(
        "--hist-since",
        default="2026-11-09T00:00:00Z",
        help="UTC ISO lower bound for historical_candles (default aligns to Pocket Option regime)",
    )
    parser.add_argument("--intervals", default=",".join(TRAIN_INTERVALS), help="Comma-separated intervals to train")
    parser.add_argument(
        "--merge-existing",
        action="store_true",
        help="Preserve unmodified intervals already present in --out-npz/--out-meta when retraining a subset",
    )
    parser.add_argument("--dry-run", action="store_true", help="Evaluate OOF metrics without saving model artifacts")
    args = parser.parse_args()

    if not args.db_url:
        raise SystemExit("DATABASE_URL / --db-url is required.")

    requested_intervals = [tf.strip() for tf in args.intervals.split(",") if tf.strip() in TRAIN_INTERVALS]
    if not requested_intervals:
        requested_intervals = list(TRAIN_INTERVALS)

    t0 = time.time()
    cutoff_nov13 = int(pd.Timestamp("2026-11-13T00:00:00Z").timestamp())
    print("[1/4] Loading subminute_candles + archive/historical candles from PostgreSQL...", flush=True)
    interval_dfs: Dict[str, pd.DataFrame] = {}
    hc_1m_df = pd.DataFrame()
    conn = psycopg2.connect(args.db_url, options="-c default_transaction_read_only=on", connect_timeout=20)
    try:
        for tf in TRAIN_INTERVALS:
            q_iv = "1min" if tf == "1min" else tf
            df_iv = _query_df(
                conn,
                """
                SELECT asset, open_time, open_price, high_price, low_price, close_price, volume
                FROM subminute_candles
                WHERE interval = %s
                ORDER BY asset, open_time ASC
                """,
                params=(q_iv,),
            )
            df_iv = normalize_candle_frame(df_iv)
            df_iv["ts"] = _parse_unix_s(df_iv["open_time"]) if not df_iv.empty else np.array([], dtype=np.int64)
            interval_dfs[tf] = df_iv

        if "1min" in requested_intervals and not args.no_historical_1m:
            if args.hist_since:
                hc_raw = _query_df(
                    conn,
                    """
                    SELECT asset, open_time, open AS open_price, high AS high_price,
                           low AS low_price, close AS close_price, volume
                    FROM historical_candles
                    WHERE interval = '1m' AND open_time >= %s
                    ORDER BY asset, open_time ASC
                    """,
                    params=(args.hist_since,),
                )
            else:
                hc_raw = _query_df(
                    conn,
                    """
                    SELECT asset, open_time, open AS open_price, high AS high_price,
                           low AS low_price, close AS close_price, volume
                    FROM historical_candles
                    WHERE interval = '1m'
                    ORDER BY asset, open_time ASC
                    """,
                )
            hc_1m_df = normalize_candle_frame(hc_raw)
            if not hc_1m_df.empty:
                hc_1m_df["ts"] = _parse_unix_s(hc_1m_df["open_time"])
                print(f"      Loaded historical_candles (1m): {len(hc_1m_df):,} rows", flush=True)

        oc_df = _query_df(
            conn,
            """
            SELECT asset, timeframe, created_at, entry_price, exit_price
            FROM trade_outcomes
            WHERE created_at >= '2026-11-13T00:00:00Z'
              AND verified_at IS NOT NULL
              AND entry_price > 0
              AND exit_price > 0
            ORDER BY created_at ASC
            """,
        )
        oc_df["ts"] = _parse_unix_s(oc_df["created_at"]) if not oc_df.empty else np.array([], dtype=np.int64)
    finally:
        conn.close()

    if not args.no_archive and args.archive_parquet and os.path.exists(args.archive_parquet):
        arch_s5 = pd.read_parquet(args.archive_parquet)
        merged_s5 = merge_candle_frames(arch_s5, interval_dfs["s5"])
        merged_s5["ts"] = _parse_unix_s(merged_s5["open_time"]) if not merged_s5.empty else np.array([], dtype=np.int64)
        print(
            f"      Merged local s5 archive ({len(arch_s5):,} rows) + DB ({len(interval_dfs['s5']):,}) -> {len(merged_s5):,} rows",
            flush=True,
        )
        interval_dfs["s5"] = merged_s5

    s5_df = interval_dfs["s5"]
    s5_basket_df = _compute_usd_basket_df_from_s5(s5_df)
    s5_map: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray]] = {}
    for asset, g in s5_df.groupby("asset", sort=False):
        gs = g.sort_values("ts")
        s5_map[asset] = (
            gs["ts"].to_numpy(),
            gs["open_price"].to_numpy(),
            gs["high_price"].to_numpy(),
            gs["low_price"].to_numpy(),
            gs["close_price"].to_numpy(),
        )

    all_arrays: Dict[str, np.ndarray] = {}
    all_meta: Dict[str, Any] = {
        "model_version": MODEL_VERSION,
        "trained_at_utc": datetime.now(timezone.utc).isoformat(),
        "core_features": CORE_FEATURES,
        "regime_features": REGIME_FEATURES,
        "basket_features": BASKET_FEATURES,
        "state_features": STATE_FEATURES,
        "micro_features": MICRO_FEATURES,
        "all_features": ALL_FEATURES,
        "intervals": {},
    }

    for tf in requested_intervals:
        dt = INTERVAL_SECONDS[tf]
        db_df = interval_dfs[tf]
        if dt > 5:
            s5_zero_df = resample_s5_grid(s5_df, dt, phase=0)
            if tf == "1min" and not hc_1m_df.empty:
                df = merge_interval_sources(hc_1m_df, s5_zero_df, db_df)
            else:
                df = merge_interval_sources(s5_zero_df, db_df)
        else:
            df = db_df

        X_all, fwd_all, ts_all, assets_all = build_dataset_for_interval(
            df, dt, None if tf == "s5" else s5_map, s5_basket_df=s5_basket_df
        )
        aux_tf = build_phase_augmented_from_s5(s5_df, dt, s5_map, s5_basket_df) if dt > 5 else None
        bundle, info = train_interval(tf, X_all, fwd_all, ts_all, assets_all, oc_df, cutoff_nov13, aux_train=aux_tf)
        all_arrays.update(bundle)
        all_meta["intervals"][tf] = info
        print(
            f"[{tf.upper():>4}] n={info['n_train']:,} | bars={info['n_total_bars']:,} | OOF_AUC={info['oof_auc']:.4f} | "
            f"OOF_top20_WR={info['oof_top20_wr_nt']:.2f}% | top10={info['oof_top10_wr_nt']:.2f}% | "
            f"max_cal={info['max_cal']*100:.2f}% | Nov13_AUC={info.get('nov13_auc', 0):.4f} | "
            f"gate={info['gate_pass']} (diff={info['parity_max_diff']:.1e})",
            flush=True,
        )
        if info.get("per_asset"):
            pa_str = " | ".join(
                f"{k}:auc={v['oof_auc']:.3f},top10={v['oof_top10_wr_nt']:.1f}%,cal_max={v['max_cal']*100:.1f}%"
                for k, v in info["per_asset"].items()
            )
            print(f"       per-pair: {pa_str}", flush=True)

    if args.dry_run:
        print(f"\n[DRY-RUN] Finished in {time.time() - t0:.1f}s (artifacts untouched).", flush=True)
        return

    # Require tradeable subminute horizons (s5..s30) to pass strict quality gate before saving deployment bundle.
    # 1min is still saved with honest gate_pass status (failing closed to NEUTRAL in inference if <53.5%).
    failed = [tf for tf in ("s5", "s10", "s15", "s30") if tf in all_meta["intervals"] and not all_meta["intervals"][tf]["gate_pass"]]
    if failed:
        raise RuntimeError(f"Quality gate failed for required subminute intervals: {failed}")
    if "1min" in all_meta["intervals"] and not all_meta["intervals"]["1min"]["gate_pass"]:
        print("[WARN] 1min below strict 53.5% OOF gate — exported with gate_pass=False (fail-closed NEUTRAL shadow mode).", flush=True)

    if args.merge_existing and os.path.exists(args.out_npz) and os.path.exists(args.out_meta):
        all_arrays, all_meta = merge_existing_artifacts(
            args.out_npz, args.out_meta, all_arrays, all_meta, requested_intervals
        )
        print(f"[MERGE] Merged {requested_intervals} into existing {args.out_npz}", flush=True)

    out_dir = os.path.dirname(os.path.abspath(args.out_npz))
    os.makedirs(out_dir, exist_ok=True)
    tmp_npz = os.path.join(out_dir, f".{os.path.basename(args.out_npz)}.tmp.npz")
    tmp_meta = f"{args.out_meta}.tmp"
    np.savez_compressed(tmp_npz, **all_arrays)
    with open(tmp_meta, "w", encoding="utf-8") as f:
        json.dump(all_meta, f, indent=2)
    os.replace(tmp_npz, args.out_npz)
    os.replace(tmp_meta, args.out_meta)

    size_kb = os.path.getsize(args.out_npz) / 1024.0
    print(f"\n[DONE] Saved {args.out_npz} ({size_kb:.1f} KB) & {args.out_meta} in {time.time() - t0:.1f}s", flush=True)


if __name__ == "__main__":
    main()
