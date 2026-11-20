import json
import os
import sys

import numpy as np
import pandas as pd

ML_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ML_DIR not in sys.path:
    sys.path.insert(0, ML_DIR)

from export_s5_archive import (  # noqa: E402
    _compute_incremental_cutoff,
    merge_candle_frames,
    normalize_candle_frame,
)
from train_subminute_quant import (  # noqa: E402
    merge_existing_artifacts,
    merge_interval_sources,
    resample_s5_grid,
)


def test_normalize_and_merge_candle_frames_prefers_fresh_db_rows():
    existing = pd.DataFrame([
        {
            "asset": "EURUSD_PO",
            "open_time": "2026-11-16T12:00:00Z",
            "open_price": 1.1000,
            "high_price": 1.1005,
            "low_price": 1.0998,
            "close_price": 1.1002,
            "volume": 5,
        },
        {
            "asset": "EURUSD_PO",
            "open_time": "2026-11-16T12:00:05Z",
            "open_price": 1.1002,
            "high_price": 1.1006,
            "low_price": 1.1001,
            "close_price": 1.1003,
            "volume": 4,
        },
    ])
    # Second bar updated in fresh DB with different ISO fractional formatting (.0000000Z)
    fresh = pd.DataFrame([
        {
            "asset": "EURUSD_PO",
            "open_time": "2026-11-16T12:00:05.0000000Z",
            "open": 1.1002,
            "high": 1.1009,
            "low": 1.1001,
            "close": 1.1008,
            "volume": 9,
        },
        {
            "asset": "EURUSD_PO",
            "open_time": "2026-11-16T12:00:10.0000000Z",
            "open": 1.1008,
            "high": 1.1010,
            "low": 1.1007,
            "close": 1.1009,
            "volume": 6,
        },
    ])

    merged = merge_candle_frames(existing, fresh)
    assert len(merged) == 3
    assert merged.iloc[0]["close_price"] == 1.1002
    assert merged.iloc[1]["close_price"] == 1.1008
    assert merged.iloc[1]["volume"] == 9.0
    assert merged.iloc[2]["close_price"] == 1.1009


def test_incremental_cutoff_uses_lexicographically_safe_prefix():
    df = normalize_candle_frame(pd.DataFrame([
        {
            "asset": "GBPUSD_PO",
            "open_time": "2026-11-20T15:30:05.0000000Z",
            "open_price": 1.30,
            "high_price": 1.31,
            "low_price": 1.29,
            "close_price": 1.305,
            "volume": 10,
        }
    ]))
    cutoff = _compute_incremental_cutoff(df, overlap_minutes=5)
    assert cutoff == "2026-11-20T15:25:05"
    assert "2026-11-20T15:25:05.0000000Z" >= cutoff
    assert "2026-11-20T15:25:05Z" >= cutoff


def test_resample_s5_grid_and_source_priority_merging():
    base_ts = 1_795_000_000  # divisible by 60 & 10
    rows = []
    for i in range(4):
        rows.append({
            "asset": "EURUSD_PO",
            "ts": base_ts + i * 5,
            "open_price": 1.1000 + i * 0.0001,
            "high_price": 1.1005 + i * 0.0001,
            "low_price": 1.0995 + i * 0.0001,
            "close_price": 1.1001 + i * 0.0001,
            "volume": 3.0,
        })
    s5_df = pd.DataFrame(rows)

    s10_p0 = resample_s5_grid(s5_df, dt=10, phase=0)
    assert len(s10_p0) == 2
    assert s10_p0.iloc[0]["open_price"] == 1.1000
    assert s10_p0.iloc[0]["close_price"] == 1.1002
    assert s10_p0.iloc[0]["volume"] == 6.0

    # Shifted phase=5 skips the first unpaired s5 bar and forms 1 middle s10 bar cleanly
    s10_p5 = resample_s5_grid(s5_df, dt=10, phase=5)
    assert len(s10_p5) == 1
    assert s10_p5.iloc[0]["open_price"] == 1.1001
    assert s10_p5.iloc[0]["close_price"] == 1.1003

    hc = pd.DataFrame([{
        "asset": "EURUSD_PO",
        "ts": base_ts,
        "open_price": 1.0999,
        "high_price": 1.1010,
        "low_price": 1.0990,
        "close_price": 1.1000,
        "volume": 20.0,
    }])
    db = pd.DataFrame([{
        "asset": "EURUSD_PO",
        "ts": base_ts,
        "open_price": 1.1000,
        "high_price": 1.1010,
        "low_price": 1.0995,
        "close_price": 1.1007,
        "volume": 25.0,
    }])
    merged = merge_interval_sources(hc, s10_p0, db)
    row_at_base = merged[merged["ts"] == base_ts].iloc[0]
    assert row_at_base["close_price"] == 1.1007


def test_merge_existing_artifacts_updates_only_requested_intervals(tmp_path):
    npz_path = str(tmp_path / "bundle.npz")
    meta_path = str(tmp_path / "bundle.json")

    np.savez_compressed(
        npz_path,
        s5_dir_weights=np.array([1.0, 2.0]),
        **{"1min_dir_weights": np.array([3.0]), "1min_stale_key": np.array([9.0])},
    )
    prev_meta = {
        "model_version": "neural_quant_v8",
        "all_features": ["f1", "f2"],
        "intervals": {
            "s5": {"gate_pass": True, "oof_auc": 0.58},
            "1min": {"gate_pass": False, "oof_auc": 0.51},
        },
    }
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(prev_meta, f)

    new_arrays = {"1min_dir_weights": np.array([4.0, 5.0])}
    new_meta = {
        "model_version": "neural_quant_v8",
        "all_features": ["f1", "f2"],
        "intervals": {"1min": {"gate_pass": True, "oof_auc": 0.53}},
    }

    merged_arrays, merged_meta = merge_existing_artifacts(
        npz_path, meta_path, new_arrays, new_meta, updated_intervals=["1min"]
    )
    assert "s5_dir_weights" in merged_arrays
    assert np.array_equal(merged_arrays["s5_dir_weights"], np.array([1.0, 2.0]))
    assert np.array_equal(merged_arrays["1min_dir_weights"], np.array([4.0, 5.0]))
    assert "1min_stale_key" not in merged_arrays
    assert list(merged_meta["intervals"].keys()) == ["s5", "1min"]
    assert merged_meta["intervals"]["s5"]["oof_auc"] == 0.58
    assert merged_meta["intervals"]["1min"]["gate_pass"] is True
