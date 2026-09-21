import threading
import numpy as np
import logging
import joblib
import os
from sklearn.linear_model import SGDClassifier
from typing import Optional

log = logging.getLogger("SGD")

class OnlineTactician:
    def __init__(self, key: str, save_dir: str):
        self._key = key
        self._save_dir = save_dir
        self._online_model: Optional[SGDClassifier] = None
        self._online_lock = threading.Lock()
        self._online_classes = np.array([0, 1])
        self._sgd_update_count: int = 0
        self.load()

    def partial_fit(self, X_last: np.ndarray, y: np.ndarray, sample_w: Optional[np.ndarray]) -> bool:
        with self._online_lock:
            if self._online_model is None:
                self._online_model = SGDClassifier(
                    loss="log_loss",
                    learning_rate="optimal",
                    alpha=0.01,
                    random_state=42,
                    warm_start=True,
                )
            self._online_model.partial_fit(X_last, y, classes=self._online_classes, sample_weight=sample_w)
            self._sgd_update_count += 1
            # Atomic save (throttled)
            if self._sgd_update_count <= 5 or self._sgd_update_count % 10 == 0:
                sgd_path = os.path.join(self._save_dir, f"{self._key}_sgd.pkl")
                os.makedirs(self._save_dir, exist_ok=True)
                tmp_path = sgd_path + ".tmp"
                joblib.dump({"model": self._online_model, "count": self._sgd_update_count}, tmp_path)
                os.replace(tmp_path, sgd_path)
            
        return True

    def predict_proba(self, X_arr: np.ndarray) -> Optional[float]:
        with self._online_lock:
            if self._online_model is not None:
                try:
                    return float(self._online_model.predict_proba(X_arr)[0, 1])
                except Exception:
                    pass
        return None

    def get_weight(self) -> float:
        with self._online_lock:
            return min(0.05, self._sgd_update_count / 200.0)

    def load(self):
        sgd_path = os.path.join(self._save_dir, f"{self._key}_sgd.pkl")
        if os.path.exists(sgd_path):
            try:
                sgd_data = joblib.load(sgd_path)
                with self._online_lock:
                    if isinstance(sgd_data, dict):
                        self._online_model = sgd_data["model"]
                        self._sgd_update_count = int(sgd_data.get("count", 0))
                    else:
                        self._online_model = sgd_data
                        self._sgd_update_count = 0
                log.info(f"[Tactician] Loaded for {self._key} | updates={self._sgd_update_count}")
            except Exception as e:
                log.warning(f"[Tactician] Load failed for {sgd_path}: {e}")


