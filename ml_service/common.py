"""
common.py - Shared utilities and definitions for ML microservice.
"""
from typing import Any
import pandas as pd

TF_MAP = {
    "s5": "s5",
    "s10": "s10",
    "s15": "s15",
    "s30": "s30",
    "1m": "1m",
    "m1": "1m",
    "5m": "5m",
    "m5": "5m",
    "15m": "15m",
    "m15": "15m",
    "1h": "1h",
    "h1": "1h",
}

def is_forex_symbol(symbol: str) -> bool:
    """Checks if a symbol is a forex pair rather than crypto."""
    s = (
        symbol.upper()
        .replace("_OTC", "")
        .replace(" OTC", "")
        .replace("_PO", "")
        .replace("/", "")
        .replace("-", "")
        .replace("_", "")
        .strip()
    )
    crypto_keys = ["BTC", "ETH", "SOL", "DOGE", "XRP", "LTC", "BNB"]
    return not any(k in s for k in crypto_keys)

def normalize_interval(interval: str) -> str:
    """Canonical interval string (s5, s10, 1m, 5m, etc.)."""
    iv = str(interval).lower().strip()
    if iv.startswith("s") and iv[1:].isdigit():
        return iv
    return TF_MAP.get(iv, "1m")

def parse_candle_timestamp_ms(val: Any) -> float:
    """Parses various timestamp formats (int, float, iso string, pd.Timestamp) into epoch ms."""
    if val is None:
        return 0.0
    if isinstance(val, (int, float)):
        return float(val)
    if isinstance(val, pd.Timestamp):
        return float(val.timestamp() * 1000.0)
    if isinstance(val, str):
        try:
            dt = pd.to_datetime(val, utc=True)
            return float(dt.timestamp() * 1000.0)
        except Exception:
            return 0.0
    return 0.0
