"""
Subminute Microstructure Predictor (s5, s10, s15, s30, 1m)
=========================================================
Pure-NumPy / JSON runtime inference with zero scikit-learn or pickle dependency
for production parity across Python 3.9 - 3.14 (Windows/Linux Docker).

Exports:
- Interval normalization across PocketOption / C# / API formats ('m1', '1m', '1min' -> '1min').
- Deterministic, strictly causal pure-NumPy feature extractor shared identically by
  offline training (`train_subminute_quant.py`) and live `/predict` serving.
- Vectorized/scalar GBDT tree-walker + regularized Linear model + OOF piecewise-linear
  isotonic probability calibration + tie-hazard dampener.
"""

from __future__ import annotations

import json
import math
import os
import threading
from typing import Any, Dict, List, Optional, Sequence, Tuple

import numpy as np

MODEL_VERSION = "neural_quant_v7"
MODEL_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_NPZ_PATH = os.path.join(MODEL_DIR, "subminute_quant_v7.npz")
DEFAULT_META_PATH = os.path.join(MODEL_DIR, "subminute_quant_v7.json")

INTERVAL_SECONDS: Dict[str, int] = {
    "s5": 5,
    "s10": 10,
    "s15": 15,
    "s30": 30,
    "1min": 60,
}


def is_weekend_otc_mask(ts_sec: np.ndarray) -> np.ndarray:
    """True during Pocket Option weekend synthetic OTC stream (Fri 20:44 UTC - Sun 21:00 UTC)."""
    ts_i = np.asarray(ts_sec, dtype=np.int64)
    dow = ((ts_i // 86400) + 3) % 7  # 0=Mon .. 4=Fri, 5=Sat, 6=Sun
    sod = ts_i % 86400
    return ((dow == 4) & (sod >= (20 * 3600 + 44 * 60))) | (dow == 5) | ((dow == 6) & (sod < 21 * 3600))


def is_weekend_otc_unix(ts_sec: int) -> bool:
    """Scalar check for weekend synthetic PRNG OTC hours (Fri 20:44 UTC - Sun 21:00 UTC)."""
    return bool(is_weekend_otc_mask(np.array([int(ts_sec)], dtype=np.int64))[0])


def is_rollover_otc_mask(ts_sec: np.ndarray) -> np.ndarray:
    """True during daily NY close / bank rollover OTC window (21:00-23:59 UTC outside weekend OTC)."""
    ts_i = np.asarray(ts_sec, dtype=np.int64)
    hr = (ts_i // 3600) % 24
    return (hr >= 21) & (~is_weekend_otc_mask(ts_i))


def is_rollover_otc_unix(ts_sec: int) -> bool:
    """Scalar check for daily synthetic rollover OTC hours (21:00-23:59 UTC)."""
    return bool(is_rollover_otc_mask(np.array([int(ts_sec)], dtype=np.int64))[0])


def is_synthetic_otc_mask(ts_sec: np.ndarray) -> np.ndarray:
    """True across both weekend synthetic OTC and daily 21:00-23:59 UTC rollover OTC."""
    return is_weekend_otc_mask(ts_sec) | is_rollover_otc_mask(ts_sec)


def usd_basket_sign(symbol: Optional[str]) -> float:
    """Returns +1.0 for direct USD pairs (USDJPY, USDCAD, USDCHF), -1.0 for inverse (EURUSD...), else 0.0."""
    if not symbol:
        return 0.0
    s = str(symbol).upper().replace("/", "").replace("_OTC", "").replace("_PO", "").strip()
    if s.startswith("USD"):
        return 1.0
    if s.endswith("USD") or "USD" in s:
        return -1.0
    return 0.0

CORE_FEATURES: List[str] = [
    "r0",
    "body_signed",
    "r1",
    "r2",
    "r3",
    "accel",
    "sum_r2",
    "sum_r3",
    "sum_r5",
    "sum_r8",
    "sum_r12",
    "sum_r20",
    "sum_r40",
    "z_r0",
    "z_body0",
    "z_r3",
    "z_r5",
    "z_r12",
    "z_r20",
    "body_bps",
    "rng_bps",
    "body_ratio",
    "uwick_ratio",
    "lwick_ratio",
    "wick_asym",
    "close_pos",
    "prev_close_pos",
    "m5_close_pos",
    "m15_close_pos",
    "atr5",
    "atr14",
    "atr20",
    "atr_ratio_5_20",
    "rng_atr_ratio",
    "std5",
    "std20",
    "vol_comp",
    "tie_rate5",
    "tie_rate20",
    "zero_rng_rate10",
    "streak",
    "up_frac10",
    "up_frac20",
    "dist_ema9_atr",
    "dist_ema21_atr",
    "dist_ema50_atr",
    "ema9_slope",
    "bb_z20",
    "donchian_pos20",
    "rsi7",
    "rsi14",
    "vol_rel",
    "hr_sin",
    "hr_cos",
]

REGIME_FEATURES: List[str] = [
    "rev_rate12",
    "rev_rate24",
    "autocorr1_24",
    "vr5_20",
    "vr12_48",
    "med_abs_r12",
    "jump_max5",
]

BASKET_FEATURES: List[str] = [
    "usd_idx_1m",
    "usd_idx_5m",
    "basket_sync",
    "resid_1m",
    "resid_5m",
]

MICRO_FEATURES: List[str] = [
    "micro_last_r",
    "micro_last2_r",
    "micro_first_half_r",
    "micro_second_half_r",
    "micro_late_share",
    "micro_path_eff",
    "micro_flips",
]

ALL_FEATURES: List[str] = CORE_FEATURES + REGIME_FEATURES + BASKET_FEATURES + MICRO_FEATURES

_MIRROR_FLIP_SIGN = {
    "r0", "body_signed", "r1", "r2", "r3", "accel",
    "sum_r2", "sum_r3", "sum_r5", "sum_r8", "sum_r12", "sum_r20", "sum_r40",
    "z_r0", "z_body0", "z_r3", "z_r5", "z_r12", "z_r20",
    "wick_asym", "streak", "dist_ema9_atr", "dist_ema21_atr", "dist_ema50_atr",
    "ema9_slope", "bb_z20",
    "usd_idx_1m", "usd_idx_5m", "resid_1m", "resid_5m",
    "micro_last_r", "micro_last2_r", "micro_first_half_r", "micro_second_half_r", "micro_late_share",
}
_MIRROR_ONE_MINUS = {
    "close_pos", "prev_close_pos", "m5_close_pos", "m15_close_pos", "donchian_pos20",
    "up_frac10", "up_frac20",
}
_MIRROR_HUNDRED_MINUS = {"rsi7", "rsi14"}
_MIRROR_SWAP = [("uwick_ratio", "lwick_ratio")]


def mirror_feature_matrix(X: np.ndarray, feature_names: Sequence[str]) -> np.ndarray:
    """
    Applies exact price-inversion reflection (P -> -P) on a feature matrix so models can
    enforce exact 50/50 antisymmetry: P(BUY | mirror(X)) == 1 - P(BUY | X).
    """
    M = np.array(X, dtype=np.float64, copy=True)
    is_1d = M.ndim == 1
    if is_1d:
        M = M.reshape(1, -1)
    idx_map = {name: i for i, name in enumerate(feature_names)}
    for n in _MIRROR_FLIP_SIGN:
        i = idx_map.get(n)
        if i is not None:
            M[:, i] = -M[:, i]
    for n in _MIRROR_ONE_MINUS:
        i = idx_map.get(n)
        if i is not None:
            M[:, i] = 1.0 - M[:, i]
    for n in _MIRROR_HUNDRED_MINUS:
        i = idx_map.get(n)
        if i is not None:
            M[:, i] = 100.0 - M[:, i]
    for a, b in _MIRROR_SWAP:
        ia, ib = idx_map.get(a), idx_map.get(b)
        if ia is not None and ib is not None:
            tmp = M[:, ia].copy()
            M[:, ia] = M[:, ib]
            M[:, ib] = tmp
    return M[0] if is_1d else M


def normalize_interval(interval: Optional[str]) -> Optional[str]:
    """Maps API/DB interval variants into canonical subminute keys or None."""
    if not interval:
        return None
    s = str(interval).strip().lower()
    if s in ("s5", "5s"):
        return "s5"
    if s in ("s10", "10s"):
        return "s10"
    if s in ("s15", "15s"):
        return "s15"
    if s in ("s30", "30s"):
        return "s30"
    if s in ("1m", "m1", "1min", "60s"):
        return "1min"
    return None


def _rolling_sum(arr: np.ndarray, k: int) -> np.ndarray:
    n = len(arr)
    out = np.full(n, np.nan, dtype=np.float64)
    if n < k:
        return out
    cs = np.empty(n + 1, dtype=np.float64)
    cs[0] = 0.0
    np.cumsum(arr, out=cs[1:])
    out[k - 1 :] = cs[k:] - cs[:-k]
    return out


def _rolling_mean(arr: np.ndarray, k: int) -> np.ndarray:
    return _rolling_sum(arr, k) / float(k)


def _rolling_std(arr: np.ndarray, k: int) -> np.ndarray:
    s1 = _rolling_sum(arr, k)
    s2 = _rolling_sum(arr * arr, k)
    var = np.maximum(s2 / float(k) - (s1 / float(k)) ** 2, 0.0)
    return np.sqrt(var)


def _ema(arr: np.ndarray, span: int) -> np.ndarray:
    n = len(arr)
    out = np.empty(n, dtype=np.float64)
    if n == 0:
        return out
    alpha = 2.0 / (float(span) + 1.0)
    one_a = 1.0 - alpha
    out[0] = arr[0]
    for i in range(1, n):
        out[i] = alpha * arr[i] + one_a * out[i - 1]
    return out


def _wilder_rsi(delta: np.ndarray, period: int) -> np.ndarray:
    n = len(delta)
    if n == 0:
        return np.empty(0, dtype=np.float64)
    alpha = 1.0 / float(period)
    one_a = 1.0 - alpha
    up = np.maximum(delta, 0.0)
    dn = np.maximum(-delta, 0.0)
    e_up = np.empty(n, dtype=np.float64)
    e_dn = np.empty(n, dtype=np.float64)
    e_up[0] = up[0]
    e_dn[0] = dn[0]
    for i in range(1, n):
        e_up[i] = alpha * up[i] + one_a * e_up[i - 1]
        e_dn[i] = alpha * dn[i] + one_a * e_dn[i - 1]
    denom = e_up + e_dn
    return np.where(denom <= 1e-12, 50.0, 100.0 * e_up / np.maximum(denom, 1e-12))


def _rolling_channel_pos(h: np.ndarray, l: np.ndarray, c: np.ndarray, k: int) -> np.ndarray:
    n = len(c)
    out = np.full(n, 0.5, dtype=np.float64)
    if n < k:
        for i in range(n):
            lo = float(np.min(l[: i + 1]))
            hi = float(np.max(h[: i + 1]))
            if hi > lo:
                out[i] = (c[i] - lo) / (hi - lo)
        return out
    for i in range(k - 1, n):
        lo = float(np.min(l[i - k + 1 : i + 1]))
        hi = float(np.max(h[i - k + 1 : i + 1]))
        if hi > lo:
            out[i] = (c[i] - lo) / (hi - lo)
    return out


def _extract_macro_field(macro: Dict[str, Any], *keys: str) -> Optional[float]:
    for k in keys:
        if k in macro and macro[k] is not None:
            try:
                v = float(macro[k])
                if math.isfinite(v):
                    return v
            except (TypeError, ValueError):
                pass
    return None


def compute_features_batch(
    ts: np.ndarray,
    o: np.ndarray,
    h: np.ndarray,
    l: np.ndarray,
    c: np.ndarray,
    v: np.ndarray,
    dt: int,
    s5_ts: Optional[np.ndarray] = None,
    s5_o: Optional[np.ndarray] = None,
    s5_c: Optional[np.ndarray] = None,
    only_last: bool = False,
    basket_arrays: Optional[Tuple[np.ndarray, np.ndarray, np.ndarray]] = None,
    usd_sign: float = 0.0,
) -> Tuple[np.ndarray, np.ndarray]:
    """
    Computes the exact (N, len(ALL_FEATURES)) feature matrix and valid_history boolean mask
    strictly causally over ascending candles of a single asset.

    When `only_last=True`, evaluates micro_features only for index `n - 1` (fast live path)
    while retaining identical numerical output on row `-1` as batch mode.
    """
    n = len(c)
    if n < 25:
        return np.empty((0, len(ALL_FEATURES)), dtype=np.float64), np.zeros(0, dtype=bool)

    ts = np.asarray(ts, dtype=np.int64)
    o = np.maximum(np.asarray(o, dtype=np.float64), 1e-9)
    h = np.asarray(h, dtype=np.float64)
    l = np.asarray(l, dtype=np.float64)
    c = np.maximum(np.asarray(c, dtype=np.float64), 1e-9)
    v = np.maximum(np.asarray(v, dtype=np.float64), 0.0)

    prev_c = np.empty(n, dtype=np.float64)
    prev_c[0] = o[0]
    prev_c[1:] = c[:-1]

    dt_prev = np.empty(n, dtype=np.int64)
    dt_prev[0] = dt
    dt_prev[1:] = np.diff(ts)
    is_contig = dt_prev == dt

    ret_close = (c - prev_c) / prev_c * 10000.0
    body_signed = (c - o) / o * 10000.0
    ret = np.where(is_contig, ret_close, body_signed)
    body = np.abs(body_signed)
    rng = np.maximum(h - l, 0.0) / prev_c * 10000.0
    uwick = np.maximum(h - np.maximum(o, c), 0.0) / prev_c * 10000.0
    lwick = np.maximum(np.minimum(o, c) - l, 0.0) / prev_c * 10000.0

    def lag(arr: np.ndarray, k: int) -> np.ndarray:
        out = np.full(n, np.nan, dtype=np.float64)
        if n > k:
            out[k:] = arr[:-k]
        return out

    atr5 = _rolling_mean(rng, 5)
    atr14 = _rolling_mean(rng, 14)
    atr20 = _rolling_mean(rng, 20)
    std5 = _rolling_std(ret, 5)
    std20 = _rolling_std(ret, 20)

    is_tie = (np.abs(ret) < 1e-9).astype(np.float64)
    tie_rate5 = _rolling_mean(is_tie, 5)
    tie_rate20 = _rolling_mean(is_tie, 20)
    zero_rng_rate10 = _rolling_mean((rng < 1e-9).astype(np.float64), 10)

    sign = np.sign(body_signed)
    streak = np.zeros(n, dtype=np.float64)
    cur_s = 0.0
    for i in range(n):
        if sign[i] == 0.0:
            cur_s = 0.0
        elif (cur_s > 0 and sign[i] > 0) or (cur_s < 0 and sign[i] < 0):
            cur_s += sign[i]
        else:
            cur_s = sign[i]
        streak[i] = cur_s

    up_score = np.where(body_signed > 0, 1.0, np.where(body_signed < 0, 0.0, 0.5))
    up_frac10 = _rolling_mean(up_score, 10)
    up_frac20 = _rolling_mean(up_score, 20)

    ema9 = _ema(c, 9)
    ema21 = _ema(c, 21)
    ema50 = _ema(c, 50)
    sma20 = _rolling_mean(c, 20)
    pstd20 = _rolling_std(c, 20)
    bb_z20 = (c - sma20) / np.maximum(pstd20, c * 1e-7)

    donchian_pos20 = _rolling_channel_pos(h, l, c, 20)
    m5_close_pos = _rolling_channel_pos(h, l, c, 5)
    m15_close_pos = _rolling_channel_pos(h, l, c, 15)

    rsi7 = _wilder_rsi(ret, 7)
    rsi14 = _wilder_rsi(ret, 14)

    vol_ma20 = _rolling_mean(v, 20)
    eps_rng = np.maximum(rng, 0.05)
    eps_atr20 = np.maximum(atr20, 0.05)
    eps_std20 = np.maximum(std20, 0.05)

    r1 = lag(ret, 1)
    r2 = lag(ret, 2)
    r3 = lag(ret, 3)
    sum_r2 = _rolling_sum(ret, 2)
    sum_r3 = _rolling_sum(ret, 3)
    sum_r5 = _rolling_sum(ret, 5)
    sum_r8 = _rolling_sum(ret, 8)
    sum_r12 = _rolling_sum(ret, 12)
    sum_r20 = _rolling_sum(ret, 20)
    sum_r40 = _rolling_sum(ret, 40)

    span_hl = h - l
    close_pos = np.where(span_hl > 1e-12, np.clip((c - l) / np.maximum(span_hl, 1e-12), 0.0, 1.0), 0.5)
    prev_close_pos = lag(close_pos, 1)
    hr = ((ts // 3600) % 24).astype(np.float64)

    # Strictly causal regime microstructure indicators (symmetric/invariant under P -> -P)
    nz_ret = np.abs(ret) > 1e-9
    same_prev_nz = nz_ret[:-1] & nz_ret[1:] & is_contig[1:]
    rev_flag = np.zeros(n, dtype=np.float64)
    valid_rev = np.zeros(n, dtype=np.float64)
    rev_flag[1:] = np.where(same_prev_nz & ((ret[1:] * ret[:-1]) < 0.0), 1.0, 0.0)
    valid_rev[1:] = np.where(same_prev_nz, 1.0, 0.0)
    rev_rate12 = _rolling_mean(rev_flag, 12) / np.maximum(_rolling_mean(valid_rev, 12), 1e-6)
    rev_rate24 = _rolling_mean(rev_flag, 24) / np.maximum(_rolling_mean(valid_rev, 24), 1e-6)

    prod1 = np.zeros(n, dtype=np.float64)
    prod1[1:] = np.where(is_contig[1:], ret[1:] * ret[:-1], 0.0)
    std24 = _rolling_std(ret, 24)
    autocorr1_24 = np.clip(_rolling_mean(prod1, 24) / np.maximum(std24 * std24, 1e-4), -1.0, 1.0)

    cs_ret = np.cumsum(np.insert(np.where(np.isfinite(ret), ret, 0.0), 0, 0.0))
    s5_sum = np.zeros(n, dtype=np.float64)
    if n >= 5:
        s5_sum[4:] = cs_ret[5:] - cs_ret[:-5]
    s12_sum = np.zeros(n, dtype=np.float64)
    if n >= 12:
        s12_sum[11:] = cs_ret[12:] - cs_ret[:-12]
    vr5_20 = (_rolling_std(s5_sum, 24) ** 2) / np.maximum(5.0 * (std24 ** 2), 1e-4)
    std48 = _rolling_std(ret, 48)
    vr12_48 = (_rolling_std(s12_sum, 48) ** 2) / np.maximum(12.0 * (std48 ** 2), 1e-4)
    med_abs_r12 = _rolling_mean(body, 12)

    abs_gap = np.abs(ret_close)
    jump_max5 = abs_gap.copy()
    for k_j in range(1, min(5, n)):
        jump_max5[k_j:] = np.maximum(jump_max5[k_j:], abs_gap[:-k_j])

    # Cross-pair USD basket features (NaN when macro_context / peer basket unavailable)
    usd_idx_1m = np.full(n, np.nan, dtype=np.float64)
    usd_idx_5m = np.full(n, np.nan, dtype=np.float64)
    basket_sync_f = np.full(n, np.nan, dtype=np.float64)
    resid_1m = np.full(n, np.nan, dtype=np.float64)
    resid_5m = np.full(n, np.nan, dtype=np.float64)

    if basket_arrays is not None and abs(usd_sign) > 0.5:
        dxy_1m_arr = np.asarray(basket_arrays[0], dtype=np.float64)
        dxy_5m_arr = np.asarray(basket_arrays[1], dtype=np.float64)
        bsync_arr = np.asarray(basket_arrays[2], dtype=np.float64)
        valid_b = np.isfinite(dxy_1m_arr) & np.isfinite(dxy_5m_arr) & np.isfinite(bsync_arr) & (bsync_arr > 0.01)

        bars_1m = max(1, int(round(60.0 / max(1, dt))))
        bars_5m = max(1, int(round(300.0 / max(1, dt))))

        def _lagged_pair_bps(k_bars: int, target_sec: int) -> np.ndarray:
            out = np.full(n, np.nan, dtype=np.float64)
            if n > k_bars:
                dt_ok = (ts[k_bars:] - ts[:-k_bars]) <= int(max(dt, target_sec) * 1.6)
                raw_m = (c[k_bars:] - c[:-k_bars]) / np.maximum(c[:-k_bars], 1e-9) * 10000.0
                out[k_bars:] = np.where(dt_ok, raw_m, np.nan)
            return out

        pair_1m = _lagged_pair_bps(bars_1m, 60)
        pair_5m = _lagged_pair_bps(bars_5m, 300)

        u1 = np.where(valid_b, np.clip(dxy_1m_arr, -50.0, 50.0) * usd_sign, np.nan)
        u5 = np.where(valid_b, np.clip(dxy_5m_arr, -50.0, 50.0) * usd_sign, np.nan)
        bs = np.where(valid_b, np.clip(bsync_arr, 0.0, 1.0), np.nan)
        usd_idx_1m = u1
        usd_idx_5m = u5
        basket_sync_f = bs
        resid_1m = np.where(valid_b & np.isfinite(pair_1m), np.clip(pair_1m - u1, -50.0, 50.0), np.nan)
        resid_5m = np.where(valid_b & np.isfinite(pair_5m), np.clip(pair_5m - u5, -50.0, 50.0), np.nan)

    micro_last_r = np.full(n, np.nan, dtype=np.float64)
    micro_last2_r = np.full(n, np.nan, dtype=np.float64)
    micro_first_half_r = np.full(n, np.nan, dtype=np.float64)
    micro_second_half_r = np.full(n, np.nan, dtype=np.float64)
    micro_late_share = np.full(n, np.nan, dtype=np.float64)
    micro_path_eff = np.full(n, np.nan, dtype=np.float64)
    micro_flips = np.full(n, np.nan, dtype=np.float64)

    if dt > 5 and s5_ts is not None and s5_o is not None and s5_c is not None and len(s5_ts) >= 2:
        st = np.asarray(s5_ts, dtype=np.int64)
        so = np.maximum(np.asarray(s5_o, dtype=np.float64), 1e-9)
        sc = np.maximum(np.asarray(s5_c, dtype=np.float64), 1e-9)
        sr = (sc - so) / so * 10000.0
        m_expected = dt // 5
        idx_range = [n - 1] if only_last else range(20, n)
        ends = np.searchsorted(st, ts + dt, side="left")
        starts = np.searchsorted(st, ts, side="left")
        for i in idx_range:
            a_i, b_i = int(starts[i]), int(ends[i])
            cnt = b_i - a_i
            if cnt >= max(2, m_expected // 2):
                # Reject bogus stub streams whose step doesn't match 5s ticks
                if cnt >= 2 and np.median(np.diff(st[a_i:b_i])) > 7:
                    continue
                seg = sr[a_i:b_i]
                micro_last_r[i] = seg[-1]
                micro_last2_r[i] = float(np.sum(seg[-min(2, cnt) :]))
                half = cnt // 2
                fh = float(np.sum(seg[:half]))
                sh = float(np.sum(seg[half:]))
                micro_first_half_r[i] = fh
                micro_second_half_r[i] = sh
                atr_val = atr20[i] if np.isfinite(atr20[i]) else np.nanmean(rng[max(0, i - 19) : i + 1])
                micro_late_share[i] = (sh - fh) / max(float(atr_val), 0.05)
                abs_s = float(np.sum(np.abs(seg)))
                micro_path_eff[i] = abs(float(np.sum(seg))) / max(abs_s, 0.05)
                if cnt >= 3:
                    nz = np.sign(seg)
                    flips = int(np.sum((nz[1:] * nz[:-1]) < 0))
                    micro_flips[i] = float(flips) / float(cnt - 1)

    ema9_lag3 = lag(ema9, 3)

    cols = [
        ret,
        body_signed,
        r1,
        r2,
        r3,
        ret - r1,
        sum_r2,
        sum_r3,
        sum_r5,
        sum_r8,
        sum_r12,
        sum_r20,
        sum_r40,
        ret / eps_std20,
        body_signed / eps_std20,
        sum_r3 / eps_std20,
        sum_r5 / eps_std20,
        sum_r12 / eps_std20,
        sum_r20 / eps_std20,
        body,
        rng,
        body / eps_rng,
        uwick / eps_rng,
        lwick / eps_rng,
        (lwick - uwick) / eps_rng,
        close_pos,
        prev_close_pos,
        m5_close_pos,
        m15_close_pos,
        atr5,
        atr14,
        atr20,
        atr5 / eps_atr20,
        rng / eps_atr20,
        std5,
        std20,
        std5 / eps_std20,
        tie_rate5,
        tie_rate20,
        zero_rng_rate10,
        streak,
        up_frac10,
        up_frac20,
        ((c - ema9) / np.maximum(ema9, 1e-9) * 10000.0) / eps_atr20,
        ((c - ema21) / np.maximum(ema21, 1e-9) * 10000.0) / eps_atr20,
        ((c - ema50) / np.maximum(ema50, 1e-9) * 10000.0) / eps_atr20,
        ((ema9 - ema9_lag3) / np.maximum(ema9, 1e-9) * 10000.0) / eps_atr20,
        bb_z20,
        donchian_pos20,
        rsi7,
        rsi14,
        v / np.maximum(vol_ma20, 1.0),
        np.sin(2.0 * np.pi * hr / 24.0),
        np.cos(2.0 * np.pi * hr / 24.0),
        rev_rate12,
        rev_rate24,
        autocorr1_24,
        vr5_20,
        vr12_48,
        med_abs_r12,
        jump_max5,
        usd_idx_1m,
        usd_idx_5m,
        basket_sync_f,
        resid_1m,
        resid_5m,
        micro_last_r,
        micro_last2_r,
        micro_first_half_r,
        micro_second_half_r,
        micro_late_share,
        micro_path_eff,
        micro_flips,
    ]
    mat = np.column_stack(cols)

    # Require at least 45 bars of history cleanly; sum_r40 has first valid at idx 39
    valid_hist = np.zeros(n, dtype=bool)
    min_req = 45
    if n > min_req:
        valid_hist[min_req:] = (ts[min_req:] - ts[:-min_req]) <= dt * int(min_req * 1.45)

    return mat, valid_hist


def compute_features_latest(
    candles: Sequence[Dict[str, Any]],
    interval: str,
    micro_candles: Optional[Sequence[Dict[str, Any]]] = None,
    symbol: Optional[str] = None,
    macro_context: Optional[Dict[str, Any]] = None,
) -> Optional[np.ndarray]:
    """Extracts the final closed feature row (shape (len(ALL_FEATURES),)) from API candle dicts."""
    canon = normalize_interval(interval)
    if canon is None or not candles or len(candles) < 25:
        return None
    dt = INTERVAL_SECONDS[canon]

    ordered = sorted(candles, key=lambda row: int(row.get("openTime", row.get("open_time", 0))))
    ts = np.array([int(r.get("openTime", r.get("open_time", 0))) for r in ordered], dtype=np.int64)
    o = np.array([float(r.get("open", 0.0)) for r in ordered], dtype=np.float64)
    h = np.array([float(r.get("high", 0.0)) for r in ordered], dtype=np.float64)
    l = np.array([float(r.get("low", 0.0)) for r in ordered], dtype=np.float64)
    c = np.array([float(r.get("close", 0.0)) for r in ordered], dtype=np.float64)
    v = np.array([float(r.get("volume", 1.0)) for r in ordered], dtype=np.float64)

    if np.any(~np.isfinite(o)) or np.any(~np.isfinite(c)) or np.any(c <= 0):
        return None

    s5_ts = s5_o = s5_c = None
    if dt > 5 and micro_candles and len(micro_candles) >= 2:
        last_close_boundary = int(ts[-1]) + dt
        m_ord = sorted(
            (m for m in micro_candles if int(m.get("openTime", m.get("open_time", 0))) < last_close_boundary),
            key=lambda row: int(row.get("openTime", row.get("open_time", 0))),
        )
        if len(m_ord) >= 2:
            s5_ts = np.array([int(r.get("openTime", r.get("open_time", 0))) for r in m_ord], dtype=np.int64)
            s5_o = np.array([float(r.get("open", 0.0)) for r in m_ord], dtype=np.float64)
            s5_c = np.array([float(r.get("close", 0.0)) for r in m_ord], dtype=np.float64)

    basket_arrays = None
    u_sign = usd_basket_sign(symbol)
    if macro_context and abs(u_sign) > 0.5:
        raw_m = macro_context.get("MacroContext") or macro_context.get("macro_context") or macro_context
        if isinstance(raw_m, dict):
            d1 = _extract_macro_field(raw_m, "DxyMomentum1mBps", "dxy_momentum_1m_bps")
            d5 = _extract_macro_field(raw_m, "DxyMomentum5mBps", "dxy_momentum_5m_bps")
            bs = _extract_macro_field(raw_m, "BasketSyncScore", "basket_sync_score")
            if d1 is not None and d5 is not None and bs is not None and bs > 0.01:
                n_c = len(c)
                a1 = np.full(n_c, np.nan, dtype=np.float64)
                a5 = np.full(n_c, np.nan, dtype=np.float64)
                ab = np.full(n_c, np.nan, dtype=np.float64)
                a1[-1] = d1
                a5[-1] = d5
                ab[-1] = bs
                basket_arrays = (a1, a5, ab)

    mat, _ = compute_features_batch(
        ts, o, h, l, c, v, dt,
        s5_ts=s5_ts, s5_o=s5_o, s5_c=s5_c,
        only_last=True,
        basket_arrays=basket_arrays,
        usd_sign=u_sign,
    )
    if len(mat) == 0:
        return None
    return mat[-1]


# ---------------------------------------------------------------------------
# Pure-NumPy Flat GBDT Serialization & Fast Evaluation (Zero Pickle / Sklearn)
# ---------------------------------------------------------------------------

def pack_hgb_for_npz(model: Any, prefix: str) -> Dict[str, np.ndarray]:
    """Packs sklearn HistGradientBoostingClassifier binary trees into flat 1D arrays."""
    baselines = np.array([float(model._baseline_prediction[0, 0])], dtype=np.float64)
    roots: List[int] = []
    f_idx: List[np.ndarray] = []
    thresh: List[np.ndarray] = []
    miss_left: List[np.ndarray] = []
    left_arr: List[np.ndarray] = []
    right_arr: List[np.ndarray] = []
    is_leaf_arr: List[np.ndarray] = []
    val_arr: List[np.ndarray] = []

    offset = 0
    for pred_list in model._predictors:
        nodes = pred_list[0].nodes
        cnt = len(nodes)
        roots.append(offset)
        f_idx.append(nodes["feature_idx"].astype(np.int32))
        thresh.append(nodes["num_threshold"].astype(np.float64))
        miss_left.append(nodes["missing_go_to_left"].astype(np.uint8))
        is_leaf = nodes["is_leaf"].astype(np.uint8)
        is_leaf_arr.append(is_leaf)
        left_arr.append(nodes["left"].astype(np.int32) + offset)
        right_arr.append(nodes["right"].astype(np.int32) + offset)
        val_arr.append(nodes["value"].astype(np.float64))
        offset += cnt

    return {
        f"{prefix}_baseline": baselines,
        f"{prefix}_roots": np.array(roots, dtype=np.int32),
        f"{prefix}_fidx": np.concatenate(f_idx),
        f"{prefix}_thresh": np.concatenate(thresh),
        f"{prefix}_mleft": np.concatenate(miss_left),
        f"{prefix}_left": np.concatenate(left_arr),
        f"{prefix}_right": np.concatenate(right_arr),
        f"{prefix}_leaf": np.concatenate(is_leaf_arr),
        f"{prefix}_val": np.concatenate(val_arr),
    }


def _sigmoid_clip(z: np.ndarray | float) -> np.ndarray | float:
    z_c = np.clip(z, -30.0, 30.0)
    return 1.0 / (1.0 + np.exp(-z_c))


def eval_hgb_flat_single(arrays: Dict[str, np.ndarray], prefix: str, x: np.ndarray) -> float:
    """Evaluates a packed GBDT binary classifier on a single 1D feature vector in ~0.05ms."""
    raw = float(arrays[f"{prefix}_baseline"][0])
    roots = arrays[f"{prefix}_roots"]
    fidx = arrays[f"{prefix}_fidx"]
    thresh = arrays[f"{prefix}_thresh"]
    mleft = arrays[f"{prefix}_mleft"]
    left = arrays[f"{prefix}_left"]
    right = arrays[f"{prefix}_right"]
    leaf = arrays[f"{prefix}_leaf"]
    val = arrays[f"{prefix}_val"]

    for ni in roots:
        cur = int(ni)
        while leaf[cur] == 0:
            v = float(x[int(fidx[cur])])
            if math.isnan(v):
                go_l = mleft[cur] != 0
            else:
                go_l = v <= float(thresh[cur])
            cur = int(left[cur] if go_l else right[cur])
        raw += float(val[cur])
    return float(_sigmoid_clip(raw))


def eval_hgb_flat_batch(arrays: Dict[str, np.ndarray], prefix: str, X: np.ndarray) -> np.ndarray:
    """Evaluates a packed GBDT binary classifier vectorized over (N, F)."""
    n = len(X)
    raw = np.full(n, float(arrays[f"{prefix}_baseline"][0]), dtype=np.float64)
    roots = arrays[f"{prefix}_roots"]
    fidx = arrays[f"{prefix}_fidx"]
    thresh = arrays[f"{prefix}_thresh"]
    mleft = arrays[f"{prefix}_mleft"]
    left = arrays[f"{prefix}_left"]
    right = arrays[f"{prefix}_right"]
    leaf = arrays[f"{prefix}_leaf"]
    val = arrays[f"{prefix}_val"]

    for root in roots:
        cur = np.full(n, int(root), dtype=np.int32)
        while True:
            is_lf = leaf[cur] != 0
            if np.all(is_lf):
                break
            active = ~is_lf
            ac = cur[active]
            fv = X[active, fidx[ac]]
            is_nan = np.isnan(fv)
            go_l = np.where(is_nan, mleft[ac] != 0, fv <= thresh[ac])
            cur[active] = np.where(go_l, left[ac], right[ac])
        raw += val[cur]
    return _sigmoid_clip(raw)


def _eval_lr(arrays: Dict[str, np.ndarray], prefix: str, X_core: np.ndarray) -> np.ndarray:
    meds = arrays[f"{prefix}_lr_meds"]
    mean = arrays[f"{prefix}_lr_mean"]
    scale = arrays[f"{prefix}_lr_scale"]
    coef = arrays[f"{prefix}_lr_coef"]
    intercept = float(arrays[f"{prefix}_lr_intercept"][0])

    x_imp = np.where(np.isfinite(X_core), X_core, meds)
    x_std = np.clip((x_imp - mean) / np.maximum(scale, 1e-9), -8.0, 8.0)
    z = x_std @ coef + intercept
    return _sigmoid_clip(z)


class SubminuteQuantPredictor:
    """Thread-safe singleton predictor for s5, s10, s15, s30, 1m PocketOption/Forex signals."""

    _instance: Optional["SubminuteQuantPredictor"] = None
    _lock = threading.Lock()

    def __init__(self, npz_path: str = DEFAULT_NPZ_PATH, meta_path: str = DEFAULT_META_PATH):
        self.npz_path = npz_path
        self.meta_path = meta_path
        self.arrays: Dict[str, np.ndarray] = {}
        self.meta: Dict[str, Any] = {}
        self.loaded_intervals: set[str] = set()
        self._load()

    @classmethod
    def get_instance(cls) -> "SubminuteQuantPredictor":
        with cls._lock:
            if cls._instance is None:
                cls._instance = cls()
            return cls._instance

    @classmethod
    def reset_instance(cls) -> None:
        with cls._lock:
            cls._instance = None

    def _load(self) -> None:
        if not (os.path.exists(self.npz_path) and os.path.exists(self.meta_path)):
            return
        try:
            with open(self.meta_path, "r", encoding="utf-8") as f:
                self.meta = json.load(f)
            with np.load(self.npz_path, allow_pickle=False) as z:
                self.arrays = {k: z[k] for k in z.files}
            for iv, info in self.meta.get("intervals", {}).items():
                if info.get("gate_pass") and f"{iv}_dir_roots" in self.arrays:
                    self.loaded_intervals.add(iv)
        except Exception as exc:
            print(f"[SubminuteQuantPredictor] Load warning: {exc}")
            self.loaded_intervals.clear()

    def is_available_for(self, interval: Optional[str]) -> bool:
        canon = normalize_interval(interval)
        return canon is not None and canon in self.loaded_intervals

    def is_available(self) -> bool:
        return len(self.loaded_intervals) > 0

    def get_interval_meta(self, interval: Optional[str]) -> Dict[str, Any]:
        canon = normalize_interval(interval)
        if not canon:
            return {}
        return dict(self.meta.get("intervals", {}).get(canon, {}))

    def predict(
        self,
        symbol: str,
        interval: str,
        candles: Sequence[Dict[str, Any]],
        mtf_candles: Optional[Sequence[Dict[str, Any]]] = None,
        micro_candles: Optional[Sequence[Dict[str, Any]]] = None,
        macro_context: Optional[Dict[str, Any]] = None,
    ) -> Optional[Tuple[str, float, str, int, float, Dict[str, Any]]]:
        """
        Returns `(direction, confidence, model_version, horizon_candles, raw_confidence, details)`
        where `raw_confidence` is signed P(BUY) in [0.0, 1.0] (`>0.5` BUY, `<0.5` PUT).
        Returns None if `interval` is not supported or model is unavailable.
        """
        canon = normalize_interval(interval)
        if canon is None or canon not in self.loaded_intervals:
            return None

        feat = compute_features_latest(
            candles,
            canon,
            micro_candles=micro_candles,
            symbol=symbol,
            macro_context=macro_context,
        )
        if feat is None:
            return ("NEUTRAL", 0.50, MODEL_VERSION, 1, 0.50, {"reason": "insufficient_history"})

        feat_mir = mirror_feature_matrix(feat, ALL_FEATURES)

        iv_meta = self.meta["intervals"][canon]
        hgb_feats_len = int(iv_meta["n_hgb_features"])
        hgb_idx = iv_meta.get("hgb_feature_indices")
        core_len = int(iv_meta["n_core_features"])

        x_hgb = feat[hgb_idx] if hgb_idx is not None else feat[:hgb_feats_len]
        x_hgb_mir = feat_mir[hgb_idx] if hgb_idx is not None else feat_mir[:hgb_feats_len]
        x_core = feat[:core_len]
        x_core_mir = feat_mir[:core_len]

        p_hgb = 0.5 * (
            eval_hgb_flat_single(self.arrays, f"{canon}_dir", x_hgb)
            + (1.0 - eval_hgb_flat_single(self.arrays, f"{canon}_dir", x_hgb_mir))
        )
        p_lr = 0.5 * (
            float(_eval_lr(self.arrays, canon, x_core.reshape(1, -1))[0])
            + (1.0 - float(_eval_lr(self.arrays, canon, x_core_mir.reshape(1, -1))[0]))
        )
        w_hgb = float(iv_meta.get("w_hgb", 0.70))
        p_raw_up = w_hgb * p_hgb + (1.0 - w_hgb) * p_lr

        # Tie probability hazard check (symmetric across mirror)
        p_tie = 0.0
        if f"{canon}_tie_roots" in self.arrays:
            p_tie = 0.5 * (
                eval_hgb_flat_single(self.arrays, f"{canon}_tie", x_hgb)
                + eval_hgb_flat_single(self.arrays, f"{canon}_tie", x_hgb_mir)
            )

        last_ts = max(int(r.get("openTime", r.get("open_time", 0))) for r in candles)
        weekend_otc = is_weekend_otc_unix(last_ts)
        rollover_otc = is_rollover_otc_unix(last_ts)
        jump_idx = ALL_FEATURES.index("jump_max5")
        jump_max5_val = float(feat[jump_idx]) if np.isfinite(feat[jump_idx]) else 0.0
        jump_guarded = jump_max5_val > 30.0

        # Calibrate directional strength monotonically via OOF isotonic curve
        raw_dir_strength = max(p_raw_up, 1.0 - p_raw_up)
        iso_x = self.arrays[f"{canon}_iso_x"]
        iso_y = self.arrays[f"{canon}_iso_y"]
        fx_cal_conf = float(np.interp(raw_dir_strength, iso_x, iso_y))

        # Select honest regime calibrator: clean FX vs synthetic/rollover OTC (do not force OTC curve on FX momentum jumps)
        cal_conf = fx_cal_conf
        if (weekend_otc or rollover_otc) and f"{canon}_iso_otc_x" in self.arrays:
            otc_x = self.arrays[f"{canon}_iso_otc_x"]
            otc_y = self.arrays[f"{canon}_iso_otc_y"]
            cal_conf = float(np.interp(raw_dir_strength, otc_x, otc_y))

        # Soft tie-risk dampener when broker stream approaches dead/flat regime (>40% tie prob)
        tie_cap = float(iv_meta.get("tie_dampen_threshold", 0.40))
        if p_tie > tie_cap:
            shrink = max(0.25, 1.0 - (p_tie - tie_cap) * 2.0)
            fx_cal_conf = 0.50 + (fx_cal_conf - 0.50) * shrink
            cal_conf = 0.50 + (cal_conf - 0.50) * shrink

        # Preserve uncapped FX directional estimate for telemetry / experiments
        uncapped_conf = float(np.clip(fx_cal_conf, 0.50, 0.80))
        raw_uncapped_prob = uncapped_conf if p_raw_up >= 0.50 else (1.0 - uncapped_conf)

        if weekend_otc:
            cal_conf = min(cal_conf, 0.52)

        cal_conf = float(np.clip(cal_conf, 0.50, 0.80))

        # Check tie_rate20 hard dead guard (e.g. >= 75% zero-return bars in last 20)
        tie_rate20_idx = CORE_FEATURES.index("tie_rate20")
        tie_rate20_val = float(feat[tie_rate20_idx]) if np.isfinite(feat[tie_rate20_idx]) else 0.0
        if tie_rate20_val >= 0.75 or cal_conf <= 0.505:
            direction = "NEUTRAL"
            cal_conf = 0.50
            signed_raw = 0.50
        elif p_raw_up >= 0.50:
            direction = "BUY"
            signed_raw = cal_conf
        else:
            direction = "PUT"
            signed_raw = 1.0 - cal_conf

        # Top standardized feature attributions (interpretable driver summary)
        meds = self.arrays[f"{canon}_lr_meds"]
        mean = self.arrays[f"{canon}_lr_mean"]
        scale = self.arrays[f"{canon}_lr_scale"]
        x_imp = np.where(np.isfinite(x_core), x_core, meds)
        z_vals = (x_imp - mean) / np.maximum(scale, 1e-9)
        top_idx = np.argsort(np.abs(z_vals))[::-1][:3]
        top_features = [f"{CORE_FEATURES[int(i)]}:{z_vals[int(i)]:+.2f}" for i in top_idx]
        micro_start = len(CORE_FEATURES) + len(REGIME_FEATURES) + len(BASKET_FEATURES)
        if INTERVAL_SECONDS[canon] > 5 and np.isfinite(feat[micro_start]):
            top_features.append(f"micro_last_r:{feat[micro_start]:+.2f}")
        basket_idx = ALL_FEATURES.index("resid_1m")
        basket_active = bool(np.isfinite(feat[basket_idx]))
        if basket_active:
            top_features.append(f"usd_resid_1m:{feat[basket_idx]:+.2f}")
        if weekend_otc:
            top_features.insert(0, "weekend_prng_otc:1.00")
        elif rollover_otc:
            top_features.insert(0, "rollover_otc:1.00")
        elif jump_guarded:
            top_features.insert(0, f"jump_guarded:{jump_max5_val:.1f}")

        details = {
            "accuracy": iv_meta.get("oof_accuracy"),
            "auc": iv_meta.get("oof_auc"),
            "n_train": iv_meta.get("n_train"),
            "p_tie": round(float(p_tie), 4),
            "weekend_otc": weekend_otc,
            "rollover_otc": rollover_otc,
            "jump_guarded": jump_guarded,
            "basket_active": basket_active,
            "raw_uncapped_prob": round(float(raw_uncapped_prob), 4),
            "top_features": top_features,
        }
        return (direction, float(cal_conf), MODEL_VERSION, 1, float(signed_raw), details)
