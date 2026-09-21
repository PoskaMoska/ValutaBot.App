import pytest
from datetime import datetime, timezone
import pandas as pd
from model import parse_candle_timestamp_ms

def test_parse_candle_timestamp_integer():
    """Test parsing standard integer milliseconds (e.g., from Binance)."""
    raw_ms = 1700000000000
    res = parse_candle_timestamp_ms(raw_ms)
    assert res == 1700000000000.0

def test_parse_candle_timestamp_float():
    """Test parsing float milliseconds."""
    raw_ms = 1700000000000.0
    res = parse_candle_timestamp_ms(raw_ms)
    assert res == 1700000000000.0

def test_parse_candle_timestamp_iso_string():
    """Test parsing ISO8601 string (e.g., from TwelveData PostgreSQL)."""
    iso_str = "2026-09-21T21:41:20.0000000Z"
    res = parse_candle_timestamp_ms(iso_str)
    # 2026-09-21 21:41:20 UTC -> 1790026880.0 sec -> 1790026880000.0 ms
    dt = pd.to_datetime(iso_str, utc=True)
    expected_ms = dt.timestamp() * 1000.0
    assert res == expected_ms

def test_parse_candle_timestamp_basic_string():
    """Test parsing basic datetime string (e.g., YYYY-MM-DD HH:MM:SS)."""
    dt_str = "2026-09-21 21:02:00"
    res = parse_candle_timestamp_ms(dt_str)
    dt = pd.to_datetime(dt_str, utc=True)
    expected_ms = dt.timestamp() * 1000.0
    assert res == expected_ms

def test_parse_candle_timestamp_pandas_timestamp():
    """Test parsing pandas.Timestamp object (e.g., from raw read_sql_query)."""
    ts = pd.Timestamp("2026-09-21 21:02:00", tz="UTC")
    res = parse_candle_timestamp_ms(ts)
    expected_ms = ts.timestamp() * 1000.0
    assert res == expected_ms

def test_parse_candle_timestamp_invalid():
    """Test parsing invalid data returns 0 instead of crashing."""
    assert parse_candle_timestamp_ms(None) == 0.0
    assert parse_candle_timestamp_ms("not_a_date") == 0.0
    assert parse_candle_timestamp_ms({"bad": "object"}) == 0.0
