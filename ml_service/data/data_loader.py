import os
import sqlite3
import pandas as pd
from typing import List, Dict
import logging

log = logging.getLogger("DataLoader")

TICKS_DB_PATH = os.path.join(os.path.dirname(os.path.dirname(__file__)), "data", "ValutaTicks.db")

def _interpolate_subminute(m1_candles: List[Dict], interval: str) -> List[Dict]:
    """Interpolate 1-minute candles into sub-minute steps (s5, s10, s15, s30).
       Uses a Brownian Bridge to generate stochastic micro-paths that respect OHLC boundaries
       without injecting artificial deterministic patterns (like sine waves)."""
    sec = int(interval[1:]) if (interval.startswith("s") and len(interval) > 1) else 60
    if sec >= 60:
        return m1_candles
        
    sub_per_min = 60 // sec
    interpolated = []
    
    import math
    import random
    
    for m in m1_candles:
        start_price = m["open"]
        end_price = m["close"]
        high_limit = m["high"]
        low_limit = m["low"]
        vol_step = m["volume"] / sub_per_min
        
        # Generate standard Brownian motion
        dW = [random.gauss(0, 1) for _ in range(sub_per_min)]
        W = [0.0]
        for dw in dW:
            W.append(W[-1] + dw)
            
        # Bridge it so it ends exactly at 0 variance from the target
        W = W[1:]
        T = sub_per_min
        bridge = [W[i] - ((i + 1) / T) * W[-1] for i in range(T)]
        
        # Scale bridge to fit within the candle's High-Low range safely
        max_b = max(bridge) if bridge else 0
        min_b = min(bridge) if bridge else 0
        range_b = max_b - min_b + 1e-10
        
        candle_range = high_limit - low_limit
        scale = (candle_range * 0.5) / range_b # Scale to 50% of the true range to avoid boundary breaks
        
        for i in range(sub_per_min):
            frac_end = (i + 1) / sub_per_min
            
            # Linear drift + stochastic bridge
            c = start_price + (end_price - start_price) * frac_end + (bridge[i] * scale)
            
            # Clamp to limits
            c = max(min(c, high_limit), low_limit)
            
            if i == 0:
                o = start_price
            else:
                o = interpolated[-1]["close"]
                
            h = max(o, c) + (candle_range * 0.1 * random.random())
            l = min(o, c) - (candle_range * 0.1 * random.random())
            
            h = min(h, high_limit)
            l = max(l, low_limit)
            
            interpolated.append({
                "open": o,
                "high": h,
                "low": l,
                "close": c,
                "volume": vol_step
            })
            
    return interpolated


def _fetch_local_sqlite(symbol: str, interval: str, limit: int) -> List[Dict]:
    # Try to fetch from PostgreSQL SubminuteCandles
    try:
        db_url = os.getenv("DATABASE_URL")
        if db_url:
            import psycopg2
            conn = psycopg2.connect(db_url)
            query = '''
                SELECT open_time as "openTime", open_price as "open", high_price as "high", low_price as "low", close_price as "close", volume as "volume"
                FROM subminute_candles 
                WHERE asset = %s AND interval = %s 
                ORDER BY open_time DESC 
                LIMIT %s
            '''
            df = pd.read_sql_query(query, conn, params=(symbol, interval, limit))
            conn.close()
            if not df.empty:
                return df.iloc[::-1].to_dict(orient='records')
    except Exception as e:
        print(f"  [WARN] PostgreSQL subminute fetch failed: {e}")

    # Fallback to SQLite
    db_path = TICKS_DB_PATH
    if not os.path.exists(db_path):
        return []
    try:
        conn = sqlite3.connect(db_path, timeout=30.0)
        query = '''
            SELECT OpenTime as openTime, Open as open, High as high, Low as low, Close as close, Volume as volume 
            FROM SubminuteCandles 
            WHERE Asset = ? AND Interval = ? 
            ORDER BY OpenTime DESC 
            LIMIT ?
        '''
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            df = pd.read_sql_query(query, conn, params=(symbol, interval, limit))
        conn.close()
        
        # DataFrame is fetched descending, we reverse it to ascending time order
        return df.iloc[::-1].to_dict(orient='records')
    except Exception as e:
        log.error(f"SQLite Fetch Error: {e}")
        return []



def _query_historical_candles_db(symbol: str, norm_interval: str, limit: int) -> pd.DataFrame:
    db_url = os.getenv("DATABASE_URL")
    if db_url:
        try:
            import psycopg2
            conn = psycopg2.connect(db_url)
            query = """
                SELECT open_time as "openTime", open as "open", high as "high",
                       low as "low", close as "close", volume as "volume"
                FROM historical_candles
                WHERE asset = %s AND interval = %s
                ORDER BY open_time DESC
                LIMIT %s
            """
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter('ignore', UserWarning)
                df = pd.read_sql_query(query, conn, params=(symbol, norm_interval, limit))
            conn.close()
            if not df.empty:
                return df
        except Exception as e:
            log.warning(f"[HistoricalCandles] PostgreSQL fetch failed: {e}")

    db_path = TICKS_DB_PATH
    if os.path.exists(db_path):
        try:
            conn = sqlite3.connect(db_path, timeout=30.0)
            cursor = conn.cursor()
            cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='HistoricalCandles'")
            if cursor.fetchone():
                query = """
                    SELECT OpenTime as openTime, Open as open, High as high, Low as low, Close as close, Volume as volume
                    FROM HistoricalCandles WHERE Asset = ? AND Interval = ? ORDER BY OpenTime DESC LIMIT ?
                """
                df = pd.read_sql_query(query, conn, params=(symbol, norm_interval, limit))
                conn.close()
                if not df.empty:
                    return df
            else:
                conn.close()
        except Exception as e:
            log.error(f"[HistoricalCandles] SQLite fetch error: {e}")
    return pd.DataFrame()


def _fetch_historical_candles(symbol: str, interval: str, limit: int) -> List[Dict]:
    """
    Fetch large historical dataset for LightGBM Global Strategist training.
    Priority 1: Direct fetch from DB.
    Priority 2: If not found and interval > 1m, fetch 1m and resample in Pandas.
    """
    interval_aliases = {"m1": "1m", "m5": "5m", "m15": "15m", "m30": "30m", "h1": "1h", "h4": "4h"}
    norm_interval = interval_aliases.get(interval.lower(), interval.lower())

    df = _query_historical_candles_db(symbol, norm_interval, limit)
    
    # If we have enough data directly, return it
    if not df.empty and len(df) >= min(limit, 3000) * 0.1:
        log.info(f"[HistoricalCandles] Loaded {len(df)} rows from DB for {symbol} {norm_interval}")
        return df.iloc[::-1].to_dict(orient='records')
        
    # If not enough data and it's a higher timeframe, try to resample from 1m
    if norm_interval not in ("1m", "s3", "s5", "s10", "s15", "s30"):
        multiplier = 1
        if norm_interval.endswith("m"): multiplier = int(norm_interval[:-1])
        elif norm_interval.endswith("h"): multiplier = int(norm_interval[:-1]) * 60
        elif norm_interval.endswith("d"): multiplier = int(norm_interval[:-1]) * 1440
        
        if multiplier > 1:
            req_limit = limit * multiplier
            log.info(f"[HistoricalCandles] Not enough {norm_interval} for {symbol}. Fetching {req_limit} 1m candles for resampling...")
            df_1m = _query_historical_candles_db(symbol, "1m", req_limit)
            
            if not df_1m.empty:
                # DB returns DESC order, we need ASC for accurate resampling
                df_1m = df_1m.iloc[::-1].copy()
                
                try:
                    df_1m['openTime'] = pd.to_datetime(df_1m['openTime'], utc=True, format='mixed')
                    df_1m.set_index('openTime', inplace=True)
                    
                    rule = norm_interval
                    if rule.endswith('m'): rule = rule[:-1] + 'min'
                    if rule.endswith('d'): rule = rule[:-1] + 'D'
                    
                    df_resampled = df_1m.resample(rule, label='left', closed='left').agg({
                        'open': 'first',
                        'high': 'max',
                        'low': 'min',
                        'close': 'last',
                        'volume': 'sum'
                    }).dropna()
                    
                    df_resampled.reset_index(inplace=True)
                    df_resampled['openTime'] = df_resampled['openTime'].apply(lambda x: x.isoformat().replace("+00:00", "Z"))
                    
                    if not df_resampled.empty:
                        log.info(f"[HistoricalCandles] Synthesized {len(df_resampled)} {norm_interval} candles from {len(df_1m)} 1m candles for {symbol}")
                        return df_resampled.to_dict(orient='records')
                except Exception as e:
                    log.error(f"[HistoricalCandles] Failed to resample 1m to {norm_interval} for {symbol}: {e}")

    # Fallback: just return what we got initially
    if not df.empty:
        log.info(f"[HistoricalCandles] Loaded {len(df)} rows from DB for {symbol} {norm_interval} (partial)")
        return df.iloc[::-1].to_dict(orient='records')
        
    return []



def _fetch_rl_feedback(symbol: str, interval: str) -> List[Dict]:
    db_path = TICKS_DB_PATH
    if not os.path.exists(db_path):
        return []
    try:
        conn = sqlite3.connect(db_path, timeout=30.0)
        
        # Check if table exists
        cursor = conn.cursor()
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='OnlineFeedback'")
        if not cursor.fetchone():
            conn.close()
            return []
            
        # Bug1 fix: fetch Timestamp for time-based matching instead of EntryPrice
        query = "SELECT Timestamp as ts, Direction as dir, WasWin as win FROM OnlineFeedback WHERE Asset=? AND Interval=?"
        df = pd.read_sql_query(query, conn, params=(symbol, interval))
        conn.close()
        
        return df.to_dict(orient='records')
    except Exception as e:
        log.error(f"SQLite RL Fetch Error: {e}")
        return []

# в”Ђв”Ђ Timeframe в†’ Binance interval string в”Ђв”Ђ
TF_MAP = {
    "s3": "1m", "s5": "1m", "s10": "1m", "s15": "1m", "s30": "1m",
    "m1": "1m", "m2": "1m", "m3": "3m", "m5": "5m", "m10": "5m",
    "m15": "15m", "m30": "30m", "h1": "1h", "h4": "4h",
    "1m": "1m", "2m": "1m", "3m": "3m", "5m": "5m", "15m": "15m", "30m": "30m", "1h": "1h", "4h": "4h",
}

LGBM_PARAMS_STANDARD = {
    "objective": "binary",
    "metric": "auc",
    "n_estimators": 500,
    "learning_rate": 0.02,
    "max_depth": 6,
    "num_leaves": 31,
    "min_child_samples": 30,
    "feature_fraction": 0.7,
    "bagging_fraction": 0.7,
    "bagging_freq": 5,
    "lambda_l1": 0.5,
    "lambda_l2": 1.0,
    "is_unbalance": True,
    "min_split_gain": 0.01,
    "verbose": -1,
}

LGBM_PARAMS_SUBMINUTE = {
    "objective": "binary",
    "metric": "auc",
    "n_estimators": 300,
    "learning_rate": 0.05,
    "max_depth": 4,
    "num_leaves": 15,
    "min_child_samples": 50,
    "feature_fraction": 0.6,
    "bagging_fraction": 0.6,
    "bagging_freq": 3,
    "lambda_l1": 1.0,
    "lambda_l2": 2.0,
    "is_unbalance": True,
    "min_split_gain": 0.01,
    "verbose": -1,
}
