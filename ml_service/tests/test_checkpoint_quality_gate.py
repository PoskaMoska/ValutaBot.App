import sys
from pathlib import Path

ml_service_dir = Path(__file__).resolve().parent.parent
if str(ml_service_dir) not in sys.path:
    sys.path.insert(0, str(ml_service_dir))

from models.patch_tst_predictor import (
    PatchTSTPredictor,
    _resolve_model_path,
    validate_checkpoint_quality,
)


def test_quality_gate_rejects_broken_v4_and_selects_v3():
    resolved = _resolve_model_path()
    assert resolved is not None
    assert resolved.name == "patch_tst_brain_v3.pt", f"Expected patch_tst_brain_v3.pt, got {resolved}"

    # Force a fresh load on singleton to confirm runtime _arch_tag matches v3
    pred = PatchTSTPredictor.get_instance()
    pred._load_model()
    assert pred.is_available()
    assert pred._arch_tag == "patch_tst_v3"


def test_validate_checkpoint_quality_rules():
    ok_bad_sample, _ = validate_checkpoint_quality({"val_acc": 55.0, "test_acc": 54.0, "val_stats": {"total": 385}})
    assert not ok_bad_sample

    ok_bad_acc, _ = validate_checkpoint_quality({"val_acc": 36.6, "test_acc": 34.0, "val_stats": {"total": 5000}})
    assert not ok_bad_acc

    ok_missing, _ = validate_checkpoint_quality({"model_state_dict": {}}, require_metrics=True)
    assert not ok_missing

    ok_good, reason = validate_checkpoint_quality(
        {"val_acc": 51.0, "test_acc": 50.2, "val_loss": 0.79, "val_stats": {"total": 15432}},
        require_metrics=True,
    )
    assert ok_good, reason


def test_calibrated_s5_onnx_sidecar_integrity():
    import hashlib
    import json
    import onnx

    models_dir = ml_service_dir / "models"
    onnx_path = models_dir / "dual_stream_brain_v5.onnx"
    json_path = models_dir / "dual_stream_brain_v5.json"
    assert onnx_path.exists()
    assert json_path.exists()

    onnx.checker.check_model(onnx.load(str(onnx_path)))
    meta = json.loads(json_path.read_text(encoding="utf-8"))
    assert meta["horizon_sec"] == 5
    assert meta["temperature_folded"] is True
    assert float(meta["temperature"]) >= 1.0
    assert meta["onnx_sha256"] == hashlib.sha256(onnx_path.read_bytes()).hexdigest()

