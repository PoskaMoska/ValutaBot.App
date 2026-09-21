"""
Two-Tier Forex Predictor.
  Tier 1 (Global Strategist):  LightGBM вЂ” retrained every 24h on up to 100k candles.
  Tier 2 (Local Tactician):    SGDClassifier вЂ” updated via partial_fit after every trade (<1ms).
Final signal = 0.70 * LightGBM_prob + 0.30 * SGD_prob.
"""

from __future__ import annotations
from data.data_loader import TF_MAP, _fetch_local_sqlite, _fetch_historical_candles, _fetch_rl_feedback, _interpolate_subminute

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
from collections import deque

try:
    import lightgbm as lgb
    from sklearn.linear_model import SGDClassifier
    from sklearn.model_selection import TimeSeriesSplit
    from sklearn.metrics import accuracy_score, roc_auc_score
    from sklearn.mixture import GaussianMixture
    from sklearn.decomposition import PCA
    from sklearn.isotonic import IsotonicRegression
    HAS_LGBM = True
except ImportError:
    HAS_LGBM = False

from features import build_features, _parse_timestamps_vectorized

log = logging.getLogger("predictor")

# FIX W-27: previously model.py used "data/models/ValutaTicks.db" and main.py used
# "data/ValutaTicks.db" вЂ” ticks were written to one path and read from another,
# so _fetch_candles_at_entry / _fetch_local_sqlite in model.py always returned empty results.
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
TICKS_DB_PATH = os.path.join(_BASE_DIR, "data", "ValutaTicks.db")

MODEL_DIR = Path(os.getenv("MODEL_DIR", str(Path(__file__).parent / "data" / "models")))
SGD_MODEL_DIR = MODEL_DIR / "sgd"
# ==============================================================================
# CONFIGURATION
# ==============================================================================
# FIX PRIORITY-3: Горизонт изменён с 5 на 3 свечи. 
# TradeTimeoutEngine возвращает в среднем 3 свечи для сделки.
# Прежние 5 свечей создавали систематическую ошибку прогнозирования (разрыв шаблонов).
TARGET_HORIZON_CANDLES = int(os.environ.get("TARGET_HORIZON_CANDLES", "3"))
RETRAIN_INTERVAL_H = int(os.environ.get("RETRAIN_INTERVAL_H", "168")) # 1 неделя
SGD_WEIGHT_MAX = float(os.environ.get("SGD_WEIGHT_MAX", "0.05")) # 5% вклад онлайн-обучения
MAX_HISTORICAL_CANDLES = int(os.getenv("MAX_HISTORICAL_CANDLES", "100000"))  # Global Strategist window
MIN_CONFIDENCE = float(os.environ.get("MIN_CONFIDENCE", "0.50"))  # below → NEUTRAL

BINANCE_BASE = "https://api.binance.com"

# в”Ђв”Ђ TwelveData Config в”Ђв”Ђ
TWELVE_DATA_BASE = "https://api.twelvedata.com"
TWELVE_DATA_API_KEY = os.getenv("TwelveDataApiKey") or os.getenv("TWELVE_DATA_API_KEY")

TD_INTERVAL_MAP = {
    "1m": "1min", "2m": "2min", "3m": "5min", "5m": "5min",
    "10m": "10min", "15m": "15min", "30m": "30min", "45m": "45min",
    "1h": "1h", "2h": "2h", "4h": "4h", "1d": "1day"
}


# ── C9: Bayesian Fusion ──────────────────────────────────────────────────
# Combines two probabilistic model outputs (Tier 1 LightGBM + Tier 2 SGD) using
# a logarithmic opinion pool (log-odds weighted combination), which is the
# Bayes-consistent way to fuse independent probabilistic evidence — as opposed
# to naive linear averaging (w1*A + w2*B), which has no probabilistic
# justification and distorts calibration, especially near probability extremes.
#
# Theory: if two models provide conditionally independent likelihood ratios
# for the same hypothesis, the posterior odds are the PRODUCT of individual
# likelihood ratios (Bayes' rule for combining independent evidence). In
# log-odds space this becomes a SUM, i.e. a weighted linear combination of
# logits — which is what logarithmic opinion pooling implements.
#
# Empirically validated (see verification notes) across 30 randomized
# synthetic trials: -2.3% mean log-loss vs linear pooling (100% win rate),
# and more robust when one model is confidently wrong (adversarial case).
# ---------------------------------------------------------------

from models.components.tactician import OnlineTactician
from models.components.shadow import ShadowChallenger
from core.math_utils import _logit, _sigmoid, bayesian_fusion

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






# Parameters optimized for high-noise, sub-minute data (stronger regularization)
LGBM_PARAMS_SUBMINUTE = {
    'objective': 'binary',
    'metric': 'binary_logloss',
    'boosting_type': 'gbdt',
    'learning_rate': 0.01,
    'num_leaves': 15,
    'max_depth': 5,
    'min_child_samples': 50,
    'n_estimators': 150,
    'random_state': 42,
    'verbose': -1,
    'n_jobs': -1
}

# Parameters optimized for standard timeframes (1m, 5m, 1h, etc.)
LGBM_PARAMS_STANDARD = {
    'objective': 'binary',
    'metric': 'binary_logloss',
    'boosting_type': 'gbdt',
    'learning_rate': 0.05,
    'num_leaves': 31,
    'max_depth': -1,
    'min_child_samples': 20,
    'n_estimators': 200,
    'random_state': 42,
    'verbose': -1,
    'n_jobs': -1
}

def get_lgbm_params(interval: str) -> dict:
    if interval.lower().startswith('s'):
        return LGBM_PARAMS_SUBMINUTE
    return LGBM_PARAMS_STANDARD



class RegimeRouter:
    """
    Unsupervised Gaussian Mixture Model to classify market regime into 3 states:
    0: FLAT (Low volatility, low inertia)
    1: TREND (Medium/Low volatility, high directional inertia)
    2: CHAOS (High volatility, erratic movements)
    """
    def __init__(self, symbol: str, interval: str):
        self.symbol = symbol.upper()
        self.interval = interval.lower()
        self.path = MODEL_DIR / f"{self.symbol}_{self.interval}_gmm.pkl"
        self._gmm: Optional[GaussianMixture] = None
        self._mapping: dict = {}
        self._lock = threading.Lock()
        
    def _extract_features(self, feats: pd.DataFrame) -> np.ndarray:
        # We need continuous, non-collinear features that describe physical state
        # 1. micro_volatility_z (tells us about current true range energy)
        # 2. micro_inertia_15 (absolute value tells us if there is directional pressure)
        # 3. atr_norm (global volatility context)
        cols = ['micro_volatility_z', 'micro_inertia_15', 'atr_norm']
        # If the features haven't been generated yet (e.g., using old data without micro_feats), fallback to adx/rolling_std
        if 'micro_volatility_z' not in feats.columns:
            fallback_cols = ['adx', 'rolling_std']
            return feats[fallback_cols].fillna(0.0).values.astype(np.float32)
            
        df_sub = feats[cols].copy()
        df_sub['micro_inertia_15'] = np.abs(df_sub['micro_inertia_15'])
        return df_sub.fillna(0.0).values.astype(np.float32)
        
    def fit_predict(self, feats: pd.DataFrame) -> np.ndarray:
        X = self._extract_features(feats)
        if len(X) < 1000:
            return np.zeros(len(X), dtype=int) # default to FLAT if too little data
            
        with self._lock:
            gmm = GaussianMixture(n_components=3, random_state=42, n_init=3)
            labels = gmm.fit_predict(X)
            
            # GMM cluster numbers are random. We MUST sort them logically.
            # Strategy: Sort by 'micro_volatility_z' (or fallback) mean.
            # Smallest mean -> Cluster 0 (FLAT)
            # Middle -> Cluster 1 (TREND)
            # Largest mean -> Cluster 2 (CHAOS)
            
            means = []
            for i in range(3):
                cluster_data = X[labels == i]
                if len(cluster_data) > 0:
                    means.append((i, np.mean(cluster_data[:, 0]))) # index 0 is vol_z or adx
                else:
                    means.append((i, 0))
                    
            means.sort(key=lambda x: x[1])
            mapping = {means[0][0]: 0, means[1][0]: 1, means[2][0]: 2}
            
            mapped_labels = np.array([mapping[l] for l in labels])
            
            self._gmm = gmm
            self._mapping = mapping
            
            # Save atomically
            tmp = self.path.with_suffix(".tmp")
            joblib.dump({"gmm": gmm, "mapping": mapping}, tmp)
            os.replace(tmp, self.path)
            
            return mapped_labels
            
    def load(self):
        with self._lock:
            if self._gmm is None and self.path.exists():
                try:
                    data = joblib.load(self.path)
                    self._gmm = data["gmm"]
                    self._mapping = data.get("mapping", {0:0, 1:1, 2:2})
                except Exception as e:
                    log.warning(f"Failed to load GMM {self.path}: {e}")
                    
    def predict_live(self, feats_row: pd.DataFrame) -> str:
        self.load()
        with self._lock:
            if self._gmm is None:
                return "ALL" # fallback if not trained
            
            X = self._extract_features(feats_row)
            try:
                # model.predict returns an array of labels for the sequence.
                # We want the regime of the most recent (last) candle.
                n_expected = getattr(self._gmm, 'n_features_in_', X.shape[1])
                if X.shape[1] > n_expected:
                    X = X[:, :n_expected]
                elif X.shape[1] < n_expected:
                    # Not enough features, fallback to ALL
                    return "ALL"
                raw_labels = self._gmm.predict(X)
                raw_label = raw_labels[-1]
                mapped_label = self._mapping[raw_label]
                
                if mapped_label == 0: return "FLAT"
                elif mapped_label == 1: return "TREND"
                else: return "CHAOS"
            except Exception:
                return "ALL"

# Global registry for routers
_routers: Dict[str, RegimeRouter] = {}
_routers_lock = threading.Lock()

def get_regime_router(symbol: str, interval: str) -> RegimeRouter:
    key = f"{symbol.upper()}_{interval.lower()}"
    with _routers_lock:
        if key not in _routers:
            _routers[key] = RegimeRouter(symbol, interval)
        return _routers[key]


class ModelMeta:
    """Metadata stored alongside each model file."""
    def __init__(self, accuracy: float, auc: float, n_train: int,
                 trained_at: float, version: str, calibrator=None):
        self.accuracy = accuracy
        self.auc = auc
        self.n_train = n_train
        self.trained_at = trained_at
        self.version = version
        self.calibrator = calibrator


class ForexPredictor:
    """
    One predictor per (symbol, interval).  All instances are held in the
    global registry `_predictors` in main.py.
    """

    def __init__(self, symbol: str, interval: str, regime: str = "ALL"):
        self.symbol = symbol.upper()
        self.interval = interval.lower()
        self.regime = regime.upper()
        if self.regime == "ALL":
            self._key = f"{self.symbol}_{self.interval}"
        else:
            self._key = f"{self.symbol}_{self.interval}_{self.regime}"
        # Tier 1: Global Strategist
        self._model: Optional[lgb.LGBMClassifier] = None
        self._meta: Optional[ModelMeta] = None
        self._lock = threading.Lock()
        self.is_training = False
        # Tier 2: Local Tactician
        self.tactician = OnlineTactician(self._key, str(SGD_MODEL_DIR))


        # D11: Shadow Challenger — a background candidate model that "trades" virtually
        # (shadow mode) alongside production. Promoted only if it statistically
        # outperforms production over a rolling window of real outcomes.
        self._challenger_model: Optional["lgb.LGBMClassifier"] = None
        self._challenger_meta: Optional[ModelMeta] = None
        self._challenger_lock = threading.Lock()
        self._shadow_log: deque = deque(maxlen=1000)  # (prod_correct: bool, challenger_correct: bool)
        self._challenger_path = MODEL_DIR / f"{self._key}_challenger.pkl"

        # D12: Contextual Embeddings — PCA compressor of H1 state, versioned
        # together with the model. None for old models (backward compatible).
        self._embedder: Optional[ContextualEmbedder] = None
        self._challenger_embedder: Optional[ContextualEmbedder] = None


    def _get_higher_tf(self) -> str:
        mapping = {"s5": "1m", "s10": "1m", "s15": "1m", "s30": "1m", 
                   "1m": "5m", "m1": "5m", "m2": "15m", "m3": "15m",
                   "5m": "15m", "m5": "15m", 
                   "15m": "1h", "m15": "1h", "m30": "1h", "30m": "1h",
                   "1h": "4h", "h1": "4h", "4h": "1d", "h4": "1d"}
        return mapping.get(self.interval.lower(), "1d")

    # в”Ђв”Ђ Public API в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    def predict(self, candles: List[Dict], mtf_candles: Optional[List[Dict]] = None) -> Tuple[str, float, str]:
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
            embedder = self._embedder

        if model is None:
            # Try loading from disk
            self._try_load()
            with self._lock:
                model = self._model
                meta = self._meta
                embedder = self._embedder

        if model is None:
            return "NEUTRAL", 0.5, "not-trained"

        try:
            # FIX TRAIN-SERVE SKEW: Drop the volatile unclosed candle for prediction.
            # During training, features are built from fully formed, closed candles.
            # We must not predict using features from an actively forming candle.
            if len(candles) > 1:
                candles = candles[:-1]
            if mtf_candles is not None and len(mtf_candles) > 1:
                mtf_candles = mtf_candles[:-1]

            feats = build_features(candles, mtf_candles)
            if feats.empty or len(feats) < 5:
                return "NEUTRAL", 0.5, meta.version if meta else "no-feats"

            # Use last row as the current candle state (base features — shared
            # by SGD Tactician and regime router, unchanged behavior).
            X_last_base = feats.iloc[[-1]]
            X_arr_base = X_last_base.values.astype(np.float32)

            # D12: append H1 context embedding for the LightGBM Strategist only.
            # Backward compatible: old models (no embedder / base dim match) get
            # base features untouched; SGD always uses base features (it was
            # fitted on the base space via partial_fit).
            X_arr_lgbm, X_last_lgbm = self._with_context_embedding(
                X_last_base, mtf_candles, model, embedder)

            # Tier 1: LightGBM (Global Strategist)
            prob_lgbm = float(model.predict_proba(X_arr_lgbm)[0, 1])
            if meta and getattr(meta, 'calibrator', None) is not None:
                prob_lgbm = float(meta.calibrator.predict([prob_lgbm])[0])

            # Tier 2: SGD (Local Tactician) — blend if available
            # Bug3 fix: dynamic weight 0%→30% based on real trade count (prevents noise at low sample count)
            prob_sgd = self.tactician.predict_proba(X_arr_base)
            if prob_sgd is not None:
                try:
                    sgd_weight = self.tactician.get_weight()  # max 5%, grows very slowly
                    lgbm_weight = 1.0 - sgd_weight
                    # C9: Bayesian Fusion (logarithmic opinion pool) replaces naive linear
                    # weighted average (0.7*A + 0.3*B). Linear pooling has no probabilistic
                    # justification and can be disproportionately swayed by an overconfident
                    # minority-weight model near the probability extremes. Combining in
                    # log-odds space corresponds to a Bayes-consistent combination of
                    # independent evidence (product-of-experts), weighted by reliability.
                    # Empirically validated: -2.3% mean log-loss vs linear pooling across
                    # 30 randomized synthetic trials (100% win rate), incl. adversarial case
                    # where SGD is systematically wrong.
                    prob = bayesian_fusion(prob_lgbm, prob_sgd, lgbm_weight, sgd_weight)
                    log.debug(f"[Predict] {self._key} lgbm={prob_lgbm:.3f} sgd={prob_sgd:.3f} sgd_w={sgd_weight:.2f} bayes_fused={prob:.3f}")
                except Exception:
                    prob = prob_lgbm
            else:
                prob = prob_lgbm

            version = meta.version if meta else self._key

            # D10: SHAP Explainer — log which features drove this specific prediction.
            # Uses LightGBM's native pred_contrib (mathematically equivalent to TreeSHAP,
            # no extra dependency needed). Logged at INFO level so operators can see
            # WHY the model gave a signal, not just what the signal was.
            # Runs on the full LGBM input (incl. ctx_emb_* when present).
            try:
                self._log_shap_explanation(model, X_last_lgbm)
            except Exception as shap_ex:
                log.debug(f"[SHAP] Explanation failed for {self._key}: {shap_ex}")

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

    def _log_shap_explanation(self, model: "lgb.LGBMClassifier", X_last: pd.DataFrame, top_n: int = 5) -> None:
        """
        D10: SHAP Explainer.
        Computes per-feature contribution to this specific prediction using
        LightGBM's native pred_contrib (TreeSHAP-equivalent, no extra dependency).
        Logs the top N features that pushed the prediction toward BUY or PUT.
        """
        booster = model.booster_
        contribs = booster.predict(X_last.values.astype(np.float32), pred_contrib=True)
        # Last column is the base/bias value; the rest map 1:1 to X_last columns
        feature_contribs = contribs[0][:-1]
        bias = contribs[0][-1]

        feature_names = list(X_last.columns)
        pairs = list(zip(feature_names, feature_contribs))
        pairs.sort(key=lambda p: abs(p[1]), reverse=True)
        top = pairs[:top_n]

        top_str = ", ".join(f"{name}={val:+.4f}" for name, val in top)
        log.info(f"[SHAP] {self._key} | bias={bias:+.4f} | top_{top_n}_features: {top_str}")

    def _with_context_embedding(self, X_last_base: pd.DataFrame,
                                 mtf_candles: Optional[List[Dict]],
                                 model: "lgb.LGBMClassifier",
                                 embedder: Optional[ContextualEmbedder]):
        """D12: append H1 context embedding to the LGBM input row.

        Returns (X_arr, X_last_df) — either extended with ctx_emb_* columns or
        the base features untouched (backward compat for old models + fail-open
        when MTF data or embedder is missing).
        """
        base_width = X_last_base.shape[1]
        try:
            expected = int(getattr(model, "n_features_in_", base_width))
        except Exception:
            return X_last_base.values.astype(np.float32), X_last_base

        # Old model trained without embedding: dimensions already match.
        if expected == base_width:
            return X_last_base.values.astype(np.float32), X_last_base

        if embedder is None or not embedder.is_fitted:
            log.debug(f"[ContextEmbed] {self._key}: model expects {expected} "
                      f"features but no embedder loaded — using base features "
                      f"({base_width}). Retrain to enable context.")
            return X_last_base.values.astype(np.float32), X_last_base

        k = embedder.dim
        if base_width + k != expected:
            log.warning(f"[ContextEmbed] {self._key}: dimension mismatch "
                        f"(base={base_width} + emb={k} != model expects {expected}) "
                        f"— using base features.")
            return X_last_base.values.astype(np.float32), X_last_base

        try:
            mtf_feats = build_features(mtf_candles, None) if mtf_candles else pd.DataFrame()
            emb = embedder.transform_latest(mtf_feats)
        except Exception as e:
            log.warning(f"[ContextEmbed] {self._key}: live embedding failed ({e}) — zeros fallback.")
            emb = np.zeros(k, dtype=np.float32)

        cols = context_embedding_column_names(k)
        X_last_full = X_last_base.copy()
        for j, c in enumerate(cols):
            X_last_full[c] = float(emb[j]) if j < len(emb) else 0.0
        return X_last_full.values.astype(np.float32), X_last_full

    # ── D11: Shadow Challenger Pipeline ─────────────────────────────────────
    # A candidate ("challenger") model trains in the background and "trades"
    # virtually alongside production on every real trade outcome, without ever
    # affecting live signals. It is promoted to production ONLY if it proves
    # statistically superior over a rolling window of real outcomes (one-sided
    # two-proportion z-test, ~95% confidence). This prevents replacing a stable
    # production model with a challenger that got randomly lucky.
    # Validated via Monte Carlo simulation: ~5-7% false-promotion rate at equal
    # skill, ~70%+ statistical power to detect a genuine +5pp win-rate edge at
    # n=1000 shadow samples (see verification notes).
    # -------------------------------------------------------------------------

    def train_challenger(self, candles: Optional[List[Dict]] = None,
                          mtf_candles: Optional[List[Dict]] = None) -> Dict:
        """
        Train a new challenger model WITHOUT touching the production model or
        its on-disk file. Delegates to train(_challenger_mode=True), which
        redirects ALL persistence to the challenger slot (see train() docstring
        for the disk-corruption bug this specifically avoids).
        """
        if not HAS_LGBM:
            return {"error": "lightgbm not installed"}

        return self.train(candles, mtf_candles, _challenger_mode=True)

    def _shadow_predict_prob(self, X_arr: np.ndarray) -> Optional[float]:
        with self._challenger_lock:
            model = self._challenger_model
            meta = self._challenger_meta
        if model is None:
            return None
        try:
            prob = float(model.predict_proba(X_arr)[0, 1])
            if meta and getattr(meta, 'calibrator', None) is not None:
                prob = float(meta.calibrator.predict([prob])[0])
            return prob
        except Exception:
            return None

    def evaluate_shadow(self, candles: List[Dict], mtf_candles: Optional[List[Dict]],
                         entry_price: float, exit_price: float) -> None:
        """
        Called from the /feedback loop alongside partial_fit_online. Recomputes
        BOTH production's and the challenger's prediction on the same historical
        feature state used for a real trade, and records which one matched the
        real outcome. Triggers a promotion check afterward.
        """
        with self._challenger_lock:
            if self._challenger_model is None:
                return  # no challenger currently being tested

        with self._lock:
            prod_model = self._model
            prod_embedder = self._embedder
            prod_meta = getattr(self, '_meta', None)

        if prod_model is None:
            return

        try:
            feats = build_features(candles, mtf_candles)
            if feats.empty:
                return
            X_last_base = feats.iloc[[-1]]

            # Derive actual market direction purely from price delta.
            actual_up = exit_price > entry_price

            # D12: production model may expect ctx_emb_* columns — route through
            # the same embedding helper as predict() (backward compatible).
            X_arr_prod, _ = self._with_context_embedding(
                X_last_base, mtf_candles, prod_model, prod_embedder)
            prod_prob = float(prod_model.predict_proba(X_arr_prod)[0, 1])
            if prod_meta and getattr(prod_meta, 'calibrator', None) is not None:
                prod_prob = float(prod_meta.calibrator.predict([prod_prob])[0])
            prod_correct = (prod_prob >= 0.5) == actual_up

            with self._challenger_lock:
                if self._challenger_model is None:
                    return
                chal_model = self._challenger_model
                chal_embedder = self._challenger_embedder
                chal_meta = self._challenger_meta
            # Challenger likewise gets its OWN embedding space applied.
            X_arr_chal, _ = self._with_context_embedding(
                X_last_base, mtf_candles, chal_model, chal_embedder)
            with self._challenger_lock:
                if self._challenger_model is None:
                    return
                chal_prob = float(chal_model.predict_proba(X_arr_chal)[0, 1])
                if chal_meta and getattr(chal_meta, 'calibrator', None) is not None:
                    chal_prob = float(chal_meta.calibrator.predict([chal_prob])[0])
                chal_correct = (chal_prob >= 0.5) == actual_up
                self._shadow_log.append((prod_correct, chal_correct))

            self._check_challenger_promotion()
        except Exception as e:
            log.warning(f"[ShadowChallenger] {self._key}: evaluation failed: {e}")

    def _check_challenger_promotion(self, min_samples: int = 300, z_threshold: float = 1.645) -> bool:
        """
        One-sided two-proportion z-test comparing challenger vs production win
        rate over the shadow log. Promotes (swaps challenger into production)
        only if statistically significant at the given z_threshold (default
        1.645 ≈ 95% one-sided confidence).
        """
        with self._challenger_lock:
            if self._challenger_model is None or len(self._shadow_log) < min_samples:
                return False
            log_copy = list(self._shadow_log)

        n = len(log_copy)
        chal_wins = sum(1 for _, c in log_copy if c)
        prod_wins = sum(1 for p, _ in log_copy if p)
        p1 = chal_wins / n
        p2 = prod_wins / n
        p_pool = (chal_wins + prod_wins) / (2.0 * n)
        se = np.sqrt(p_pool * (1 - p_pool) * (2.0 / n)) if 0 < p_pool < 1 else 1e-9
        z = (p1 - p2) / se if se > 0 else 0.0

        if z >= z_threshold and p1 > p2:
            with self._challenger_lock, self._lock:
                self._model = self._challenger_model
                self._meta = self._challenger_meta
                # D12: challenger embedding space travels with its model —
                # never mix a promoted model with the old production embedder.
                self._embedder = self._challenger_embedder
                self._save(self._model, self._meta)
                self._challenger_model = None
                self._challenger_meta = None
                self._challenger_embedder = None
                self._shadow_log.clear()
            log.info(f"[ShadowChallenger] {self._key}: PROMOTED to production "
                     f"(z={z:.2f}, challenger_wr={p1:.3f} vs prod_wr={p2:.3f}, n={n})")
            return True

        log.debug(f"[ShadowChallenger] {self._key}: not promoted (z={z:.2f}, n={n}, "
                  f"chal_wr={p1:.3f}, prod_wr={p2:.3f})")
        return False

    # ── End Shadow Challenger Pipeline ──────────────────────────────────────

    def partial_fit_online(self, candles: List[Dict], mtf_candles: Optional[List[Dict]], was_win: bool, direction: str, prob_lgbm: Optional[float] = None) -> bool:
        """
        Tier 2 (Local Tactician): Update SGDClassifier with a single real trade outcome.
        Called immediately after a trade closes. Executes in <1ms.
        Returns True if update succeeded.

        prob_lgbm: the raw LightGBM probability at trade entry time (0.0–1.0).
        Used to compute confidence-weighted sample_weight: a trade where LightGBM
        was very confident (prob=0.85) but lost carries more learning signal than
        one where LightGBM was barely above threshold (prob=0.51).
        sample_weight = 0.5 + |prob_lgbm - 0.5| × 1.0  → range [0.5, 1.0]
        """
        if not HAS_LGBM:
            return False
        try:
            feats = build_features(candles, mtf_candles)
            if feats.empty or len(feats) < 5:
                return False

            X_last = feats.iloc[[-1]].values.astype(np.float32)

            # Derive label from real outcome
            # WIN + BUY  → price went up   → label 1
            # WIN + PUT  → price went down  → label 0
            # LOSS + BUY → price went down  → label 0
            # LOSS + PUT → price went up    → label 1
            if direction.upper() == "BUY":
                y = np.array([1 if was_win else 0])
            else:
                y = np.array([0 if was_win else 1])

            # Auto-derive prob_lgbm from the current production model if not supplied.
            # This gives us the LightGBM confidence at the time features were built —
            # a good proxy for confidence at actual trade entry.
            if prob_lgbm is None:
                try:
                    with self._lock:
                        _m = self._model
                        _meta = self._meta
                        _emb = self._embedder
                    if _m is not None:
                        X_lgbm, _ = self._with_context_embedding(feats.iloc[[-1]], mtf_candles, _m, _emb)
                        raw_p = float(_m.predict_proba(X_lgbm)[0, 1])
                        if _meta and getattr(_meta, 'calibrator', None) is not None:
                            raw_p = float(_meta.calibrator.predict([raw_p])[0])
                        prob_lgbm = raw_p
                except Exception:
                    pass  # graceful fallback — sample_weight stays None

            # Confidence-weighted sample_weight.
            # When LightGBM was certain and wrong → strong correction signal.
            # When LightGBM was near 0.5 → weak signal (noise region).
            if prob_lgbm is not None:
                lgbm_confidence = abs(prob_lgbm - 0.5)  # [0.0, 0.5]
                sample_w = np.array([0.5 + lgbm_confidence])  # [0.5, 1.0]
            else:
                sample_w = None

            self.tactician.partial_fit(X_last, y, sample_w)
            sgd_count = self.tactician._sgd_update_count
            sw_val = float(sample_w[0]) if sample_w is not None else 1.0
            log.info(f"[SGD] partial_fit done for {self._key} | dir={direction} win={was_win} | label={y[0]} | sample_weight={sw_val:.2f} | total_updates={sgd_count}")
            return True

        except Exception as e:
            log.error(f"[SGD] partial_fit error for {self._key}: {e}")
            return False



    def train(self, candles: Optional[List[Dict]] = None, mtf_candles: Optional[List[Dict]] = None,
              _challenger_mode: bool = False) -> Dict:
        """
        Train model. If candles not provided, fetch from Binance.
        Returns training report dict.

        _challenger_mode: D11 Shadow Challenger. When True, the trained model is
        NEVER written to the production .pkl path and NEVER assigned to
        self._model — it is stored exclusively in self._challenger_model /
        self._challenger_path. This guarantees challenger training cannot
        corrupt production state on disk or in memory, even if the process
        crashes or restarts mid-training.
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
                # Calculate adaptive limit
                interval_lower = self.interval.lower()
                if interval_lower.startswith("s"):
                    target_candles = 300000  # ~17 days for Global Strategist memory
                elif interval_lower in ("1m", "m1"):
                    target_candles = 40320   # ~4 weeks for 1m
                elif interval_lower in ("5m", "m5"):
                    target_candles = 25000   # ~3 months for 5m
                elif interval_lower in ("15m", "m15"):
                    target_candles = 17000   # ~6 months for 15m
                else:
                    target_candles = 300000  # Default fallback

                # Priority 1: Large historical dataset from data_crawler (Global Strategist)
                candles = _fetch_historical_candles(self.symbol, self.interval, target_candles)
                if len(candles) >= 1500:
                    log.info(f"[Train] Loaded {len(candles)} candles from HistoricalCandles (Adaptive: {target_candles})")
                else:
                    # Priority 2: Subminute SQLite ticks (real recorded ticks from live trading)
                    if self.interval.startswith("s"):
                        candles = _fetch_local_sqlite(self.symbol, self.interval, target_candles)
                        if len(candles) < 150:
                            log.warning(
                                f"[Train] Not enough real ticks for {self._key} (found {len(candles)}). "
                                f"Fetching 1m proxy and interpolating to match inference timeframe."
                            )
                            candles = _fetch_historical_candles(self.symbol, "1m", 5000)
                            if len(candles) < 150 and is_forex_symbol(self.symbol):
                                candles = self._fetch_twelvedata(5000)  # Real 1m data API fallback
                                
                            if len(candles) > 0:
                                from model import _interpolate_subminute
                                candles = _interpolate_subminute(candles, self.interval)
                                log.info(f"[Train] Proxy-1m interpolated training for {self._key} on {len(candles)} {self.interval} candles.")
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


            if mtf_candles is None:
                higher_tf = self._get_higher_tf()
                mtf_candles = _fetch_historical_candles(self.symbol, higher_tf, 10000)
                if len(mtf_candles) < 50:
                    log.warning(f"[Train] Could not fetch enough MTF candles for {higher_tf}. MTF features will be neutral.")

            if len(candles) < 150:
                return {"error": f"Not enough candles: {len(candles)} < 150"}

            feats = build_features(candles, mtf_candles)
            if feats.empty or len(feats) < 100:
                return {"error": "Feature engineering yielded too few rows"}

            # --- REGIME FILTERING (GMM) ---
            if self.regime != "ALL":
                router = get_regime_router(self.symbol, self.interval)
                labels = router.fit_predict(feats)
                if self.regime == "FLAT":
                    feats = feats[labels == 0]
                elif self.regime == "TREND":
                    feats = feats[labels == 1]
                elif self.regime == "CHAOS":
                    feats = feats[labels == 2]
            
            if feats.empty or len(feats) < 50:
                return {"error": f"Too few rows after filtering for regime {self.regime}"}

            # NEW: Strict Time-Barrier Target + Smart Magnitude Weights
            H = TARGET_HORIZON_CANDLES
            
            # Use pandas directly for vectorized operations
            closes_s = pd.Series([cl["close"] for cl in candles])
            future_close = closes_s.shift(-H)
            
            # Binary target: strictly Higher(1) or Lower/Equal(0)
            target_raw = (future_close > closes_s).astype(int).values
            
            # Calculate Absolute Return for Magnitude Weighting
            abs_return = np.abs(np.log(future_close / closes_s))
            local_vol = abs_return.ewm(span=1000, min_periods=1).mean()
            magnitude_weight = abs_return / (local_vol + 1e-8)
            magnitude_weight = np.clip(magnitude_weight, 0.1, 5.0).fillna(1.0).values
            
            # Calculate Time Decay Weight
            try:
                # Get the last timestamp (youngest) in the dataset
                if "openTime" in candles[-1] and candles[-1]["openTime"]:
                    timestamps_raw = pd.Series([cl.get("openTime", 0) for cl in candles])
                    timestamps = _parse_timestamps_vectorized(timestamps_raw)
                    max_time = timestamps.iloc[-1]
                    age_hours = (max_time - timestamps).dt.total_seconds() / 3600.0
                else:
                    age_hours = np.linspace(len(candles), 0, len(candles)) / 60.0 # fallback
                
                # FIX: Adaptive half-life based on dataset size to prevent "amnesia"
                # Set half-life to 1/3 of the total dataset span, but at least 24h
                max_age_h = age_hours.max() if len(age_hours) > 0 else 24.0
                half_life_hours = max(24.0, max_age_h / 3.0)
                
                decay_rate = np.log(2) / half_life_hours
                time_weight = np.exp(-decay_rate * age_hours)
                
                # Prevent weight from dropping below 0.1 so historical patterns are still learned
                time_weight = np.clip(time_weight, 0.1, 1.0)
            except Exception as e:
                log.warning(f"Failed to calc true time weights, falling back to linspace: {e}")
                power = np.linspace(0, 1, len(candles))
                time_weight = 0.1 * np.power(10.0, power)
                
            final_weights = time_weight * magnitude_weight
            final_weights = final_weights / (np.mean(final_weights) + 1e-8) # normalize
            
            # Align features
            feat_indices = feats.index.values
            valid_mask = feat_indices < (len(closes_s) - H)  # drop last H rows
            feat_indices_valid = feat_indices[valid_mask]
            
            feats = feats.loc[feat_indices_valid]
            target_aligned = target_raw[feat_indices_valid]
            weights_aligned = final_weights[feat_indices_valid]

            # ── D12: Contextual Embeddings ────────────────────────────────
            # Fit a PCA compressor on the higher-TF (H1) feature state and append
            # per-row H1 context vectors (ctx_emb_*) aligned by timestamp
            # (merge_asof semantics — no look-ahead). Fail-open: if MTF data is
            # missing/thin, the model trains on base features exactly as before.
            fitted_embedder: Optional[ContextualEmbedder] = None
            if mtf_candles is not None and len(mtf_candles) >= CONTEXT_EMBEDDING_MIN_MTF_ROWS:
                try:
                    mtf_tail = mtf_candles[-3000:]  # cap for fit latency
                    mtf_feats_full = build_features(mtf_tail, None)
                    if len(mtf_feats_full) >= CONTEXT_EMBEDDING_MIN_MTF_ROWS:
                        candidate = ContextualEmbedder()
                        if candidate.fit(mtf_feats_full):
                            main_times = [candles[int(i)].get("openTime", 0)
                                          for i in feats.index.values]
                            # mtf_feats_full rows are a warmup-sliced subset of
                            # mtf_tail: use the preserved index LABELS (original
                            # positions) to fetch each feat row's own timestamp.
                            # This keeps mtf_times 1:1 with mtf_feats_full rows —
                            # passing all raw mtf times would misalign by the
                            # warmup offset and index out of bounds.
                            mtf_row_positions = mtf_feats_full.index.values
                            mtf_times = [mtf_tail[int(p)].get("openTime", 0)
                                         for p in mtf_row_positions]
                            emb_matrix = candidate.transform_aligned(
                                main_times, mtf_times, mtf_feats_full)
                            for k_idx in range(emb_matrix.shape[1]):
                                feats[f"ctx_emb_{k_idx}"] = emb_matrix[:, k_idx]
                            fitted_embedder = candidate
                            log.info(f"[ContextEmbed] {self._key}: appended "
                                     f"{emb_matrix.shape[1]} context dims to "
                                     f"{len(feats)} training rows")
                except Exception as ctx_ex:
                    log.warning(f"[ContextEmbed] {self._key}: training-time "
                                f"embedding skipped: {ctx_ex}")
                    fitted_embedder = None
            # ── End D12 ───────────────────────────────────────────────────

            X = feats.values.astype(np.float32)
            y = target_aligned.copy()  # copy so RL can modify labels safely
            base_weights = weights_aligned.copy()

            # Log class balance
            if len(y) > 0:
                buy_pct = (np.sum(y) / len(y)) * 100.0
                put_pct = 100.0 - buy_pct
                log.info(f"[{self._key}] Training on {len(y)} candles. Class balance: BUY {buy_pct:.1f}% | PUT {put_pct:.1f}%")

            # --- Bug1 fix: Online RL Integration — match by timestamp (not price) ---
            # FIX (found while wiring B6 variance labels): base_weights is a pandas
            # Series carrying ORIGINAL candle labels (warmup rows 0-24 missing), but
            # TimeSeriesSplit yields POSITIONAL indices. Series[pos_array] does
            # label-lookup in pandas 2.x -> KeyError whenever candles have parsable
            # openTime (i.e. the normal production path — retraining was silently
            # failing). np.asarray makes indexing positional, matching y/val_idx.
            sample_weights = np.asarray(base_weights, dtype=np.float32)
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
                    used_fb_ids = set()
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

                        # FIX LABEL SMEARING: Match window must be strict (0.9x tf) and 1:1.
                        tf_seconds = {"s5": 5, "s10": 10, "s15": 15, "s30": 30, "1m": 60, "m1": 60, "5m": 300, "m5": 300}.get(self.interval, 60)
                        max_diff = tf_seconds * 0.9  # Strictly match only the closest candle
                        
                        if best_fb and best_diff < max_diff:
                            # Ensure this feedback is only consumed ONCE (ArgMin by distance prevents cloning)
                            if id(best_fb) not in used_fb_ids:
                                used_fb_ids.add(id(best_fb))
                                match_count += 1
                                sample_weights[i] = 5.0  # x5 weight
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
            # B6: collect out-of-fold predictions to train the variance model.
            # Each row appears in exactly one val fold, so concatenated OOF rows
            # cover (nearly) the full training set with HONEST out-of-sample
            # errors: |fold_model_prob - actual|. This avoids the optimism bias
            # of training variance on in-sample errors of the final model
            # (which memorized the training data and looks falsely certain).
            oof_feat_parts, oof_err_parts = [], []
            oof_probs, oof_y = [], []

            for train_idx, val_idx in tscv.split(X):
                X_tr, X_val = X[train_idx], X[val_idx]
                y_tr, y_val = y[train_idx], y[val_idx]

                m = lgb.LGBMClassifier(**get_lgbm_params(self.interval))
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
                # B6: val_idx is positional into X == feats.values, so
                # feats.iloc[val_idx] recovers the matching feature rows.
                oof_feat_parts.append(feats.iloc[val_idx])
                oof_err_parts.append(np.abs(probs - y_val))
                oof_probs.append(probs)
                oof_y.append(y_val)

            avg_acc = float(np.mean(val_accs))
            avg_auc = float(np.mean(val_aucs))

            # Train probability calibrator on OOF predictions
            calibrator = None
            if len(oof_probs) > 0:
                try:
                    oof_probs_all = np.concatenate(oof_probs)
                    oof_y_all = np.concatenate(oof_y)
                    calibrator = IsotonicRegression(out_of_bounds='clip')
                    calibrator.fit(oof_probs_all, oof_y_all)
                except Exception as e:
                    log.warning(f"Probability calibration failed: {e}")

            # Final model on all data
            final_model = lgb.LGBMClassifier(**get_lgbm_params(self.interval))
            final_model.fit(X, y, sample_weight=sample_weights)

            version = f"lgbm-v1-{self._key}-{int(time.time())}"
            meta = ModelMeta(
                accuracy=avg_acc,
                auc=avg_auc,
                n_train=len(X),
                trained_at=time.time(),
                version=version,
                calibrator=calibrator
            )

            if _challenger_mode:
                # D11: Challenger path — never touches production .pkl or self._model.
                # Skips the quality gate too: the live shadow A/B test (statistical
                # z-test on real trade outcomes) is a stricter and more meaningful
                # gate than a single validation-split accuracy comparison.
                # D12: challenger carries its OWN embedder (embedding space is
                # versioned with the model it was trained with).
                with self._challenger_lock:
                    self._challenger_model = final_model
                    self._challenger_meta = meta
                    self._challenger_embedder = fitted_embedder
                    tmp = self._challenger_path.with_suffix(".tmp")
                    joblib.dump({"model": final_model, "meta": meta,
                                 "embedder": fitted_embedder}, tmp)
                    os.replace(tmp, self._challenger_path)

                report = {
                    "symbol": self.symbol, "interval": self.interval,
                    "n_train": len(X), "accuracy": round(avg_acc, 4),
                    "auc": round(avg_auc, 4), "version": version,
                    "challenger": True,
                    "has_context_embedder": fitted_embedder is not None and fitted_embedder.is_fitted,
                }
                log.info(f"[ShadowChallenger] {self._key}: challenger trained (not deployed): {report}")
                return report

            self._save(final_model, meta, embedder=fitted_embedder)

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
                # D12: keep in-memory embedder consistent with the deployed model
                # (only assigned when the quality gate passes, same as model/meta).
                self._embedder = fitted_embedder

            # ── B6: train the variance model on OOF calibration errors ──────
            # Runs ONLY in the production deploy path (gate passed): the variance
            # model must describe the LIVE model. Challenger training must NOT
            # touch it (challenger has different errors; overwriting would corrupt
            # live confidence dampening). OOF errors come from fold-models, not
            # the final model — they slightly OVERESTIMATE true error (fold models
            # see less data), which makes dampening conservative. Safe direction.
            variance_trained = False
            try:
                if oof_feat_parts and oof_err_parts:
                    oof_feats_all = pd.concat(oof_feat_parts, ignore_index=True)
                    oof_err_all = np.concatenate(oof_err_parts)
                    log.info(f"[Variance] {self._key}: OOF error stats over "
                             f"{len(oof_err_all)} samples: mean={oof_err_all.mean():.4f} "
                             f"std={oof_err_all.std():.4f} "
                             f"(mean error = expected miscalibration of main model)")
                    var_model = VariancePredictor(self.symbol, self.interval, self.regime)
                    var_report = var_model.train(oof_feats_all, oof_err_all)
                    if "error" not in var_report:
                        variance_trained = True
                        log.info(f"[Variance] {self._key}: variance model trained: {var_report}")
                    else:
                        log.warning(f"[Variance] {self._key}: variance training skipped: {var_report}")
            except Exception as var_ex:
                # Fail-open: main model deploys regardless; /predict falls back
                # to the uninformative prior (0.35) when no variance model exists.
                log.warning(f"[Variance] {self._key}: auto-training failed (non-fatal): {var_ex}")
            # ── End B6 ──────────────────────────────────────────────────────

            report = {
                "symbol": self.symbol,
                "interval": self.interval,
                "n_train": len(X),
                "accuracy": round(avg_acc, 4),
                "auc": round(avg_auc, 4),
                "version": version,
                "has_context_embedder": fitted_embedder is not None and fitted_embedder.is_fitted,
                "variance_trained": variance_trained,
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
            embedder = self._embedder
        if meta is None:
            return {"status": "not-trained", "key": self._key}
        return {
            "key": self._key,
            "accuracy": meta.accuracy,
            "auc": meta.auc,
            "n_train": meta.n_train,
            "version": meta.version,
            "age_hours": round((time.time() - meta.trained_at) / 3600, 1),
            "has_context_embedder": embedder is not None and getattr(embedder, "is_fitted", False),
        }

    # в”Ђв”Ђ Internal helpers в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    def _model_path(self) -> Path:
        return MODEL_DIR / f"{self._key}.pkl"

    def _save(self, model, meta: ModelMeta, embedder: Optional[ContextualEmbedder] = None):
        # FIX C-09: write atomically via tmp + os.replace, same pattern as SGD.
        # Old code wrote directly to the target file — a crash during joblib.dump
        # would leave a corrupted pkl that permanently breaks model loading.
        # D12: the contextual embedder is versioned in the SAME artifact so model
        # and embedding space can never drift apart (incl. challenger promotion).
        p = self._model_path()
        tmp = p.with_suffix(".tmp")
        joblib.dump({"model": model, "meta": meta,
                     "embedder": embedder if embedder is not None else self._embedder}, tmp)
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
                    # D12: old artifacts have no "embedder" key -> None (backward compat,
                    # predict() falls back to base features when dims don't match).
                    self._embedder = data.get("embedder")
                log.info(f"[Load] Loaded LightGBM from {p}" +
                         (" (+context embedder)" if self._embedder is not None and
                          getattr(self._embedder, "is_fitted", False) else ""))
            except Exception as e:
                # FIX W-10: corrupted pkl was silently ignored on every restart.
                # Delete it so next retrain produces a clean file.
                log.warning(f"[Load] Failed to load LightGBM {p}: {e}. Deleting corrupt file.")
                try:
                    p.unlink(missing_ok=True)
                except Exception:
                    pass

        self.tactician.load()


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


# ── Predictive Variance Model ─────────────────────────────────────────────
# Second LightGBM model trained to predict prediction variance/confidence.
# Outputs: variance estimate (0-1), helps calibrate confidence intervals.
# Trained on same data as main model but with different objective (regression on
# |confidence| rather than classification).
# Fusion with main model: final_confidence = main_confidence * (1 - variance_estimate)
# ---------------------------------------------------------------

VARIANCE_MODEL_DIR = MODEL_DIR / "variance"


class VariancePredictor:
    """
    LightGBM regressor for predicting prediction uncertainty/variance.
    Takes same features as main model but predicts continuous [0,1] variance.
    """

    def __init__(self, symbol: str, interval: str, regime: str = "ALL"):
        self.symbol = symbol.upper()
        self.interval = interval.lower()
        self.regime = regime.upper()
        if self.regime == "ALL":
            self._key = f"{self.symbol}_{self.interval}"
        else:
            self._key = f"{self.symbol}_{self.interval}_{self.regime}"
        self._model: Optional["lgb.LGBMRegressor"] = None
        self._lock = threading.Lock()
        VARIANCE_MODEL_DIR.mkdir(parents=True, exist_ok=True)
        self._model_path = VARIANCE_MODEL_DIR / f"{self._key}_variance.pkl"
        self._try_load()

    def _try_load(self):
        if self._model_path.exists():
            try:
                self._model = joblib.load(self._model_path)
                log.info(f"[VariancePredictor] Loaded model for {self._key}")
            except Exception as e:
                log.warning(f"[VariancePredictor] Failed to load {self._model_path}: {e}")

    def _extract_features(self, feats: pd.DataFrame) -> np.ndarray:
        # Use a stable subset of features that generalizes across regimes
        candidate_cols = [
            'micro_volatility_z', 'micro_inertia_15', 'atr_norm',
            'of_delta_ratio', 'of_block_trade',
            'body_ratio', 'upper_wick', 'lower_wick',
            'rsi14', 'rsi7', 'hurst', 'rolling_std', 'adx'
        ]
        cols = [c for c in candidate_cols if c in feats.columns]
        if not cols:
            return np.zeros((len(feats), 1), dtype=np.float32)
        df_sub = feats[cols].copy()
        return df_sub.fillna(0.0).values.astype(np.float32)

    def train(self, feats: pd.DataFrame, target_confidence_error: np.ndarray) -> dict:
        """
        Train the variance model to predict |predicted_prob - actual_outcome|
        (i.e. how wrong the main model tends to be under these feature conditions).
        target_confidence_error: array same length as feats, values in [0, 1]
        representing the historical absolute calibration error of the main model
        for similar feature states.
        """
        X = self._extract_features(feats)
        if len(X) < 200:
            return {"error": f"Not enough samples for variance model ({len(X)} < 200)"}

        y = np.clip(target_confidence_error, 0.0, 1.0)

        with self._lock:
            self._model = lgb.LGBMRegressor(
                objective="regression",
                n_estimators=150,
                learning_rate=0.05,
                max_depth=4,
                num_leaves=15,
                min_child_samples=20,
                subsample=0.8,
                colsample_bytree=0.8,
                random_state=42,
                verbose=-1,
            )
            self._model.fit(X, y)

            tmp = self._model_path.with_suffix(".tmp")
            joblib.dump(self._model, tmp)
            os.replace(tmp, self._model_path)

        return {"symbol": self.symbol, "interval": self.interval, "n_train": len(X)}

    def predict_variance_batch(self, feats: pd.DataFrame) -> np.ndarray:
        """Returns estimated calibration error / uncertainty in [0,1] for each row."""
        if self._model is None:
            # Uninformative prior: moderate uncertainty until trained
            return np.full(len(feats), 0.35, dtype=np.float32)
        X = self._extract_features(feats)
        if len(X) == 0:
            return np.array([], dtype=np.float32)
        try:
            preds = self._model.predict(X)
            return np.clip(preds, 0.0, 1.0)
        except Exception as e:
            log.warning(f"[VariancePredictor] predict failed: {e}")
            return np.full(len(feats), 0.35, dtype=np.float32)

    def predict_variance(self, candles: List[Dict], mtf_candles: Optional[List[Dict]] = None) -> float:
        """Predict variance/uncertainty for the latest candle in the series."""
        feats = build_features(candles, mtf_candles)
        if feats.empty:
            return 0.35
        var_pred = self.predict_variance_batch(feats)
        return float(var_pred[-1]) if len(var_pred) > 0 else 0.35


# Global registry for variance predictors (mirrors _predictors in main.py)
_variance_predictors: Dict[str, VariancePredictor] = {}
_variance_registry_lock = threading.Lock()


def get_variance_predictor(symbol: str, interval: str, regime: str = "ALL") -> VariancePredictor:
    regime_u = regime.upper()
    key = f"{symbol.upper()}_{interval.lower()}" if regime_u == "ALL" else f"{symbol.upper()}_{interval.lower()}_{regime_u}"
    with _variance_registry_lock:
        if key not in _variance_predictors:
            _variance_predictors[key] = VariancePredictor(symbol, interval, regime)
        return _variance_predictors[key]


# ── End Predictive Variance Model ─────────────────────────────────────────


# ── D12: Contextual Embeddings ─────────────────────────────────────────────
# Compresses the full higher-timeframe (H1) market state into a compact dense
# vector that is appended as extra features to the sub-minute (s5) model.
#
# Motivation: the existing MTF integration passes only 2 scalars from the
# higher TF (mtf_rsi, mtf_trend). The H1 feature matrix actually contains ~48
# informative dimensions (trend, volatility regime, order flow, entropy...).
# A PCA-based embedder ("poor man's autoencoder" — linear, no extra deps
# beyond sklearn which is already required) learns the principal axes of H1
# state variation during training and projects the live H1 state onto them
# at inference, giving the s5 model a compact long-term context.
#
# Design guarantees:
#   * Backward compatible: old .pkl files have no embedder -> predict() falls
#     back to base features when dimensions don't match the loaded model.
#   * Train/predict symmetry: the same ctx_emb_* columns are appended in both
#     paths; the embedder object is versioned together with the model in one
#     .pkl artifact (incl. challenger path for D11 promotion).
#   * Stateless-safe: constant columns are dropped at fit; missing columns at
#     transform are zero-filled; non-finite values are sanitized.
# ---------------------------------------------------------------

CONTEXT_EMBEDDING_DIM = int(os.environ.get("CONTEXT_EMBEDDING_DIM", "8"))
CONTEXT_EMBEDDING_MIN_MTF_ROWS = 60


class ContextualEmbedder:
    """PCA-based compressor of higher-timeframe feature state."""

    def __init__(self, n_components: int = CONTEXT_EMBEDDING_DIM):
        self.n_components = n_components
        self._pca = None
        self._columns: List[str] = []
        self._n_features = 0

    @property
    def dim(self) -> int:
        if self._pca is not None:
            return int(self._pca.n_components_)
        return 0

    @property
    def is_fitted(self) -> bool:
        return self._pca is not None

    def _sanitize(self, feats: pd.DataFrame) -> pd.DataFrame:
        df = feats.copy()
        if self._columns:
            for c in self._columns:
                if c not in df.columns:
                    df[c] = 0.0
            df = df[self._columns]
        df = df.replace([np.inf, -np.inf], np.nan).fillna(0.0)
        return df.astype(np.float32)

    def fit(self, mtf_feats: pd.DataFrame) -> bool:
        """Fit PCA on H1/MTF feature matrix. Returns True on success."""
        if not HAS_LGBM or mtf_feats is None or len(mtf_feats) < CONTEXT_EMBEDDING_MIN_MTF_ROWS:
            return False
        try:
            df = mtf_feats.replace([np.inf, -np.inf], np.nan).fillna(0.0)
            # Drop near-constant columns (carry no information, break PCA scaling)
            nunique = df.nunique()
            keep = [c for c in df.columns if nunique.get(c, 0) > 1]
            if len(keep) < 2:
                return False
            df = df[keep]
            k = min(self.n_components, len(df) - 1, len(keep))
            if k < 1:
                return False
            self._pca = PCA(n_components=k, random_state=42)
            self._pca.fit(df.values.astype(np.float32))
            self._columns = keep
            self._n_features = len(keep)
            explained = float(np.sum(self._pca.explained_variance_ratio_))
            log.info(f"[ContextEmbed] Fitted PCA: {len(keep)} -> {k} dims, "
                     f"explained_variance={explained:.3f}")
            return True
        except Exception as e:
            log.warning(f"[ContextEmbed] fit failed: {e}")
            self._pca = None
            self._columns = []
            return False

    def transform_latest(self, mtf_feats: pd.DataFrame) -> np.ndarray:
        """Project the latest H1/MTF state row to embedding space.

        Returns zeros of fitted dim when MTF data is missing (fail-open:
        model still predicts, just without long-term context).
        """
        k = self.dim
        if k == 0 or mtf_feats is None or mtf_feats.empty:
            return np.zeros(max(k, 0), dtype=np.float32)
        try:
            row = self._sanitize(mtf_feats.iloc[[-1]])
            emb = self._pca.transform(row.values.astype(np.float32))
            return np.asarray(emb[0], dtype=np.float32)
        except Exception as e:
            log.warning(f"[ContextEmbed] transform failed: {e}")
            return np.zeros(k, dtype=np.float32)

    def transform_aligned(self, main_times: list, mtf_times: list,
                           mtf_feats: pd.DataFrame) -> np.ndarray:
        """Per-row H1 embeddings aligned to main-TF candle times.

        For each main-candle timestamp, uses the latest MTF row with
        open_time <= main time (same merge_asof semantics as the existing MTF
        feature merge in features.py — no look-ahead). Rows with no prior MTF
        state get a zero vector.
        """
        k = self.dim
        n = len(main_times)
        if k == 0 or n == 0 or mtf_feats is None or mtf_feats.empty:
            return np.zeros((n, max(k, 1)), dtype=np.float32)
        try:
            mtf_ts = np.array([_to_epoch_seconds(t) for t in mtf_times], dtype=np.float64)
            order = np.argsort(mtf_ts)
            mtf_ts_sorted = mtf_ts[order]
            
            # FIX D12: Look-Ahead Bias protection.
            # MTF features are computed using the candle's close, which happens at openTime + interval.
            # We must shift the alignment timestamps forward by the MTF interval.
            if len(mtf_ts_sorted) > 1:
                mtf_interval = np.median(np.diff(mtf_ts_sorted))
                if mtf_interval > 0:
                    mtf_ts_sorted += mtf_interval
                    
            # Precompute embeddings for all MTF rows once
            clean = self._sanitize(mtf_feats)
            all_emb = self._pca.transform(clean.values.astype(np.float32))
            all_emb = all_emb[order]  # align with sorted times

            out = np.zeros((n, k), dtype=np.float32)
            for i, t in enumerate(main_times):
                ts = _to_epoch_seconds(t)
                if not np.isfinite(ts):
                    continue
                j = int(np.searchsorted(mtf_ts_sorted, ts, side="right")) - 1
                if j >= 0:
                    out[i] = all_emb[j]
            return out
        except Exception as e:
            log.warning(f"[ContextEmbed] aligned transform failed: {e}")
            return np.zeros((n, max(k, 1)), dtype=np.float32)


def _to_epoch_seconds(ts) -> float:
    """Parse openTime (unix int/float or ISO string) to epoch seconds. NaN if unparsable."""
    try:
        if ts is None:
            return float("nan")
        if isinstance(ts, (int, float)):
            v = float(ts)
            if v > 1e11:  # milliseconds
                v /= 1000.0
            return v
        s = str(ts).strip()
        if not s:
            return float("nan")
        if s.replace(".", "", 1).lstrip("-").isdigit():
            v = float(s)
            if v > 1e11:
                v /= 1000.0
            return v
        dt = _parse_timestamps_vectorized(pd.Series([ts])).iloc[0]
        if dt is None or pd.isna(dt):
            return float("nan")
        return float(dt.timestamp())
    except Exception:
        return float("nan")


def context_embedding_column_names(dim: int) -> List[str]:
    return [f"ctx_emb_{i}" for i in range(dim)]


# ── End Contextual Embeddings ─────────────────────────────────────────────








