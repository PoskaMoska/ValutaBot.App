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
    # E2E Parity Test: C# sends candles as an array of anonymous objects.
    # Python consumes it as a list of dicts. We must generate exactly 60 objects.
    mock_candles = []
    for i in range(60):
        mock_candles.append({
            "openTime": 1700000000 + (i * 60),
            "open": 1.0,
            "high": 1.1,
            "low": 0.9,
            "close": 1.05 + (i * 0.001), # Create tiny synthetic trend
            "volume": 100
        })

    req = {
        "symbol": "EURUSD",
        "interval": "1m",
        "candles": mock_candles,
        "is_forex": True,
        "smc_bos_dir": "BUY",
        "smc_has_ob": True,
        "smc_has_fvg": False,
        "of_delta_ratio": 1.2,
        "of_state": "BULLISH"
    }
    resp = client.post("/predict", json=req, headers=HEADERS)
    
    # We must explicitly demand 200 OK. If it's 422 (validation), it must fail the deploy.
    assert resp.status_code == 200, f"Expected 200, got {resp.status_code}. Response: {resp.text}"
    
    # Assert output structure is correct for C# to deserialize
    data = resp.json()
    assert "direction" in data
    assert "confidence" in data
    assert data["direction"] in ["BUY", "PUT", "NEUTRAL"]

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

    # Test that passing a string for was_win fails validation
    invalid_req = req.copy()
    invalid_req["was_win"] = "not_a_boolean"
    resp_invalid = client.post("/feedback", json=invalid_req, headers=HEADERS)
    assert resp_invalid.status_code == 422
