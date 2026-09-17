import pytest
from fastapi.testclient import TestClient
import os
import sys

# Add ml_service to path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import app

client = TestClient(app)
API_SECRET = os.getenv("INTERNAL_API_SECRET", "default_secret")
HEADERS = {"X-Internal-Secret": API_SECRET}

def test_predict_endpoint_candles():
    req = {
        "symbol": "EURUSD",
        "interval": "1m",
        "candles": {
            "openTime": [1000] * 60,
            "open": [1.0] * 60,
            "high": [1.1] * 60,
            "low": [0.9] * 60,
            "close": [1.05] * 60,
            "volume": [100] * 60
        },
        "is_forex": True
    }
    resp = client.post("/predict", json=req, headers=HEADERS)
    # The endpoint might return 200 or 500 depending on model loading, but should not crash with AttributeError
    assert resp.status_code != 500

def test_feedback_endpoint_types():
    req = {
        "asset": "EURUSD",
        "timeframe": "s5",
        "was_win": True,
        "direction": "BUY",
        "entry_price": 1.0950,
        "exit_price": 1.0955,
        "timestamp": "2026-09-17T12:00:00Z",
        "is_forex": True
    }
    # Send valid feedback
    resp = client.post("/feedback", json=req, headers=HEADERS)
    assert resp.status_code == 200

    # Test that passing a string for was_win fails validation (Pydantic will auto-convert "true", but let's pass something invalid)
    invalid_req = req.copy()
    invalid_req["was_win"] = "not_a_boolean"
    resp_invalid = client.post("/feedback", json=invalid_req, headers=HEADERS)
    assert resp_invalid.status_code == 422
