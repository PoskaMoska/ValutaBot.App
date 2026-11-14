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

from models.subminute_quant_predictor import (
    ALL_FEATURES,
    CORE_FEATURES,
    DEFAULT_META_PATH,
    DEFAULT_NPZ_PATH,
    INTERVAL_SECONDS,
    MODEL_VERSION,
    compute_features_batch,
    eval_hgb_flat_batch,
    is_weekend_otc_mask,
    pack_hgb_for_npz,
)

TRAIN_INTERVALS = ["s5", "s10", "s15", "s30", "1min"]


def _norm_asset(name: str) -> str:
    return re.sub(r"_PO$", "", str(name).strip().upper())


def _parse_unix_s(series: pd.Series) -> np.ndarray:
    return pd.to_datetime(series, utc=True).to_numpy().astype("datetime64[s]").astype("int64")


def _fit_core_lr(
    X_core: np.ndarray, y: np.ndarray, c_reg: float
) -> Tuple[LogisticRegression, StandardScaler, np.ndarray]:
    meds = np.nanmedian(X_core, axis=0)
    meds = np.where(np.isfinite(meds), meds, 0.0)
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
            max_iter=260,
            max_depth=4,
            min_samples_leaf=400,
            l2_regularization=12.0,
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
    Fits monotonic OOF non-tie win-rate calibration with tail block pooling so no tiny upper
    sample tail produces inflated overconfident probabilities.
    """
    iso = IsotonicRegression(y_min=0.50, y_max=0.75, increasing=True, out_of_bounds="clip")
    iso.fit(dir_strength, won)

    xs = np.asarray(iso.X_thresholds_, dtype=np.float64)
    ys = np.asarray(iso.y_thresholds_, dtype=np.float64)

    # Pool upper tail if fewer than 200 samples support the rightmost segment
    if len(xs) > 3:
        tail_cutoff = xs[-2]
        n_tail = int(np.sum(dir_strength >= tail_cutoff))
        if n_tail < 200:
            shrink_y = float(np.mean(won[dir_strength >= xs[-3]])) if np.sum(dir_strength >= xs[-3]) > 0 else ys[-2]
            ys[-1] = min(ys[-1], max(ys[-2], shrink_y))

    # Ensure strictly increasing xs for np.interp and anchor lower end cleanly at 0.50
    if xs[0] > 0.50:
        xs = np.insert(xs, 0, 0.50)
        ys = np.insert(ys, 0, max(0.50, ys[0] - 0.01))
    if xs[-1] < 1.0:
        xs = np.append(xs, 1.0)
        ys = np.append(ys, ys[-1])

    # Deduplicate any identical x coordinates keeping conservative average
    ux, idx = np.unique(xs, return_index=True)
    uy = np.maximum.accumulate(ys[idx])
    return ux, np.clip(uy, 0.50, 0.75)


def build_dataset_for_interval(
    df: pd.DataFrame, dt: int, s5_map: Optional[Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]]
) -> Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Builds chronological (X, fwd, ts, asset_norm) arrays cleanly across all assets."""
    x_list, fwd_list, ts_list, asset_list = [], [], [], []
    for asset, g in df.groupby("asset", sort=False):
        g_s = g.sort_values("ts")
        ts = g_s["ts"].to_numpy()
        o = g_s["open_price"].to_numpy()
        h = g_s["high_price"].to_numpy()
        l = g_s["low_price"].to_numpy()
        c = g_s["close_price"].to_numpy()
        v = g_s["volume"].to_numpy()

        st = so = sc = None
        if dt > 5 and s5_map is not None and asset in s5_map:
            st, so, sc = s5_map[asset]

        mat, valid_hist = compute_features_batch(ts, o, h, l, c, v, dt, s5_ts=st, s5_o=so, s5_c=sc)
        if len(mat) == 0:
            continue
        n = len(ts)
        valid_next = np.zeros(n, dtype=bool)
        valid_next[:-1] = (ts[1:] - ts[:-1]) == dt
        fwd = np.zeros(n, dtype=np.float64)
        fwd[:-1] = (c[1:] - o[1:]) / np.maximum(o[1:], 1e-9) * 10000.0
        keep = valid_hist & valid_next
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


def build_1min_phase_augmented_from_s5(
    s5_df: pd.DataFrame, s5_map: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]]
) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """
    Synthesizes 11 phase-shifted (5s..55s) complete 60s PO OHLC grids strictly from s5 ticks
    so 1min training leverages 12x real tick microstructure while OOF/validation remain offset-0 only.
    """
    parts_x, parts_f, parts_ts = [], [], []
    for phase in range(5, 60, 5):
        asset_frames = []
        for asset, g in s5_df.groupby("asset", sort=False):
            gs = g.sort_values("ts")
            ts_arr = gs["ts"].to_numpy()
            bucket = ((ts_arr - phase) // 60) * 60 + phase
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
            agg = agg[agg["cnt"] == 12].copy()
            if len(agg) > 0:
                agg["asset"] = asset
                asset_frames.append(agg)
        if asset_frames:
            Xp, fp, tsp, _ = build_dataset_for_interval(pd.concat(asset_frames, ignore_index=True), 60, s5_map)
            parts_x.append(Xp)
            parts_f.append(fp)
            parts_ts.append(tsp)
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
) -> Dict[str, Any]:
    tf_key = "m1" if tf == "1min" else tf
    sub = oc_df[oc_df["timeframe"].str.lower() == tf_key]
    if len(sub) > 0:
        sub = sub[~is_weekend_otc_mask(sub["ts"].to_numpy())]
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
        cal_c = float(np.interp(raw_str, iso_x, iso_y))
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
    # Exclude weekend synthetic PRNG OTC ticks (Fri 20:44Z - Sun 21:00Z) so models learn pure live FX patterns
    fx_mask = (~is_weekend_otc_mask(ts_all)) & (~is_weekend_otc_mask(ts_all + dt))
    X_all = X_all[fx_mask]
    fwd_all = fwd_all[fx_mask]
    ts_all = ts_all[fx_mask]
    assets_all = assets_all[fx_mask]
    if aux_train is not None:
        ax, af, ats = aux_train
        aux_fx = (~is_weekend_otc_mask(ats)) & (~is_weekend_otc_mask(ats + dt))
        aux_train = (ax[aux_fx], af[aux_fx], ats[aux_fx])

    n_core = len(CORE_FEATURES)
    max_hgb = n_core if tf == "s5" else len(ALL_FEATURES)
    full_nt_mask = fwd_all != 0
    active_hgb_cols = [
        j for j in range(max_hgb)
        if np.unique(X_all[full_nt_mask & ~np.isnan(X_all[:, j]), j]).size >= 2
    ]
    n_hgb = len(active_hgb_cols)
    c_reg = 0.005 if tf == "1min" else 0.01

    X_hgb_all = X_all[:, active_hgb_cols]
    X_core_all = X_all[:, :n_core]
    purge_sec = dt * 120

    aux_hgb = aux_train[0][:, active_hgb_cols] if aux_train is not None else None
    aux_core = aux_train[0][:, :n_core] if aux_train is not None else None
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

    def _fit_predict_fold_hgb(X_tr_f: np.ndarray, y_tr_f: np.ndarray, X_va_f: np.ndarray) -> np.ndarray:
        f_cols = [
            k for k in range(X_tr_f.shape[1])
            if np.unique(X_tr_f[~np.isnan(X_tr_f[:, k]), k]).size >= 2
        ]
        m_f = _make_hgb(tf, seed=42)
        m_f.fit(X_tr_f[:, f_cols], y_tr_f)
        return m_f.predict_proba(X_va_f[:, f_cols])[:, 1]

    # 1) 3-Fold Chronological Expanding Walk-Forward CV for OOF & calibration
    n_total = len(ts_all)
    fold_bounds = [
        (0.50, 0.67),
        (0.67, 0.83),
        (0.83, 1.00),
    ]
    oof_p_hgb, oof_p_lr, oof_fwd = [], [], []
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
        lr_f, sc_f, meds_f = _fit_core_lr(xc_tr, y_tr, c_reg=c_reg)
        oof_p_lr.append(_predict_core_lr(lr_f, sc_f, meds_f, X_core_all[va_slice]))
        oof_p_hgb.append(_fit_predict_fold_hgb(xh_tr, y_tr, X_hgb_all[va_slice]))
        oof_fwd.append(fwd_all[va_slice])
        oof_indices.extend(va_slice.tolist())

    p_hgb_oof = np.concatenate(oof_p_hgb)
    p_lr_oof = np.concatenate(oof_p_lr)
    fwd_oof = np.concatenate(oof_fwd)
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
    oof_dir_nt = np.maximum(p_ens_oof[nt_oof], 1.0 - p_ens_oof[nt_oof])
    oof_won_nt = (((p_ens_oof[nt_oof] >= 0.5) & (fwd_oof[nt_oof] > 0)) | ((p_ens_oof[nt_oof] < 0.5) & (fwd_oof[nt_oof] < 0))).astype(np.float64)
    oof_acc = float(np.mean(oof_won_nt))

    top20_thr = float(np.percentile(oof_dir_nt, 80))
    oof_top20_wr = float(np.mean(oof_won_nt[oof_dir_nt >= top20_thr])) * 100.0
    top10_thr = float(np.percentile(oof_dir_nt, 90))
    oof_top10_wr = float(np.mean(oof_won_nt[oof_dir_nt >= top10_thr])) * 100.0

    # Fit strict OOF monotonic calibrator
    iso_x, iso_y = _fit_robust_isotonic(oof_dir_nt, oof_won_nt)

    # 2) Separate Nov 13 Holdout Benchmark (candles + real trade_outcomes)
    t_nov_cut = cutoff_nov13 - purge_sec
    nov_tr_mask = (ts_all <= t_nov_cut) & (fwd_all != 0)
    nov_te_mask = ts_all >= cutoff_nov13
    nov_info: Dict[str, Any] = {}
    if np.sum(nov_tr_mask) >= 5000 and np.sum(nov_te_mask) >= 500:
        xh_ntr, xc_ntr, fw_ntr = _concat_train(nov_tr_mask, t_nov_cut, non_tie_only=True)
        y_ntr = (fw_ntr > 0).astype(np.int64)
        lr_n, sc_n, meds_n = _fit_core_lr(xc_ntr, y_ntr, c_reg=c_reg)
        p_hgb_nte = _fit_predict_fold_hgb(xh_ntr, y_ntr, X_hgb_all[nov_te_mask])
        p_nte = best_w * p_hgb_nte + (1.0 - best_w) * _predict_core_lr(lr_n, sc_n, meds_n, X_core_all[nov_te_mask])
        f_nte = fwd_all[nov_te_mask]
        nt_n = f_nte != 0
        nov_auc = float(roc_auc_score((f_nte[nt_n] > 0).astype(np.int64), p_nte[nt_n]))
        str_n = np.maximum(p_nte[nt_n], 1.0 - p_nte[nt_n])
        won_n = (((p_nte[nt_n] >= 0.5) & (f_nte[nt_n] > 0)) | ((p_nte[nt_n] < 0.5) & (f_nte[nt_n] < 0))).astype(np.float64)
        thr_n20 = float(np.percentile(str_n, 80))
        nov_top20_wr = float(np.mean(won_n[str_n >= thr_n20])) * 100.0
        oc_res = evaluate_real_outcomes(oc_df, tf, dt, ts_all[nov_te_mask], assets_all[nov_te_mask], p_nte, iso_x, iso_y)
        nov_info = {
            "nov13_auc": round(nov_auc, 4),
            "nov13_candle_top20_wr_nt": round(nov_top20_wr, 2),
            "nov13_real_outcomes": oc_res,
        }

    # Quality Gate
    gate_pass = bool(oof_auc >= 0.515 and max(oof_top20_wr, oof_top10_wr) >= 53.5)

    # 3) Final Refit on Full History for Deployment
    full_nt_mask = fwd_all != 0
    xh_full, xc_full, fw_full = _concat_train(full_nt_mask, None, non_tie_only=True)
    y_full_nt = (fw_full > 0).astype(np.int64)
    lr_final, sc_final, meds_final = _fit_core_lr(xc_full, y_full_nt, c_reg=c_reg)
    hgb_final = _make_hgb(tf, seed=42)
    hgb_final.fit(xh_full, y_full_nt)

    # Companion Tie-Hazard Classifier on ALL bars
    all_main_mask = np.ones(len(ts_all), dtype=bool)
    xh_tie_all, _, fw_tie_all = _concat_train(all_main_mask, None, non_tie_only=False)
    y_tie_all = (fw_tie_all == 0).astype(np.int64)
    tie_hgb = HistGradientBoostingClassifier(
        max_iter=150,
        max_depth=3,
        min_samples_leaf=250,
        l2_regularization=10.0,
        learning_rate=0.05,
        random_state=42,
    )
    tie_hgb.fit(xh_tie_all, y_tie_all)

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
        "oof_auc": round(oof_auc, 4),
        "oof_accuracy": round(oof_acc, 4),
        "oof_top20_wr_nt": round(oof_top20_wr, 2),
        "oof_top10_wr_nt": round(oof_top10_wr, 2),
        "tie_dampen_threshold": 0.40,
        "parity_max_diff": float(max_parity_diff),
        **nov_info,
    }
    return npz_bundle, meta_info


def main() -> None:
    parser = argparse.ArgumentParser(description="Train Subminute Quant Predictor (s5-1m)")
    parser.add_argument("--db-url", default=os.environ.get("DATABASE_URL", ""), help="PostgreSQL DSN")
    parser.add_argument("--out-npz", default=DEFAULT_NPZ_PATH)
    parser.add_argument("--out-meta", default=DEFAULT_META_PATH)
    args = parser.parse_args()

    if not args.db_url:
        raise SystemExit("DATABASE_URL / --db-url is required.")

    t0 = time.time()
    conn = psycopg2.connect(args.db_url, options="-c default_transaction_read_only=on", connect_timeout=20)
    cutoff_nov13 = int(pd.Timestamp("2026-11-13T00:00:00Z").timestamp())

    try:
        print("[1/4] Loading s5 candles + trade_outcomes from PostgreSQL...", flush=True)
        s5_df = pd.read_sql_query(
            """
            SELECT asset, open_time, open_price, high_price, low_price, close_price, volume
            FROM subminute_candles
            WHERE interval = 's5'
            ORDER BY asset, open_time ASC
            """,
            conn,
        )
        s5_df["ts"] = _parse_unix_s(s5_df["open_time"])
        s5_map: Dict[str, Tuple[np.ndarray, np.ndarray, np.ndarray]] = {}
        for asset, g in s5_df.groupby("asset", sort=False):
            gs = g.sort_values("ts")
            s5_map[asset] = (gs["ts"].to_numpy(), gs["open_price"].to_numpy(), gs["close_price"].to_numpy())

        oc_df = pd.read_sql_query(
            """
            SELECT asset, timeframe, created_at, entry_price, exit_price
            FROM trade_outcomes
            WHERE created_at >= '2026-11-13T00:00:00Z'
              AND verified_at IS NOT NULL
              AND entry_price > 0
              AND exit_price > 0
            ORDER BY created_at ASC
            """,
            conn,
        )
        oc_df["ts"] = _parse_unix_s(oc_df["created_at"])

        all_arrays: Dict[str, np.ndarray] = {}
        all_meta: Dict[str, Any] = {
            "model_version": MODEL_VERSION,
            "trained_at_utc": datetime.now(timezone.utc).isoformat(),
            "core_features": CORE_FEATURES,
            "all_features": ALL_FEATURES,
            "intervals": {},
        }

        for tf in TRAIN_INTERVALS:
            dt = INTERVAL_SECONDS[tf]
            if tf == "s5":
                df = s5_df
            elif tf == "1min":
                df = pd.read_sql_query(
                    """
                    SELECT asset, open_time, open_price, high_price, low_price, close_price, volume
                    FROM subminute_candles
                    WHERE interval = '1min'
                    ORDER BY asset, open_time ASC
                    """,
                    conn,
                )
                df["ts"] = _parse_unix_s(df["open_time"])
            else:
                df = pd.read_sql_query(
                    """
                    SELECT asset, open_time, open_price, high_price, low_price, close_price, volume
                    FROM subminute_candles
                    WHERE interval = %s
                    ORDER BY asset, open_time ASC
                    """,
                    conn,
                    params=(tf,),
                )
                df["ts"] = _parse_unix_s(df["open_time"])

            X_all, fwd_all, ts_all, assets_all = build_dataset_for_interval(df, dt, None if tf == "s5" else s5_map)
            aux_1m = build_1min_phase_augmented_from_s5(s5_df, s5_map) if tf == "1min" else None
            bundle, info = train_interval(tf, X_all, fwd_all, ts_all, assets_all, oc_df, cutoff_nov13, aux_train=aux_1m)
            all_arrays.update(bundle)
            all_meta["intervals"][tf] = info
            print(
                f"[{tf.upper():>4}] n={info['n_train']:,} | OOF_AUC={info['oof_auc']:.4f} | "
                f"OOF_top20_WR={info['oof_top20_wr_nt']:.2f}% | "
                f"Nov13_AUC={info.get('nov13_auc', 0):.4f} | "
                f"Nov13_Real={info.get('nov13_real_outcomes', {}).get('top20', {})} | "
                f"gate={info['gate_pass']} (diff={info['parity_max_diff']:.1e})",
                flush=True,
            )

        # Require all 5 subminute timeframes to pass gate before saving deployment bundle
        failed = [tf for tf, m in all_meta["intervals"].items() if not m["gate_pass"]]
        if failed:
            raise RuntimeError(f"Quality gate failed for intervals: {failed}")

        os.makedirs(os.path.dirname(os.path.abspath(args.out_npz)), exist_ok=True)
        np.savez_compressed(args.out_npz, **all_arrays)
        with open(args.out_meta, "w", encoding="utf-8") as f:
            json.dump(all_meta, f, indent=2)

        size_kb = os.path.getsize(args.out_npz) / 1024.0
        print(f"\n[DONE] Saved {args.out_npz} ({size_kb:.1f} KB) & {args.out_meta} in {time.time() - t0:.1f}s", flush=True)
    finally:
        conn.close()


if __name__ == "__main__":
    main()
