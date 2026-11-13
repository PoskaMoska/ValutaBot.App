import sys
from pathlib import Path

ML_DIR = Path(__file__).resolve().parents[1]
if str(ML_DIR) not in sys.path:
    sys.path.insert(0, str(ML_DIR))

from models.patch_tst_predictor import OnlineDriftDetector, PatchTSTPredictor


def _make_candles(start: float, step: float, noise: float = 0.00002, n: int = 80):
    candles = []
    px = start
    for i in range(n):
        o = px
        c = px + step + (noise if i % 2 else -noise)
        h = max(o, c) + abs(step) * 0.2 + 0.00003
        l = min(o, c) - abs(step) * 0.2 - 0.00003
        candles.append({"open": o, "high": h, "low": l, "close": c, "volume": 100.0})
        px = c
    return candles


def test_default_payout_none_does_not_suppress_trend_bounce_to_hold():
    predictor = PatchTSTPredictor.get_instance()
    predictor.drift_detector = OnlineDriftDetector()

    downtrend_with_bounce = _make_candles(1.1350, -0.00006, n=62) + [
        {"open": 1.13128, "high": 1.13148, "low": 1.13120, "close": 1.13142, "volume": 100.0},
        {"open": 1.13142, "high": 1.13158, "low": 1.13135, "close": 1.13152, "volume": 100.0},
    ]

    result = predictor.predict(
        "EURUSD",
        "m1",
        downtrend_with_bounce,
        macro_context={"ta_score": -0.70, "rsi": 35.0, "adx": 42.0},
        payout=None,
    )
    assert result is not None
    direction, prob, *_ = result
    assert direction in ("BUY", "PUT"), f"Expected directional signal on active trend, got HOLD ({result})"
    assert 0.50 <= prob <= 0.92


def test_sequential_predictions_do_not_self_trigger_drift_hold_collapse():
    predictor = PatchTSTPredictor.get_instance()
    predictor.drift_detector = OnlineDriftDetector()

    uptrend = _make_candles(1.1280, +0.00005, n=80)
    directions = []
    for i in range(15):
        shift = (i % 3) * 0.00001
        cs = [
            {
                "open": c["open"] + shift,
                "high": c["high"] + shift,
                "low": c["low"] + shift,
                "close": c["close"] + shift,
                "volume": c["volume"],
            }
            for c in uptrend
        ]
        res = predictor.predict(
            "EURUSD",
            "m1",
            cs,
            macro_context={"ta_score": 0.75, "rsi": 70.0, "adx": 42.0},
            payout=None,
        )
        assert res is not None
        directions.append(res[0])

    status = predictor.drift_detector.get_status()
    assert status["sample_count"] == 15
    # Even if telemetry marks high 3-class softmax entropy as drift on synthetic streams,
    # it must never feed back into suppressing directional predictions into HOLD.
    assert status["class_frequencies"]["HOLD"] == 0.0, f"Predictions collapsed into HOLD: {status}"
    assert all(d in ("BUY", "PUT") for d in directions), f"Unexpected HOLD collapse: {directions}"


def test_explicit_unsustainable_low_payout_still_enforces_ev_guard():
    predictor = PatchTSTPredictor.get_instance()
    predictor.drift_detector = OnlineDriftDetector()
    candles = _make_candles(1.1320, 0.0, noise=0.00004, n=64)

    res = predictor.predict("EURUSD", "m1", candles, payout=0.50)
    assert res is not None
    assert res[0] == "HOLD"
