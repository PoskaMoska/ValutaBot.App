import pytest
import sys
import os

# Add ml_service to path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from model import ForexPredictor

def test_lightgbm_initialization():
    """
    Sanity Check for memory and model loading.
    Ensures that LightGBM is correctly installed and that the ForexPredictor
    can instantiate without SegFaulting or throwing missing dependency errors.
    """
    try:
        import lightgbm as lgb
    except ImportError:
        pytest.fail("LightGBM is not installed. The deployment container is missing required dependencies!")

    # Instantiate the strategist
    try:
        strategist = ForexPredictor("EURUSD", "1m")
        assert strategist._model is None or hasattr(strategist._model, 'predict')
    except Exception as e:
        pytest.fail(f"ForexPredictor failed to initialize during deploy: {str(e)}")
