"""
patch_tst_predictor.py - Live inference wrapper for DualStreamPatchBrain (PatchTST-Lite).

Runs calibrated sequence inference:
1. Prepares micro-candles (160 timesteps) & MTF candles (30 timesteps).
2. Extracts 23-element macro context vector (DXY momentum, day anchors, spread, SMC tags).
3. Executes DualStreamPatchBrain forward pass.
4. Produces direction (BUY/PUT) and honest calibrated probability [50%..65%].
"""

import os
import math
import logging
from typing import Optional, List, Dict, Tuple
from pathlib import Path
import numpy as np

log = logging.getLogger("patch-tst-predictor")

_MODEL_PATH = Path(__file__).parent / "patch_tst_brain_v3.pt"
_FALLBACK_MODEL_PATH = Path(__file__).parent / "sequence_brain_v3.pt"

ASSET_MAP = {
    "EURUSD": 0, "GBPUSD": 1, "USDJPY": 2, "AUDUSD": 3,
    "USDCAD": 4, "USDCHF": 5, "EURJPY": 6, "GBPJPY": 7,
    "BTCUSD": 8, "ETHUSD": 9
}

TF_SECONDS = {
    "s5": 5, "s10": 10, "s15": 15, "s30": 30,
    "m1": 60, "1m": 60, "m5": 300, "5m": 300, "m15": 900, "15m": 900
}


def normalize_candles_live(candles: List[Dict], seq_len: int = 160) -> np.ndarray:
    """
    Normalizes raw OHLCV candles to relative returns matching training distribution.
    Features: [(close-open)/open, (high-open)/open, (low-open)/open, (close-prev)/prev, log1p(vol)/10]
    """
    arr = np.zeros((seq_len, 5), dtype=np.float32)
    if not candles:
        return arr

    tail = candles[-seq_len:] if len(candles) >= seq_len else candles
    offset = seq_len - len(tail)

    prev_close = None
    for i, c in enumerate(tail):
        idx = offset + i
        op = float(c.get("open", 1.0))
        hi = float(c.get("high", op))
        lo = float(c.get("low", op))
        cl = float(c.get("close", op))
        vol = float(c.get("volume", 1.0))

        if op <= 0:
            op = 1e-5

        arr[idx, 0] = (cl - op) / op
        arr[idx, 1] = (hi - op) / op
        arr[idx, 2] = (lo - op) / op

        if prev_close is not None and prev_close > 0:
            arr[idx, 3] = (cl - prev_close) / prev_close
        else:
            arr[idx, 3] = 0.0

        arr[idx, 4] = math.log1p(max(0.0, vol)) / 10.0
        prev_close = cl

    return arr


def build_macro_vector_live(
    symbol: str,
    interval: str,
    candles: List[Dict],
    macro_data: Optional[Dict] = None,
    smc_data: Optional[Dict] = None
) -> np.ndarray:
    """
    Builds the 23-element macro context vector matching training features.
    """
    macro = macro_data or {}
    smc = smc_data or {}

    day_pos = float(macro.get("DayRangePositionPct", 0.5))
    dist_high = float(macro.get("DistToDayHighBps", 0.0)) / 100.0
    dist_low = float(macro.get("DistToDayLowBps", 0.0)) / 100.0
    dxy_1m = float(macro.get("DxyMomentum1mBps", 0.0)) / 10.0
    dxy_5m = float(macro.get("DxyMomentum5mBps", 0.0)) / 10.0
    basket_sync = float(macro.get("BasketSyncScore", 0.0))
    spread = float(macro.get("SpreadBps", 1.0)) / 10.0
    asian_high = float(macro.get("DistToAsianHighBps", 0.0)) / 100.0
    asian_low = float(macro.get("DistToAsianLowBps", 0.0)) / 100.0

    # Real-time indicators from candles if available
    pe = float(macro.get("PriceEntropy", 0.5))
    adx = float(macro.get("Adx", 25.0)) / 100.0
    rsi = (float(macro.get("Rsi", 50.0)) - 50.0) / 50.0
    news_m = float(macro.get("MinutesToNews", 120.0))
    news_norm = min(120.0, max(0.0, news_m)) / 120.0

    hr = float(macro.get("HourUtc", 12.0))
    dow = float(macro.get("DayOfWeek", 2.0))
    hr_sin = math.sin(2 * math.pi * hr / 24.0)
    hr_cos = math.cos(2 * math.pi * hr / 24.0)
    dow_sin = math.sin(2 * math.pi * dow / 7.0)
    dow_cos = math.cos(2 * math.pi * dow / 7.0)

    tf_str = str(interval).lower()
    tf_sec = float(TF_SECONDS.get(tf_str, 5)) / 60.0

    has_ob = 1.0 if (smc.get("smc_has_ob") or smc.get("HasOrderBlock")) else 0.0
    has_fvg = 1.0 if (smc.get("smc_has_fvg") or smc.get("HasFvg")) else 0.0
    has_swp = 1.0 if (smc.get("smc_has_swp") or smc.get("HasLiquiditySweep")) else 0.0
    has_bos = 1.0 if (smc.get("smc_bos_dir", "NONE") not in ("NONE", "") or smc.get("HasBos")) else 0.0

    asset_clean = symbol.upper().replace("/", "").replace("_", "")
    asset_id = float(ASSET_MAP.get(asset_clean, 6.0))

    vec = np.array([
        day_pos, dist_high, dist_low,
        dxy_1m, dxy_5m, basket_sync, spread,
        asian_high, asian_low,
        pe, adx, rsi, news_norm,
        hr_sin, hr_cos, dow_sin, dow_cos,
        tf_sec,
        has_ob, has_fvg, has_swp, has_bos,
        asset_id
    ], dtype=np.float32)

    return vec


class PatchTSTPredictor:
    """
    Inference engine for DualStreamPatchBrain.
    """
    _instance: Optional["PatchTSTPredictor"] = None

    def __init__(self):
        self.model = None
        self.device = "cpu"
        self._load_model()

    @classmethod
    def get_instance(cls) -> "PatchTSTPredictor":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def _load_model(self):
        try:
            import torch
            from models.patch_tst_brain import DualStreamPatchBrain

            self.device = torch.device("cpu")
            model_path = _MODEL_PATH if _MODEL_PATH.exists() else _FALLBACK_MODEL_PATH
            if not model_path.exists():
                log.warning(f"[PatchTSTPredictor] Model file not found at {model_path}. Neural brain offline.")
                return

            checkpoint = torch.load(model_path, map_location=self.device, weights_only=False)
            macro_dim = checkpoint.get("macro_features", 23)

            model = DualStreamPatchBrain(
                candle_features=5,
                seq_len_micro=160,
                seq_len_mtf=30,
                macro_features=macro_dim,
                d_model=64,
                nhead=4,
                num_layers=2,
                num_classes=3,
                dropout=0.0
            )

            state_dict = checkpoint["model_state_dict"] if "model_state_dict" in checkpoint else checkpoint
            model.load_state_dict(state_dict)
            model.eval()

            self.model = model
            val_acc = checkpoint.get("val_acc", 0.0)
            log.info(f"[PatchTSTPredictor] Loaded model from {model_path.name} (Val Acc: {val_acc:.2f}%)")
        except Exception as e:
            log.error(f"[PatchTSTPredictor] Failed to load PyTorch model: {e}")
            self.model = None

    def is_available(self) -> bool:
        return self.model is not None

    def predict(
        self,
        symbol: str,
        interval: str,
        candles: List[Dict],
        mtf_candles: Optional[List[Dict]] = None,
        macro_context: Optional[Dict] = None,
        smc_context: Optional[Dict] = None
    ) -> Optional[Tuple[str, float, str, int, float]]:
        """
        Runs neural inference.
        Returns (direction, calibrated_confidence, model_version, horizon_candles, raw_confidence)
        or None if model is unavailable.
        """
        if self.model is None or not candles:
            return None

        try:
            import torch
            import torch.nn.functional as F

            x_micro_np = normalize_candles_live(candles, seq_len=160)
            x_mtf_np = normalize_candles_live(mtf_candles or [], seq_len=30)
            x_macro_np = build_macro_vector_live(symbol, interval, candles, macro_context, smc_context)

            x_micro_t = torch.tensor(x_micro_np, dtype=torch.float32).unsqueeze(0).to(self.device)
            x_mtf_t = torch.tensor(x_mtf_np, dtype=torch.float32).unsqueeze(0).to(self.device)
            x_macro_t = torch.tensor(x_macro_np, dtype=torch.float32).unsqueeze(0).to(self.device)

            with torch.no_grad():
                logits, _ = self.model(x_micro_t, x_mtf_t, x_macro_t)
                probs = F.softmax(logits, dim=-1)[0].cpu().numpy()

            p_hold = float(probs[0])
            p_buy = float(probs[1])
            p_put = float(probs[2])

            # 1. Determine direction
            if p_buy >= p_put:
                direction = "BUY"
                denom = p_buy + p_put
                p_dir = p_buy / denom if denom > 0 else 0.5
            else:
                direction = "PUT"
                denom = p_buy + p_put
                p_dir = p_put / denom if denom > 0 else 0.5

            # 2. Honest Statistical Calibration
            # Dampen directional edge by market chop/noise probability
            chop_factor = max(0.15, 1.0 - p_hold * 0.70)
            calibrated_prob = 0.50 + (p_dir - 0.50) * chop_factor

            # Bound strictly between 50% and 65% for binary options realism
            calibrated_prob = round(float(np.clip(calibrated_prob, 0.50, 0.65)), 4)
            raw_prob = round(float(p_dir), 4)

            log.info(f"[PatchTST Brain] {symbol} {interval} -> {direction} | Calibrated: {calibrated_prob*100:.1f}% | Raw: {raw_prob*100:.1f}% | HOLD: {p_hold*100:.1f}%")

            return direction, calibrated_prob, "patch_tst_v3", 3, raw_prob

        except Exception as e:
            log.error(f"[PatchTST Brain] Inference error: {e}", exc_info=True)
            return None
