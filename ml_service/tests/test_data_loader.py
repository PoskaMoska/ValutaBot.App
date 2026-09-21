import pytest
from data.data_loader import _interpolate_subminute

def test_brownian_bridge_s5_length():
    """Test that a 1-minute candle is split into exactly 12 5-second candles."""
    m1_candles = [{
        "openTime": 1700000000000,
        "open": 1.1000,
        "high": 1.1050,
        "low": 1.0950,
        "close": 1.1020,
        "volume": 120.0
    }]
    
    s5_candles = _interpolate_subminute(m1_candles, "s5")
    assert len(s5_candles) == 12, f"Expected 12 candles, got {len(s5_candles)}"

def test_brownian_bridge_s15_boundaries():
    """Test that interpolated subminute candles strictly respect the original OHLC boundaries."""
    m1_candles = [{
        "openTime": 1700000000000,
        "open": 1.1000,
        "high": 1.1050,
        "low": 1.0950,
        "close": 1.1020,
        "volume": 120.0
    }]
    
    s15_candles = _interpolate_subminute(m1_candles, "s15")
    assert len(s15_candles) == 4, f"Expected 4 candles, got {len(s15_candles)}"
    
    # First candle must open exactly where the 1m candle opened
    assert s15_candles[0]["open"] == 1.1000, "First open price mismatch"
    
    # Last candle must close exactly where the 1m candle closed
    assert abs(s15_candles[-1]["close"] - 1.1020) < 1e-9, "Final close price mismatch"
    
    # All synthetic candles must remain strictly within the 1m High/Low bounds
    for i, c in enumerate(s15_candles):
        assert c["high"] <= 1.1050, f"Candle {i} breached High boundary: {c['high']} > 1.1050"
        assert c["low"] >= 1.0950, f"Candle {i} breached Low boundary: {c['low']} < 1.0950"

def test_brownian_bridge_no_change_for_1m():
    """Test that passing >= 60s intervals returns the original candles unmodified."""
    m1_candles = [{"openTime": 1700000000000, "open": 1.1, "high": 1.2, "low": 1.0, "close": 1.15, "volume": 100}]
    res = _interpolate_subminute(m1_candles, "1m")
    assert len(res) == 1
    assert res[0] == m1_candles[0]
