import os
import sys
import time
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
    MODEL_VERSION,
    SubminuteQuantPredictor,
    compute_features_batch,
    compute_features_latest,
    eval_hgb_flat_batch,
    eval_hgb_flat_single,
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
    sc = np.array([r["close"] for r in c_s5], dtype=np.float64)

    batch_mat, _ = compute_features_batch(ts, o, h, l, c, v, 60, s5_ts=st, s5_o=so, s5_c=sc)
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
    assert np.all(np.isnan(row[-7:]))


def test_loaded_production_bundle_all_5_timeframes_and_latency():
    if not (os.path.exists(DEFAULT_NPZ_PATH) and os.path.exists(DEFAULT_META_PATH)):
        pytest.skip("subminute_quant_v6 model artifacts not present yet")

    predictor = SubminuteQuantPredictor.get_instance()
    assert predictor.is_available()

    for iv_in, dt in [("s5", 5), ("s10", 10), ("s15", 15), ("s30", 30), ("m1", 60)]:
        assert predictor.is_available_for(iv_in), f"{iv_in} should be loaded and pass quality gate"
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
        if direction == "BUY":
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
        pytest.skip("subminute_quant_v6 model artifacts not present yet")

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
        pytest.skip("subminute_quant_v6 model artifacts not present yet")

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
