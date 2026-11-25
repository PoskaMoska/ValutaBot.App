import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pytest
from sklearn.ensemble import HistGradientBoostingClassifier

ML_DIR = Path(__file__).resolve().parents[1]
if str(ML_DIR) not in sys.path:
    sys.path.insert(0, str(ML_DIR))

from models.subminute_quant_predictor import (
    ALL_FEATURES,
    DEFAULT_META_PATH,
    DEFAULT_NPZ_PATH,
    HTF_FEATURES,
    MICRO_FEATURES,
    MODEL_VERSION,
    STATE_FEATURES,
    SubminuteQuantPredictor,
    canonical_asset_base,
    compute_features_batch,
    compute_features_latest,
    eval_hgb_flat_batch,
    eval_hgb_flat_single,
    mirror_feature_matrix,
    normalize_interval,
    pack_hgb_for_npz,
)


def _synthetic_candles(n: int = 120, dt: int = 15, base_ts: int = 1794500000, step_noise: float = 0.00015):
    rng = np.random.default_rng(7)
    candles = []
    px = 1.1000
    for i in range(n):
        move = float(rng.normal(0.0, step_noise))
        o = px
        c = max(0.01, px + move)
        h = max(o, c) + abs(float(rng.normal(0.0, step_noise * 0.4))) + 1e-5
        l = min(o, c) - abs(float(rng.normal(0.0, step_noise * 0.4))) - 1e-5
        candles.append({
            "openTime": base_ts + i * dt,
            "open": o,
            "high": h,
            "low": l,
            "close": c,
            "volume": float(100.0 + abs(move) * 1e5),
        })
        px = c
    return candles


def test_interval_normalization_covers_all_subminute_formats():
    assert normalize_interval("s5") == "s5"
    assert normalize_interval("5S") == "s5"
    assert normalize_interval("s10") == "s10"
    assert normalize_interval("s15") == "s15"
    assert normalize_interval("s30") == "s30"
    assert normalize_interval("m1") == "1min"
    assert normalize_interval("1m") == "1min"
    assert normalize_interval("1min") == "1min"
    assert normalize_interval("60s") == "1min"
    assert normalize_interval("5m") is None
    assert normalize_interval("15m") is None
    assert normalize_interval("h1") is None
    assert normalize_interval(None) is None


def test_pure_numpy_tree_walker_matches_sklearn_with_nans():
    rng = np.random.default_rng(123)
    X = rng.normal(size=(600, 8))
    X[rng.random(X.shape) < 0.18] = np.nan
    y = ((X[:, 0] + np.nan_to_num(X[:, 1]) - 0.3 * np.nan_to_num(X[:, 2])) > 0).astype(int)

    clf = HistGradientBoostingClassifier(
        max_iter=40, max_depth=4, min_samples_leaf=20, l2_regularization=2.0, random_state=42
    ).fit(X, y)

    packed = pack_hgb_for_npz(clf, "test")
    X_eval = np.vstack([X[:120], np.full((5, 8), np.nan), np.zeros((5, 8))])
    p_sk = clf.predict_proba(X_eval)[:, 1]
    p_batch = eval_hgb_flat_batch(packed, "test", X_eval)
    p_single = np.array([eval_hgb_flat_single(packed, "test", row) for row in X_eval])

    np.testing.assert_allclose(p_batch, p_sk, rtol=0.0, atol=1e-10)
    np.testing.assert_allclose(p_single, p_sk, rtol=0.0, atol=1e-10)


def test_feature_causality_future_candle_change_cannot_alter_past_row():
    c_15 = _synthetic_candles(n=90, dt=15)
    c_s5 = _synthetic_candles(n=270, dt=5, base_ts=c_15[0]["openTime"])

    # Prefix [:-1] vs mutated last candle
    feat_prefix = compute_features_latest(c_15[:-1], "s15", micro_candles=c_s5[:-3])
    c_15_mut = list(c_15)
    c_15_mut[-1] = {**c_15_mut[-1], "close": c_15_mut[-1]["close"] * 1.5, "high": c_15_mut[-1]["high"] * 1.6}
    feat_prefix_after = compute_features_latest(c_15_mut[:-1], "s15", micro_candles=c_s5[:-3])

    assert feat_prefix is not None and feat_prefix_after is not None
    np.testing.assert_allclose(feat_prefix, feat_prefix_after, rtol=0.0, atol=0.0, equal_nan=True)


def test_compute_features_latest_matches_batch_row():
    c_1m = _synthetic_candles(n=100, dt=60, base_ts=1794500000)
    c_s5 = _synthetic_candles(n=1200, dt=5, base_ts=1794500000)

    ts = np.array([r["openTime"] for r in c_1m], dtype=np.int64)
    o = np.array([r["open"] for r in c_1m], dtype=np.float64)
    h = np.array([r["high"] for r in c_1m], dtype=np.float64)
    l = np.array([r["low"] for r in c_1m], dtype=np.float64)
    c = np.array([r["close"] for r in c_1m], dtype=np.float64)
    v = np.array([r["volume"] for r in c_1m], dtype=np.float64)
    st = np.array([r["openTime"] for r in c_s5], dtype=np.int64)
    so = np.array([r["open"] for r in c_s5], dtype=np.float64)
    sh = np.array([r["high"] for r in c_s5], dtype=np.float64)
    sl = np.array([r["low"] for r in c_s5], dtype=np.float64)
    sc = np.array([r["close"] for r in c_s5], dtype=np.float64)

    batch_mat, _ = compute_features_batch(ts, o, h, l, c, v, 60, s5_ts=st, s5_o=so, s5_c=sc, s5_h=sh, s5_l=sl)
    latest_row = compute_features_latest(c_1m, "m1", micro_candles=c_s5)
    assert latest_row is not None
    assert len(latest_row) == len(ALL_FEATURES)
    np.testing.assert_allclose(latest_row, batch_mat[-1], rtol=0.0, atol=1e-12, equal_nan=True)


def test_bogus_micro_candles_interval_rejected_safely():
    c_15 = _synthetic_candles(n=90, dt=15)
    # 60s mock candles masquerading as s5 must not inject bogus micro stats
    bogus_1m = _synthetic_candles(n=90, dt=60, base_ts=c_15[0]["openTime"])
    row = compute_features_latest(c_15, "s15", micro_candles=bogus_1m)
    assert row is not None
    assert np.all(np.isnan(row[-len(MICRO_FEATURES):]))
    for sf in STATE_FEATURES:
        assert np.isnan(row[ALL_FEATURES.index(sf)])


def test_loaded_production_bundle_all_5_timeframes_and_latency():
    if not (os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH)):
        pytest.skip("subminute_quant_v7 model artifacts not present yet")

    predictor = SubminuteQuantPredictor.get_instance()
    assert predictor.is_available()

    for iv_in, dt in [("s5", 5), ("s10", 10), ("s15", 15), ("s30", 30), ("m1", 60)]:
        assert predictor.is_available_for(iv_in), f"{iv_in} should be loaded in SubminuteQuantPredictor"
        iv_meta = predictor.get_interval_meta(iv_in)
        assert iv_meta.get("gate_pass") is True, f"{iv_in} must pass quality gate"
        candles = _synthetic_candles(n=120, dt=dt, base_ts=1794500000)
        micro = _synthetic_candles(n=480, dt=5, base_ts=candles[-40]["openTime"]) if dt > 5 else None

        t0 = time.perf_counter()
        res = predictor.predict("EURUSD", iv_in, candles, micro_candles=micro)
        elapsed_ms = (time.perf_counter() - t0) * 1000.0

        assert res is not None
        direction, conf, ver, hor, raw_conf, details = res
        assert ver == MODEL_VERSION
        assert hor == 1
        assert direction in ("BUY", "PUT", "NEUTRAL")
        assert 0.50 <= conf <= 0.80
        assert 0.20 <= raw_conf <= 0.80
        if not iv_meta.get("gate_pass"):
            assert direction == "NEUTRAL"
            assert conf == 0.50
        elif direction == "BUY":
            assert raw_conf > 0.50
        elif direction == "PUT":
            assert raw_conf < 0.50
        else:
            assert abs(raw_conf - 0.50) < 1e-9
        assert details.get("auc") is not None and details["auc"] > 0.51
        assert elapsed_ms < 25.0, f"{iv_in} latency too high: {elapsed_ms:.1f}ms"

    # Dead flat candles must return NEUTRAL
    flat = [
        {"openTime": 1794500000 + i * 5, "open": 1.10, "high": 1.10, "low": 1.10, "close": 1.10, "volume": 1.0}
        for i in range(80)
    ]
    res_flat = predictor.predict("EURUSD", "s5", flat)
    assert res_flat is not None
    assert res_flat[0] == "NEUTRAL"
    assert res_flat[1] == 0.50
    assert res_flat[4] == 0.50


def test_fastapi_predict_routes_all_subminute_intervals_to_quant():
    if not (os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH)):
        pytest.skip("subminute_quant_v7 model artifacts not present yet")

    from fastapi.testclient import TestClient
    import main as ml_main

    SubminuteQuantPredictor.reset_instance()
    secret = os.environ.get("INTERNAL_API_SECRET", "default_secret")
    headers = {"X-Internal-Secret": secret}

    with TestClient(ml_main.app) as client:
        for iv, dt in [("s5", 5), ("s10", 10), ("s15", 15), ("s30", 30), ("m1", 60)]:
            candles = _synthetic_candles(n=120, dt=dt, base_ts=1794500000)
            payload = {
                "symbol": "EURUSD",
                "interval": iv,
                "candles": candles,
                "is_forex": True,
            }
            if dt > 5:
                payload["micro_candles"] = _synthetic_candles(n=480, dt=5, base_ts=candles[-40]["openTime"])

            resp = client.post("/predict", json=payload, headers=headers)
            assert resp.status_code == 200, f"{iv} HTTP {resp.status_code}: {resp.text}"
            body = resp.json()
            assert body["model_version"] == MODEL_VERSION
            assert body["direction"] in ("BUY", "PUT", "NEUTRAL")
            assert 0.50 <= body["confidence"] <= 0.80
            assert body["auc"] is not None and body["auc"] > 0.51
            assert isinstance(body["top_features"], list) and len(body["top_features"]) >= 3
            assert body.get("raw_uncapped_prob") is not None
            assert 0.20 <= body["raw_uncapped_prob"] <= 0.80


def test_weekend_synthetic_otc_caps_probability_honestly():
    if not (os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH)):
        pytest.skip("subminute_quant_v7 model artifacts not present yet")

    predictor = SubminuteQuantPredictor.get_instance()
    # Saturday 2026-11-14 14:00:00Z = 1794664800 -> synthetic weekend PRNG window
    sat_candles = _synthetic_candles(n=120, dt=5, base_ts=1794664800)
    res_sat = predictor.predict("EURUSD", "s5", sat_candles)
    assert res_sat is not None
    assert res_sat[0] in ("BUY", "PUT", "NEUTRAL")
    assert res_sat[1] <= 0.52
    assert res_sat[5].get("weekend_otc") is True
    assert res_sat[5].get("raw_uncapped_prob") is not None
    assert 0.20 <= res_sat[5]["raw_uncapped_prob"] <= 0.80

    # Also verify /predict HTTP response carries raw_uncapped_prob while clamping confidence <= 0.52 on weekends
    from fastapi.testclient import TestClient
    import main as ml_main

    secret = os.environ.get("INTERNAL_API_SECRET", "default_secret")
    with TestClient(ml_main.app) as client:
        resp = client.post(
            "/predict",
            json={"symbol": "EURUSD_otc", "interval": "s5", "candles": sat_candles, "is_forex": False},
            headers={"X-Internal-Secret": secret},
        )
        assert resp.status_code == 200, f"HTTP {resp.status_code}: {resp.text}"
        body = resp.json()
        assert body["confidence"] <= 0.52
        assert body.get("raw_uncapped_prob") is not None
        assert 0.20 <= body["raw_uncapped_prob"] <= 0.80


def test_mirror_symmetry_and_macro_basket_fuzz_safety():
    c = _synthetic_candles(n=90, dt=15, base_ts=1794500000)
    micro_s5 = _synthetic_candles(n=96, dt=5, base_ts=c[-30]["openTime"])
    feat = compute_features_latest(
        c, "s15", micro_candles=micro_s5, symbol="EURUSD",
        macro_context={
            "DxyMomentum5sBps": 0.9,
            "DxyMomentum15sBps": 1.6,
            "DxyMomentum30sBps": 2.0,
            "DxyMomentum1mBps": 2.4,
            "DxyMomentum5mBps": 4.1,
            "BasketSyncScore": 0.83,
            "BasketSync5sScore": 0.80,
        },
    )
    assert feat is not None
    for fn in (
        "usd_idx_5s", "resid_5s", "sg_vel_z", "kal_dev_z",
        "z_s60", "dist_ema120", "ch60", "er20", "m5_slope_z", "m15_slope_z",
        "micro_last5_cpos", "micro_last5_wick", "micro_last_z", "micro_last2_z", "micro_sec_half_z",
    ):
        assert np.isfinite(feat[ALL_FEATURES.index(fn)]), f"{fn} should be finite"
    mir = mirror_feature_matrix(feat, ALL_FEATURES)
    back = mirror_feature_matrix(mir, ALL_FEATURES)
    np.testing.assert_allclose(back, feat, rtol=0.0, atol=1e-12, equal_nan=True)
    for fn in (
        "usd_idx_5s", "resid_5s", "sg_vel_z", "kal_dev_z",
        "z_s60", "dist_ema120", "m5_slope_z", "m15_slope_z",
        "micro_last5_cpos", "micro_last5_wick", "micro_last_z", "micro_last2_z", "micro_sec_half_z",
    ):
        idx_fn = ALL_FEATURES.index(fn)
        assert mir[idx_fn] == pytest.approx(-feat[idx_fn], abs=1e-12)
    for ch_fn in ("ch60", "ch120"):
        idx_ch = ALL_FEATURES.index(ch_fn)
        assert mir[idx_ch] == pytest.approx(1.0 - feat[idx_ch], abs=1e-12)
    for sym_fn in ("er20", "er60"):
        idx_sym = ALL_FEATURES.index(sym_fn)
        assert mir[idx_sym] == pytest.approx(feat[idx_sym], abs=1e-12)

    # Full 120+ bar horizon verifies z_s120 + batch/latest exact schema layout (MICRO_FEATURES trailing)
    assert ALL_FEATURES[-len(MICRO_FEATURES):] == MICRO_FEATURES
    assert len(HTF_FEATURES) == 9
    c_long = _synthetic_candles(n=130, dt=30, base_ts=1794500000)
    micro_long = _synthetic_candles(n=240, dt=5, base_ts=c_long[-40]["openTime"])
    feat_long = compute_features_latest(c_long, "s30", micro_candles=micro_long, symbol="EURUSD")
    assert feat_long is not None
    assert np.isfinite(feat_long[ALL_FEATURES.index("z_s120")])
    mir_long = mirror_feature_matrix(feat_long, ALL_FEATURES)
    assert mir_long[ALL_FEATURES.index("z_s120")] == pytest.approx(-feat_long[ALL_FEATURES.index("z_s120")], abs=1e-12)

    # Negate percentage steps cleanly via geometric inverse (1/P -> linear flip of steps)
    if os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH):
        predictor = SubminuteQuantPredictor.get_instance()
        # Feature-level mirror evaluation symmetry via /predict macro fuzz resilience
        res_fuzz = predictor.predict(
            "EURUSD", "s15", c,
            macro_context={"DxyMomentum1mBps": -888888.0, "DxyMomentum5mBps": float("nan"), "BasketSyncScore": 999.0},
        )
        assert res_fuzz is not None
        assert res_fuzz[0] in ("BUY", "PUT", "NEUTRAL")
        assert 0.50 <= res_fuzz[1] <= 0.80

        # Daily rollover OTC window (Thu 22:15 UTC) triggers honest rollover calibrator without crashing
        rollover_base_ts = int(datetime(2026, 11, 12, 22, 15, tzinfo=timezone.utc).timestamp())
        rollover_candles = _synthetic_candles(n=120, dt=15, base_ts=rollover_base_ts)
        res_roll = predictor.predict("EURUSD_PO", "s15", rollover_candles)
        assert res_roll is not None
        assert res_roll[5].get("rollover_otc") is True
        assert res_roll[1] <= 0.52
        for tf_key in ("s5", "s10", "s15", "s30", "1min"):
            assert float(np.max(predictor.arrays[f"{tf_key}_iso_otc_y"])) <= 0.53


def test_weekend_otc_active_candles_preserve_direction_never_neutral():
    """On active non-flat Sunday OTC candles, tight calibration must NOT erase direction to NEUTRAL."""
    if not (os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH)):
        pytest.skip("SubminuteQuant v7 artifacts not present locally")

    predictor = SubminuteQuantPredictor.get_instance()
    sun_ts = int(datetime(2026, 11, 22, 11, 0, tzinfo=timezone.utc).timestamp())

    for tf_key, dt in (("s5", 5), ("s10", 10), ("s15", 15), ("s30", 30), ("1min", 60)):
        iv_meta = predictor.get_interval_meta(tf_key)
        sun_candles = _synthetic_candles(n=120, dt=dt, base_ts=sun_ts)
        res = predictor.predict("EURUSD_PO", tf_key, sun_candles)
        assert res is not None
        direction, conf, _, _, raw_conf, meta = res
        assert meta.get("weekend_otc") is True
        if not iv_meta.get("gate_pass"):
            assert direction == "NEUTRAL"
            assert conf == pytest.approx(0.50, abs=1e-6)
            continue
        assert direction in ("BUY", "PUT"), f"{tf_key} collapsed active weekend OTC to {direction}"
        assert 0.51 <= conf <= 0.52, f"{tf_key} conf={conf} outside [0.51, 0.52]"
        if direction == "BUY":
            assert raw_conf > 0.50
        else:
            assert raw_conf < 0.50

    # Frozen dead-flat Sunday candles must still fail closed to NEUTRAL
    flat_sun = [
        {"open": 1.1000, "high": 1.1000, "low": 1.1000, "close": 1.1000, "volume": 1.0, "time": sun_ts + i * 60}
        for i in range(120)
    ]
    res_flat = predictor.predict("EURUSD_PO", "1min", flat_sun)
    assert res_flat is not None
    assert res_flat[0] == "NEUTRAL"
    assert res_flat[1] == pytest.approx(0.50, abs=1e-6)


def test_canonical_asset_base_and_per_pair_isotonic_shrinkage():
    from train_subminute_quant import _fit_shrunk_pair_isotonic

    assert canonical_asset_base("GBP/USD_PO") == "GBPUSD"
    assert canonical_asset_base("eurusd_otc") == "EURUSD"
    assert canonical_asset_base("USDJPY") == "USDJPY"
    assert canonical_asset_base(None) == ""

    glob_x = np.array([0.50, 0.55, 0.60, 0.70, 1.0])
    glob_y = np.array([0.50, 0.54, 0.58, 0.65, 0.65])
    rng = np.random.default_rng(42)

    # Below min_samples -> None (falls back cleanly to global curve)
    small_x = rng.uniform(0.50, 0.70, size=800)
    small_y = (rng.random(800) < 0.72).astype(float)
    assert _fit_shrunk_pair_isotonic(small_x, small_y, glob_x, glob_y, min_samples=1200) is None

    # Sufficient OOF samples -> strictly monotonic blended curve in [0.50, 0.75]
    pair_x = np.linspace(0.50, 0.72, 10000)
    probs = np.clip(0.50 + 1.15 * (pair_x - 0.50), 0.50, 0.75)
    pair_w = (rng.random(len(pair_x)) < probs).astype(float)
    res = _fit_shrunk_pair_isotonic(pair_x, pair_w, glob_x, glob_y, min_samples=1200, shrink_n0=5000.0)
    assert res is not None
    ux, uy = res
    assert len(ux) == len(uy) and len(ux) >= 4
    assert np.all(np.diff(uy) >= -1e-12)
    assert float(np.min(uy)) >= 0.50 and float(np.max(uy)) <= 0.75


def test_per_asset_calibrator_routing_and_weekday_otc_isolation():
    if not (os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH)):
        pytest.skip("SubminuteQuant v7 artifacts not present locally")

    predictor = SubminuteQuantPredictor.get_instance()
    # Monday active FX session timestamp (2026-11-23 14:00:00Z -> clean FX window)
    fx_ts = int(datetime(2026, 11, 23, 14, 0, 0, tzinfo=timezone.utc).timestamp())
    c_fx = _synthetic_candles(n=120, dt=5, base_ts=fx_ts)

    # Inject synthetic per-pair calibrator for GBPUSD cleanly in memory and restore afterward
    orig_x = predictor.arrays.get("s5_GBPUSD_iso_x")
    orig_y = predictor.arrays.get("s5_GBPUSD_iso_y")
    try:
        predictor.arrays["s5_GBPUSD_iso_x"] = np.array([0.50, 0.51, 1.0], dtype=np.float64)
        predictor.arrays["s5_GBPUSD_iso_y"] = np.array([0.50, 0.73, 0.73], dtype=np.float64)

        res_gbp = predictor.predict("GBPUSD_PO", "s5", c_fx)
        res_unknown = predictor.predict("NZDUSD_PO", "s5", c_fx)
        res_otc = predictor.predict("GBPUSD_otc", "s5", c_fx)

        assert res_gbp is not None and res_unknown is not None and res_otc is not None
        assert res_gbp[5].get("calibrator") == "pair:GBPUSD"
        assert res_unknown[5].get("calibrator") == "global"
        assert res_otc[5].get("calibrator") == "otc_rollover"
        assert res_otc[1] <= 0.525
    finally:
        if orig_x is not None and orig_y is not None:
            predictor.arrays["s5_GBPUSD_iso_x"] = orig_x
            predictor.arrays["s5_GBPUSD_iso_y"] = orig_y
        else:
            predictor.arrays.pop("s5_GBPUSD_iso_x", None)
            predictor.arrays.pop("s5_GBPUSD_iso_y", None)


def test_lightgbm_flat_npz_parity_and_ensemble_symmetry():
    lgb = pytest.importorskip("lightgbm")
    from sklearn.ensemble import HistGradientBoostingClassifier
    from sklearn.linear_model import LogisticRegression
    from sklearn.preprocessing import StandardScaler
    from models.subminute_quant_predictor import CORE_FEATURES, ALL_FEATURES, mirror_feature_matrix
    from train_subminute_quant import pack_lgb_for_npz

    rng = np.random.default_rng(123)
    hgb_idx = [i for i, f in enumerate(ALL_FEATURES) if not f.startswith("micro_")]
    n_hgb = len(hgb_idx)
    X = rng.normal(size=(600, n_hgb))
    # Inject NaNs ONLY on col 2 so col 0 checks unseen-NaN handling in pack_lgb_for_npz
    X[:100, 2] = np.nan
    y = ((X[:, 0] + 0.5 * np.nan_to_num(X[:, 2])) > 0).astype(int)

    m_lgb = lgb.LGBMClassifier(n_estimators=25, num_leaves=7, random_state=42, verbose=-1).fit(X, y)
    m_hgb = HistGradientBoostingClassifier(max_iter=20, max_depth=3, random_state=42).fit(X, y)

    X_unseen_nan = X[:120].copy()
    X_unseen_nan[:40, 0] = np.nan

    packed_lgb = pack_lgb_for_npz(m_lgb, "s5_lgb")
    p_sk = m_lgb.predict_proba(X_unseen_nan)[:, 1]
    p_np = eval_hgb_flat_batch(packed_lgb, "s5_lgb", X_unseen_nan)
    assert float(np.max(np.abs(p_sk - p_np))) < 1e-8

    n_core = len(CORE_FEATURES)
    X_lr = np.nan_to_num(X[:, :n_core], 0.0)
    sc = StandardScaler().fit(X_lr)
    lr = LogisticRegression(random_state=42).fit(sc.transform(X_lr), y)

    bundle = {}
    bundle.update(pack_hgb_for_npz(m_hgb, "s5_dir"))
    bundle.update(packed_lgb)
    bundle.update(pack_hgb_for_npz(m_hgb, "s5_tie"))
    bundle["s5_lr_meds"] = np.zeros(n_core, dtype=np.float64)
    bundle["s5_lr_mean"] = sc.mean_.astype(np.float64)
    bundle["s5_lr_scale"] = sc.scale_.astype(np.float64)
    bundle["s5_lr_coef"] = lr.coef_[0].astype(np.float64)
    bundle["s5_lr_intercept"] = np.array([float(lr.intercept_[0])], dtype=np.float64)
    bundle["s5_iso_x"] = np.array([0.50, 0.70, 1.0], dtype=np.float64)
    bundle["s5_iso_y"] = np.array([0.50, 0.65, 0.65], dtype=np.float64)

    pred = SubminuteQuantPredictor.__new__(SubminuteQuantPredictor)
    pred.arrays = bundle
    pred.loaded_intervals = {"s5"}
    pred.gated_intervals = set()
    pred.meta = {
        "intervals": {
            "s5": {
                "gate_pass": True,
                "n_hgb_features": n_hgb,
                "hgb_feature_indices": hgb_idx,
                "n_core_features": n_core,
                "lr_feature_indices": list(range(n_core)),
                "w_hgb": 0.4,
                "w_lgb": 0.4,
                "w_lr": 0.2,
                "tie_dampen_threshold": 0.99,
            }
        }
    }

    c_up = _synthetic_candles(n=100, dt=5)
    c_dn = [{"openTime": c["openTime"], "open": 2.2 - c["open"], "high": 2.2 - c["low"], "low": 2.2 - c["high"], "close": 2.2 - c["close"], "volume": c["volume"]} for c in c_up]
    r_up = pred.predict("EURUSD_PO", "s5", c_up)
    r_dn = pred.predict("EURUSD_PO", "s5", c_dn)
    assert r_up is not None and r_dn is not None
    assert abs((1.0 - r_up[4]) - r_dn[4]) < 1e-4