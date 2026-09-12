import pytest
from fastapi.testclient import TestClient
import numpy as np
import time

from main import app
from features import kalman_smooth

client = TestClient(app)

def generate_dummy_candles(n=100):
    candles = []
    base_time = int(time.time()) - (n * 60)
    for i in range(n):
        noise = np.random.normal(0, 0.0001)
        price = 1.1000 + (np.sin(i / 10.0) * 0.001) + noise
        candles.append({
            "openTime": base_time + (i * 60),
            "open": price - 0.0001,
            "high": price + 0.0002,
            "low": price - 0.0002,
            "close": price,
            "volume": 100.0 + np.random.uniform(0, 50)
        })
    return candles

def test_predict_endpoint_parity():
    candles = generate_dummy_candles(100)
    payload = {
        "symbol": "EURUSD",
        "interval": "1m",
        "is_forex": True,
        "candles": candles,
        "mtf_candles": None
    }
    response = client.post("/predict", json=payload)
    assert response.status_code == 200, f"Failed with {response.text}"
    data = response.json()
    assert "direction" in data
    assert data["direction"] in ["BUY", "PUT", "NEUTRAL"]
    assert "confidence" in data
    assert isinstance(data["confidence"], float)

def test_kalman_filter_invariants():
    prices = np.array([c["close"] for c in generate_dummy_candles(50)])
    smoothed = kalman_smooth(prices, Q=1e-3, R=1e-2, P0=1.0)
    assert len(smoothed) == len(prices)
    assert not np.isnan(smoothed).any()
    assert not np.isinf(smoothed).any()
    assert np.abs(np.mean(smoothed) - np.mean(prices)) < 0.01

if __name__ == '__main__':
    pytest.main(['-v', 'test_data_parity.py'])
