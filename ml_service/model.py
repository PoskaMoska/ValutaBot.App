"""
Two-Tier Forex Predictor.
  Tier 1 (Global Strategist):  LightGBM вЂ” retrained every 24h on up to 100k candles.
  Tier 2 (Local Tactician):    SGDClassifier вЂ” updated via partial_fit after every trade (<1ms).
Final signal = 0.70 * LightGBM_prob + 0.30 * SGD_prob.
"""

from __future__ import annotations

import os
import time
import logging
import threading
import numpy as np
import pandas as pd
import requests
import joblib
import sqlite3

from pathlib import Path
from typing import Optional, Tuple, List, Dict
from datetime import datetime, timezone

try:
    import lightgbm as lgb
    from sklearn.linear_model import SGDClassifier
    from sklearn.model_selection import TimeSeriesSplit
    from sklearn.metrics import accuracy_score, roc_auc_score
    HAS_LGBM = True
except ImportError:
    HAS_LGBM = False

from features import build_features

log = logging.getLogger("predictor")

# FIX W-27: previously model.py used "data/models/ValutaTicks.db" and main.py used
# "data/ValutaTicks.db" вЂ” ticks were written to one path and read from another,
# so _fetch_candles_at_entry / _fetch_local_sqlite in model.py always returned empty results.
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
TICKS_DB_PATH = os.path.join(_BASE_DIR, "data", "ValutaTicks.db")

MODEL_DIR = Path(os.getenv("MODEL_DIR", str(Path(__file__).parent / "data" / "models")))
SGD_MODEL_DIR = MODEL_DIR / "sgd"
RETRAIN_INTERVAL_H = int(os.getenv("RETRAIN_INTERVAL_H", "168"))  # Weekly global retrain only
MAX_HISTORICAL_CANDLES = int(os.getenv("MAX_HISTORICAL_CANDLES", "100000"))  # Global Strategist window
# Bug2 fix: configurable target horizon (default=5 candles, aligned with typical TradeTimeout 15*0.6в‰€9 в†’ 5вЂ“10)
TARGET_HORIZON_CANDLES = int(os.getenv("TARGET_HORIZON_CANDLES", "5"))
MIN_CONFIDENCE = 0.50  # below в†’ NEUTRAL
BINANCE_BASE = "https://api.binance.com"

# в”Ђв”Ђ TwelveData Config в”Ђв”Ђ
TWELVE_DATA_BASE = "https://api.twelvedata.com"
TWELVE_DATA_API_KEY = os.getenv("TwelveDataApiKey") or os.getenv("TWELVE_DATA_API_KEY")

TD_INTERVAL_MAP = {
    "1m": "1min", "2m": "2min", "3m": "5min", "5m": "5min",
    "10m": "10min", "15m": "15min", "30m": "30min", "45m": "45min",
    "1h": "1h", "2h": "2h", "4h": "4h", "1d": "1day"
}

def is_forex_symbol(symbol: str) -> bool:
    # FIX W-23: "EURUSD_OTC" has length 10 в†’ old check (len==6) returned False в†’
    # OTC pairs were treated as crypto and always returned NEUTRAL prediction.
    sym = symbol.upper().replace("_OTC", "")  # strip OTC suffix before length check
    if sym in ["GOLD", "SILVER", "BRENT", "OIL", "XAUUSD", "XAGUSD"]:
        return True
    # Most Forex assets are 6 letters (EURUSD, USDJPY) and do not end with USDT
    if len(sym) == 6 and not sym.endswith("USDT"):
        return True
    return False

def to_twelvedata_symbol(symbol: str) -> str:
    sym = symbol.upper().replace("_OTC", "").replace("OTC", "").strip()
    if sym.endswith("USDT"):
        sym = sym.replace("USDT", "USD")
        
    if sym in ["GOLD", "XAUUSD"]:
        return "XAU/USD"
    if sym in ["SILVER", "XAGUSD"]:
        return "XAG/USD"
    # EURUSD -> EUR/USD
    if len(sym) == 6:
        return f"{sym[:3]}/{sym[3:]}"
    return sym

def _interpolate_subminute(m1_candles: List[Dict], interval: str) -> List[Dict]:
    """Interpolate 1-minute candles into sub-minute steps (s5, s10, s15, s30).
       Uses a deterministic zig-zag interpolation to prevent injecting white noise."""
    sec = int(interval[1:]) if (interval.startswith("s") and len(interval) > 1) else 60
    if sec >= 60:
        return m1_candles
        
    sub_per_min = 60 // sec
    interpolated = []
    
    import math
    
    for m in m1_candles:
        start_price = m["open"]
        end_price = m["close"]
        price_range = end_price - start_price
        high_limit = m["high"]
        low_limit = m["low"]
        vol_step = (high_limit - low_limit) / sub_per_min
        
        for i in range(sub_per_min):
            frac_start = i / sub_per_min
            frac_end = (i + 1) / sub_per_min
            
            o = start_price + price_range * frac_start
            c = start_price + price_range * frac_end
            
            # Deterministic micro-wicks (alternating sine wave pattern instead of random noise)
            micro_wick = vol_step * 0.25 * math.sin(i * math.pi / 2.0)
            
            h = max(o, c) + abs(micro_wick)
            l = min(o, c) - abs(micro_wick)
            
            h = min(h, high_limit)
            l = max(l, low_limit)
            
            interpolated.append({
                "open": o,
                "high": h,
                "low": l,
                "close": c,
                "volume": m["volume"] / sub_per_min
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
        df = pd.read_sql_query(query, conn, params=(symbol, interval, limit))
        conn.close()
        
        # DataFrame is fetched descending, we reverse it to ascending time order
        return df.iloc[::-1].to_dict(orient='records')
    except Exception as e:
        log.error(f"SQLite Fetch Error: {e}")
        return []


def _fetch_historical_candles(symbol: str, interval: str, limit: int) -> List[Dict]:
    """
    Fetch large historical dataset for LightGBM Global Strategist training.
    Priority 1: PostgreSQL historical_candles table (Railway cloud).
    Priority 2: Local SQLite HistoricalCandles table (local dev fallback).
    """
    interval_aliases = {"m1": "1m", "m5": "5m", "m15": "15m", "m30": "30m", "h1": "1h", "h4": "4h"}
    norm_interval = interval_aliases.get(interval.lower(), interval.lower())

    # Priority 1: PostgreSQL (Railway)
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
            df = pd.read_sql_query(query, conn, params=(symbol, norm_interval, limit))
            conn.close()
            if not df.empty:
                log.info(f"[HistoricalCandles] Loaded {len(df)} rows from PostgreSQL for {symbol} {norm_interval}")
                return df.iloc[::-1].to_dict(orient='records')
        except Exception as e:
            log.warning(f"[HistoricalCandles] PostgreSQL fetch failed: {e}")

    # Priority 2: SQLite fallback (local dev)
    db_path = TICKS_DB_PATH
    if not os.path.exists(db_path):
        return []
    try:
        conn = sqlite3.connect(db_path, timeout=30.0)
        cursor = conn.cursor()
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='HistoricalCandles'")
        if not cursor.fetchone():
            conn.close()
            return []
        query = """
            SELECT OpenTime as openTime, Open as open, High as high, Low as low, Close as close, Volume as volume
            FROM HistoricalCandles WHERE Asset = ? AND Interval = ? ORDER BY OpenTime DESC LIMIT ?
        """
        df = pd.read_sql_query(query, conn, params=(symbol, norm_interval, limit))
        conn.close()
        if df.empty:
            return []
        log.info(f"[HistoricalCandles] Loaded {len(df)} rows from SQLite for {symbol} {norm_interval}")
        return df.iloc[::-1].to_dict(orient='records')
    except Exception as e:
        log.error(f"[HistoricalCandles] SQLite fetch error: {e}")
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

LGBM_PARAMS = {
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
    # FIX: Prevent directional bias from class imbalance.
    # When training data covers a trending period (e.g. 70% of candles go DOWN),
    # without this the model learns to always predict PUT and gets high accuracy
    # without any real pattern recognition. is_unbalance forces it to learn
    # patterns from BOTH directions equally.
    "is_unbalance": True,
    "min_split_gain": 0.01,
    "verbose": -1,
}



class ModelMeta:
    """Metadata stored alongside each model file."""
    def __init__(self, accuracy: float, auc: float, n_train: int,
                 trained_at: float, version: str):
        self.accuracy = accuracy
        self.auc = auc
        self.n_train = n_train
        self.trained_at = trained_at
        self.version = version


class ForexPredictor:
    """
    One predictor per (symbol, interval).  All instances are held in the
    global registry `_predictors` in main.py.
    """

    def __init__(self, symbol: str, interval: str):
        self.symbol = symbol.upper()
        self.interval = interval.lower()
        self._key = f"{self.symbol}_{self.interval}"
        # Tier 1: Global Strategist
        self._model: Optional[lgb.LGBMClassifier] = None
        self._meta: Optional[ModelMeta] = None
        self._lock = threading.Lock()
        self.is_training = False
        # Tier 2: Local Tactician
        self._online_model: Optional[SGDClassifier] = None
        self._online_lock = threading.Lock()
        self._online_classes = np.array([0, 1])
        self._sgd_update_count: int = 0  # Bug3 fix: track updates for dynamic weight
        MODEL_DIR.mkdir(parents=True, exist_ok=True)
        SGD_MODEL_DIR.mkdir(parents=True, exist_ok=True)


    # в”Ђв”Ђ Public API в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    def predict(self, candles: List[Dict]) -> Tuple[str, float, str]:
        """
        Predict next candle direction from supplied candle list.
        Returns (direction, confidence, model_version).
        direction: "BUY" | "PUT" | "NEUTRAL"
        confidence: 0.0 вЂ“ 1.0
        """
        if not HAS_LGBM:
            return "NEUTRAL", 0.5, "no-lgbm"

        with self._lock:
            model = self._model
            meta = self._meta

        if model is None:
            # Try loading from disk
            self._try_load()
            with self._lock:
                model = self._model
                meta = self._meta

        if model is None:
            return "NEUTRAL", 0.5, "not-trained"

        try:
            feats = build_features(candles)
            if feats.empty or len(feats) < 5:
                return "NEUTRAL", 0.5, meta.version if meta else "no-feats"

            # Use last row as the current candle state
            X_last = feats.iloc[[-1]]
            X_arr = X_last.values.astype(np.float32)

            # Tier 1: LightGBM (Global Strategist)
            prob_lgbm = float(model.predict_proba(X_arr)[0, 1])

            # Tier 2: SGD (Local Tactician) вЂ” blend if available
            # Bug3 fix: dynamic weight 0%в†’30% based on real trade count (prevents noise at low sample count)
            with self._online_lock:
                online_model = self._online_model
                sgd_count = self._sgd_update_count
            if online_model is not None:
                try:
                    prob_sgd = float(online_model.predict_proba(X_arr)[0, 1])
                    sgd_weight = min(0.05, sgd_count / 200.0)  # max 5%, grows very slowly
                    lgbm_weight = 1.0 - sgd_weight
                    prob = lgbm_weight * prob_lgbm + sgd_weight * prob_sgd
                    log.debug(f"[Predict] {self._key} lgbm={prob_lgbm:.3f} sgd={prob_sgd:.3f} sgd_w={sgd_weight:.2f} blended={prob:.3f}")
                except Exception:
                    prob = prob_lgbm
            else:
                prob = prob_lgbm

            version = meta.version if meta else self._key

            if prob >= MIN_CONFIDENCE:
                return "BUY", prob, version
            elif prob <= (1.0 - MIN_CONFIDENCE):
                return "PUT", 1.0 - prob, version
            else:
                confidence = abs(prob - 0.5) * 2
                return "NEUTRAL", 0.5 + confidence * 0.15, version

        except Exception as e:
            log.error(f"[Predict] {self._key}: {e}")
            return "NEUTRAL", 0.5, "error"

    def partial_fit_online(self, candles: List[Dict], was_win: bool, direction: str) -> bool:
        """
        Tier 2 (Local Tactician): Update SGDClassifier with a single real trade outcome.
        Called immediately after a trade closes. Executes in <1ms.
        Returns True if update succeeded.
        """
        if not HAS_LGBM:
            return False
        try:
            feats = build_features(candles)
            if feats.empty or len(feats) < 5:
                return False

            X_last = feats.iloc[[-1]].values.astype(np.float32)

            # Derive label from real outcome
            # WIN + BUY  в†’ price went up   в†’ label 1
            # WIN + PUT  в†’ price went down  в†’ label 0
            # LOSS + BUY в†’ price went down  в†’ label 0
            # LOSS + PUT в†’ price went up    в†’ label 1
            if direction.upper() == "BUY":
                y = np.array([1 if was_win else 0])
            else:
                y = np.array([0 if was_win else 1])

            with self._online_lock:
                if self._online_model is None:
                    self._online_model = SGDClassifier(
                        loss="log_loss",
                        learning_rate="optimal",
                        alpha=0.01,
                        random_state=42,
                        warm_start=True,
                    )
                self._online_model.partial_fit(X_last, y, classes=self._online_classes)
                self._sgd_update_count += 1  # Bug3 fix: track update count
                online_model = self._online_model
                sgd_count = self._sgd_update_count

                # FIX Race Condition: СЃРѕС…СЂР°РЅРµРЅРёРµ Р’РќРЈРўР Р Р»РѕРєР° С‡РµСЂРµР· Р°С‚РѕРјР°СЂРЅСѓСЋ Р·Р°РїРёСЃСЊ.
                # Р Р°РЅСЊС€Рµ joblib.dump Р±С‹Р» РІРЅРµ with-Р±Р»РѕРєР° в†’ РїР°СЂР°Р»Р»РµР»СЊРЅС‹Рµ /feedback
                # РјРѕРіР»Рё РѕРґРЅРѕРІСЂРµРјРµРЅРЅРѕ РїРёСЃР°С‚СЊ РІ РѕРґРёРЅ .pkl в†’ Corrupted Pickle.
                # РџР°С‚С‚РµСЂРЅ: СЃРЅР°С‡Р°Р»Р° РІРѕ РІСЂРµРјРµРЅРЅС‹Р№ С„Р°Р№Р», Р·Р°С‚РµРј os.replace (Р°С‚РѕРјР°СЂРЅРѕ).
                sgd_path = SGD_MODEL_DIR / f"{self._key}_sgd.pkl"
                SGD_MODEL_DIR.mkdir(parents=True, exist_ok=True)
                tmp_path = sgd_path.with_suffix(".tmp")
                joblib.dump({"model": online_model, "count": sgd_count}, tmp_path)
                os.replace(tmp_path, sgd_path)

            log.info(f"[SGD] partial_fit done for {self._key} | dir={direction} win={was_win} | label={y[0]} | total_updates={sgd_count}")
            return True

        except Exception as e:
            log.error(f"[SGD] partial_fit error for {self._key}: {e}")
            return False



    def train(self, candles: Optional[List[Dict]] = None) -> Dict:
        """
        Train model. If candles not provided, fetch from Binance.
        Returns training report dict.
        """
        if not HAS_LGBM:
            return {"error": "lightgbm not installed"}

        with self._lock:
            if self.is_training:
                log.warning(f"[Train] Training already in progress for {self._key}. Skipping duplicate request.")
                return {"error": "Training already in progress"}
            self.is_training = True

        log.info(f"[Train] Starting training for {self._key}")
        try:
            if candles is None:
                # Calculate adaptive limit: max(100k / interval_minutes, 20k)
                interval_minutes = 1
                if self.interval == "5m": interval_minutes = 5
                elif self.interval == "15m": interval_minutes = 15
                target_candles = max(MAX_HISTORICAL_CANDLES // interval_minutes, 20000)

                # Priority 1: Large historical dataset from data_crawler (Global Strategist)
                candles = _fetch_historical_candles(self.symbol, self.interval, target_candles)
                if len(candles) >= 1500:
                    log.info(f"[Train] Loaded {len(candles)} candles from HistoricalCandles (Adaptive: {target_candles})")
                else:
                    # Priority 2: Subminute SQLite ticks (real recorded ticks from live trading)
                    if self.interval.startswith("s"):
                        candles = _fetch_local_sqlite(self.symbol, self.interval, 1500)
                        if len(candles) < 150:
                            # FIXED: Never use synthetic interpolation вЂ” it produces fake ~78% accuracy
                            # by learning the sine-wave generator pattern instead of real market dynamics.
                            # Instead, train on 5000 REAL 1-minute candles as a proxy.
                            # A model trained on genuine price action is FAR more honest (expect ~52-56% accuracy)
                            # and will generalize to real sub-minute patterns much better.
                            log.warning(
                                f"[Train] Not enough real ticks for {self._key} (found {len(candles)}). "
                                f"Using real 1m candles as proxy (no synthetic interpolation)."
                            )
                            candles = _fetch_historical_candles(self.symbol, "1m", 5000)
                            if len(candles) < 150 and is_forex_symbol(self.symbol):
                                candles = self._fetch_twelvedata(5000)  # Real 1m data API fallback
                            if len(candles) > 0:
                                log.info(f"[Train] Proxy-1m training for {self._key} on {len(candles)} real candles.")
                            else:
                                log.error(f"[Train] Could not fetch real 1m data for {self._key}. Skipping.")
                    else:
                        # Priority 3: TwelveData API (forex only)
                        if is_forex_symbol(self.symbol):
                            limit = 5000
                            candles = self._fetch_twelvedata(limit)
                            log.info(f"[Train] API fallback: fetched {len(candles)} candles for {self._key}")
                        else:
                            log.error(f"[Train] No local data for {self._key} and OTC cannot use TwelveData fallback.")
                            candles = []


            if len(candles) < 150:
                return {"error": f"Not enough candles: {len(candles)} < 150"}

            feats = build_features(candles)
            if feats.empty or len(feats) < 100:
                return {"error": "Feature engineering yielded too few rows"}

            # Bug2 fix: configurable target horizon aligned with TradeTimeout (was 3, now TARGET_HORIZON_CANDLES=5)
            H = TARGET_HORIZON_CANDLES
            closes = np.array([cl["close"] for cl in candles])
            target_raw = np.zeros(len(closes), dtype=int)
            target_raw[:-H] = (closes[H:] > closes[:-H]).astype(int)

            # Align features (feature matrix is shorter due to rolling-window NaN drop)
            feat_indices = feats.index.values
            valid_mask = feat_indices < (len(closes) - H)  # drop last H rows вЂ” no valid future target
            feat_indices_valid = feat_indices[valid_mask]
            feats = feats.loc[feat_indices_valid]
            target_aligned = target_raw[feat_indices_valid]

            X = feats.values.astype(np.float32)
            y = target_aligned.copy()  # copy so RL can modify labels safely

            # Log class balance to help diagnose trend bias in Railway logs
            if len(y) > 0:
                buy_pct = (np.sum(y) / len(y)) * 100.0
                put_pct = 100.0 - buy_pct
                log.info(f"[{self._key}] Training on {len(y)} candles. Class balance: BUY {buy_pct:.1f}% | PUT {put_pct:.1f}%")

            # --- Bug1 fix: Online RL Integration вЂ” match by timestamp (not price) ---
            sample_weights = np.ones(len(y), dtype=np.float32)
            rl_feedbacks = _fetch_rl_feedback(self.symbol, self.interval)

            if rl_feedbacks:
                parsed_feedbacks = []
                for f in rl_feedbacks:
                    try:
                        ts_str = str(f.get("ts", "")).strip().replace("Z", "+00:00")
                        if not ts_str:
                            continue
                        ts = datetime.fromisoformat(ts_str)
                        if ts.tzinfo is None:
                            ts = ts.replace(tzinfo=timezone.utc)
                        parsed_feedbacks.append({"ts": ts, "dir": f["dir"], "win": int(f["win"])})
                    except Exception:
                        pass

                if parsed_feedbacks:
                    match_count = 0
                    for i, orig_idx in enumerate(feat_indices_valid):
                        raw_time = candles[orig_idx].get("openTime")
                        if raw_time is None:
                            continue
                        try:
                            # openTime can be Unix timestamp (int/float) or ISO string
                            if isinstance(raw_time, (int, float)):
                                candle_dt = datetime.fromtimestamp(raw_time, tz=timezone.utc)
                            else:
                                ts_str = str(raw_time).replace(" ", "T").replace("Z", "+00:00")
                                candle_dt = datetime.fromisoformat(ts_str)
                                if candle_dt.tzinfo is None:
                                    candle_dt = candle_dt.replace(tzinfo=timezone.utc)
                        except Exception:
                            continue

                        best_fb, best_diff = None, float("inf")
                        for fb in parsed_feedbacks:
                            diff = abs((fb["ts"] - candle_dt).total_seconds())
                            if diff < best_diff:
                                best_diff, best_fb = diff, fb

                        # FIX W-12: Match window was hardcoded to В±300s (5 mins).
                        # For s5 TF, this matched one trade to 60 candles (massive noise).
                        # Now dynamic: В±1.5 candles
                        tf_seconds = {"s5": 5, "s15": 15, "s30": 30, "1m": 60, "5m": 300, "15m": 900}.get(self.interval, 60)
                        max_diff = max(30, tf_seconds * 1.5)
                        
                        if best_fb and best_diff < max_diff:
                            match_count += 1
                            sample_weights[i] = 5.0  # x5 weight (reduced from x10 to avoid over-correction)
                            win, dir_ = best_fb["win"], best_fb["dir"]
                            if win == 0:
                                y[i] = 0 if dir_ == "BUY" else 1
                            else:
                                y[i] = 1 if dir_ == "BUY" else 0

                    if match_count > 0:
                        log.info(f"[Online RL] {self._key}: matched {match_count} feedback samples by timestamp (В±5 min window) with x5 weight.")
                    else:
                        log.debug(f"[Online RL] {self._key}: {len(parsed_feedbacks)} feedbacks parsed but 0 matched to candles (time mismatch >5 min).")
            # -----------------------------------------------------------------------

            # Train/val split
            tscv = TimeSeriesSplit(n_splits=3)
            val_accs, val_aucs = [], []

            for train_idx, val_idx in tscv.split(X):
                X_tr, X_val = X[train_idx], X[val_idx]
                y_tr, y_val = y[train_idx], y[val_idx]

                m = lgb.LGBMClassifier(**LGBM_PARAMS)
                m.fit(
                    X_tr, y_tr,
                    sample_weight=sample_weights[train_idx],
                    eval_set=[(X_val, y_val)],
                    eval_sample_weight=[sample_weights[val_idx]],
                    callbacks=[lgb.early_stopping(50, verbose=False),
                               lgb.log_evaluation(period=-1)]
                )
                preds = m.predict(X_val)
                probs = m.predict_proba(X_val)[:, 1]
                val_accs.append(accuracy_score(y_val, preds))
                try:
                    val_aucs.append(roc_auc_score(y_val, probs))
                except Exception:
                    val_aucs.append(0.5)

            avg_acc = float(np.mean(val_accs))
            avg_auc = float(np.mean(val_aucs))

            # Final model on all data
            final_model = lgb.LGBMClassifier(**LGBM_PARAMS)
            final_model.fit(X, y, sample_weight=sample_weights)

            version = f"lgbm-v1-{self._key}-{int(time.time())}"
            meta = ModelMeta(
                accuracy=avg_acc,
                auc=avg_auc,
                n_train=len(X),
                trained_at=time.time(),
                version=version,
            )

            self._save(final_model, meta)

            # FIX H-3: Quality Gate — do not deploy new model if it's significantly worse
            # than the current one. This prevents weekly retraining from replacing a
            # good 58%-accuracy model with a bad 49% model on noisy/thin data.
            with self._lock:
                current_acc = self._meta.accuracy if self._meta is not None else 0.0

            if avg_acc < current_acc - 0.02:
                log.warning(
                    f"[Train] Quality Gate BLOCKED deploy for {self._key}: "
                    f"new_acc={avg_acc:.4f} < current_acc={current_acc:.4f} - 0.02. "
                    f"Keeping old model."
                )
                return {
                    "symbol": self.symbol, "interval": self.interval,
                    "n_train": len(X), "accuracy": round(avg_acc, 4),
                    "auc": round(avg_auc, 4), "version": version,
                    "deployed": False, "reason": "quality_gate_blocked"
                }

            with self._lock:
                self._model = final_model
                self._meta = meta

            report = {
                "symbol": self.symbol,
                "interval": self.interval,
                "n_train": len(X),
                "accuracy": round(avg_acc, 4),
                "auc": round(avg_auc, 4),
                "version": version,
            }
            log.info(f"[Train] Done: {report}")
            return report

        except Exception as e:
            log.error(f"[Train] {self._key}: {e}", exc_info=True)
            return {"error": str(e)}
        finally:
            with self._lock:
                self.is_training = False

    def needs_retrain(self) -> bool:
        with self._lock:
            if self.is_training:
                return False
            meta = self._meta
        if meta is None:
            return True
        age_h = (time.time() - meta.trained_at) / 3600
        return age_h >= RETRAIN_INTERVAL_H

    def get_status(self) -> Dict:
        with self._lock:
            meta = self._meta
        if meta is None:
            return {"status": "not-trained", "key": self._key}
        return {
            "key": self._key,
            "accuracy": meta.accuracy,
            "auc": meta.auc,
            "n_train": meta.n_train,
            "version": meta.version,
            "age_hours": round((time.time() - meta.trained_at) / 3600, 1),
        }

    # в”Ђв”Ђ Internal helpers в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    def _model_path(self) -> Path:
        return MODEL_DIR / f"{self._key}.pkl"

    def _save(self, model, meta: ModelMeta):
        # FIX C-09: write atomically via tmp + os.replace, same pattern as SGD.
        # Old code wrote directly to the target file вЂ” a crash during joblib.dump
        # would leave a corrupted pkl that permanently breaks model loading.
        p = self._model_path()
        tmp = p.with_suffix(".tmp")
        joblib.dump({"model": model, "meta": meta}, tmp)
        os.replace(tmp, p)

    def _try_load(self):
        # Load LightGBM (Tier 1)
        p = self._model_path()
        if p.exists():
            try:
                data = joblib.load(p)
                with self._lock:
                    self._model = data["model"]
                    self._meta = data["meta"]
                log.info(f"[Load] Loaded LightGBM from {p}")
            except Exception as e:
                # FIX W-10: corrupted pkl was silently ignored on every restart.
                # Delete it so next retrain produces a clean file.
                log.warning(f"[Load] Failed to load LightGBM {p}: {e}. Deleting corrupt file.")
                try:
                    p.unlink(missing_ok=True)
                except Exception:
                    pass

        # Load SGD (Tier 2) вЂ” Bug3 fix: restore update count from dict format
        sgd_path = SGD_MODEL_DIR / f"{self._key}_sgd.pkl"
        if sgd_path.exists():
            try:
                sgd_data = joblib.load(sgd_path)
                with self._online_lock:
                    if isinstance(sgd_data, dict):
                        # New format: {model, count}
                        self._online_model = sgd_data["model"]
                        self._sgd_update_count = int(sgd_data.get("count", 0))
                    else:
                        # Legacy format: raw SGDClassifier object (backward compat)
                        self._online_model = sgd_data
                        self._sgd_update_count = 0
                log.info(f"[Load] Loaded SGD from {sgd_path} | total_updates={self._sgd_update_count}")
            except Exception as e:
                log.warning(f"[Load] Failed to load SGD {sgd_path}: {e}")


    def _fetch_binance(self, limit: int = 1500) -> List[Dict]:
        """Fetch historical klines from Binance REST API."""
        binance_interval = TF_MAP.get(self.interval, self.interval)
        url = f"{BINANCE_BASE}/api/v3/klines"
        params = {"symbol": self.symbol, "interval": binance_interval, "limit": min(limit, 1500)}
        resp = requests.get(url, params=params, timeout=15)
        resp.raise_for_status()
        raw = resp.json()
        candles = [
            {
                "open":   float(k[1]),
                "high":   float(k[2]),
                "low":    float(k[3]),
                "close":  float(k[4]),
                "volume": float(k[5]),
            }
            for k in raw
        ]
        return candles

    def _fetch_twelvedata(self, limit: int = 1500) -> List[Dict]:
        """Fetch historical candles from TwelveData REST API."""
        if not TWELVE_DATA_API_KEY:
            raise ValueError("TwelveDataApiKey environment variable is not configured on the service.")

        td_symbol = to_twelvedata_symbol(self.symbol)
        td_interval = TD_INTERVAL_MAP.get(self.interval, "1min")
        
        log.info(f"[TwelveData] Fetching history for {td_symbol} ({td_interval}), limit={limit}")
        
        url = f"{TWELVE_DATA_BASE}/time_series"
        params = {
            "symbol": td_symbol,
            "interval": td_interval,
            "outputsize": min(limit, 5000),
            "apikey": TWELVE_DATA_API_KEY
        }
        
        resp = requests.get(url, params=params, timeout=20)
        resp.raise_for_status()
        data = resp.json()
        
        if data.get("status") == "error":
            raise Exception(f"TwelveData API error: {data.get('message')}")
            
        raw_candles = data.get("values")
        if not raw_candles:
            raise Exception(f"TwelveData returned no candles for {td_symbol}")
            
        # Reversing so that the oldest is at index 0 and latest is at index -1
        raw_candles.reverse()
        
        candles = [
            {
                "open":   float(k["open"]),
                "high":   float(k["high"]),
                "low":    float(k["low"]),
                "close":  float(k["close"]),
                "volume": float(k.get("volume", 0.0) or 0.0),
            }
            for k in raw_candles
        ]
        log.info(f"[TwelveData] Successfully fetched {len(candles)} candles for {td_symbol}")
        return candles



