import threading
import numpy as np
import logging
import joblib
import os
from typing import Optional, Dict, List
import lightgbm as lgb
try:
    HAS_LGBM = True
except ImportError:
    HAS_LGBM = False

log = logging.getLogger("Shadow")

class ShadowChallenger:
    def __init__(self, key: str, save_dir: str):
        self._key = key
        self._save_dir = save_dir
        self._challenger_model: Optional[lgb.LGBMClassifier] = None
        self._challenger_meta = None
        self._challenger_lock = threading.Lock()
        self._shadow_trades: List[Dict] = []
        self._shadow_promoted = False
        self.load()

    def add_trade(self, trade_data: Dict) -> int:
        with self._challenger_lock:
            if self._challenger_model is not None:
                self._shadow_trades.append(trade_data)
                # Keep only last 1000
                if len(self._shadow_trades) > 1000:
                    self._shadow_trades.pop(0)
                return len(self._shadow_trades)
        return 0
        
    def get_trades(self) -> List[Dict]:
        with self._challenger_lock:
            return list(self._shadow_trades)
            
    def clear_trades(self):
        with self._challenger_lock:
            self._shadow_trades.clear()

    def update_model(self, model, meta):
        with self._challenger_lock:
            self._challenger_model = model
            self._challenger_meta = meta
            self._shadow_trades.clear()
            self._shadow_promoted = False
            
            # Atomic save
            path = os.path.join(self._save_dir, f"{self._key}_challenger.pkl")
            os.makedirs(self._save_dir, exist_ok=True)
            tmp_path = path + ".tmp"
            joblib.dump({"model": model, "meta": meta}, tmp_path)
            os.replace(tmp_path, path)

    def predict_proba(self, X_arr: np.ndarray) -> Optional[float]:
        with self._challenger_lock:
            if self._challenger_model is not None:
                try:
                    return float(self._challenger_model.predict_proba(X_arr)[0, 1])
                except Exception:
                    pass
        return None

    def load(self):
        path = os.path.join(self._save_dir, f"{self._key}_challenger.pkl")
        if os.path.exists(path):
            try:
                data = joblib.load(path)
                with self._challenger_lock:
                    self._challenger_model = data.get("model")
                    self._challenger_meta = data.get("meta")
                log.info(f"[Shadow] Loaded for {self._key}")
            except Exception as e:
                log.warning(f"[Shadow] Load failed for {path}: {e}")
