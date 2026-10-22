import pytest
import sys
import os

# Add ml_service to path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from models.patch_tst_predictor import PatchTSTPredictor

def test_neural_brain_initialization():
    """
    Sanity Check for Neural Brain initialization and PyTorch weights loading.
    Ensures that PatchTSTPredictor is loaded and available for live inference.
    """
    try:
        import torch
    except ImportError:
        pytest.fail("PyTorch is not installed. Container missing required ML dependencies!")

    predictor = PatchTSTPredictor.get_instance()
    assert predictor.is_available(), "PatchTSTPredictor must be loaded and available!"
    assert predictor.model is not None, "Neural model state dict failed to load!"
    assert predictor._candle_features in (4, 5)
    assert predictor._seq_len == 160
    assert predictor._seq_len_mtf == 30
