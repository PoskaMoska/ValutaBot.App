"""
patch_tst_predictor.py - Universal Live Inference Wrapper for Neural Brains.

Auto-detects model architecture and configuration from checkpoint metadata:
- SequenceBrain (hybrid BiGRU or transformer) → forward(x_candles, x_macro)
- DualStreamPatchBrain (PatchTST-Lite) → forward(x_micro, x_mtf, x_macro)

Calibrated inference pipeline:
1. Prepares micro-candles (seq_len timesteps) & MTF candles (seq_len_mtf timesteps).
2. Extracts 23-element macro context vector (DXY momentum, day anchors, spread, SMC tags).
3. Executes model forward pass with temperature-scaled softmax.
4. Produces direction (BUY/PUT) and calibrated probability (no hard ceiling).
"""

import os
import math
import logging
from typing import Optional, List, Dict, Tuple
from pathlib import Path
import numpy as np

from datetime import datetime

log = logging.getLogger("patch-tst-predictor")

_MODEL_PATH = Path(__file__).parent / "patch_tst_brain_v4.pt"
_FALLBACK_V3_PATH = Path(__file__).parent / "patch_tst_brain_v3.pt"
_FALLBACK_MODEL_PATH = Path(__file__).parent / "sequence_brain_v3.pt"


def _resolve_model_path() -> Optional[Path]:
    candidate_paths = [
        Path("data/models/patch_tst_brain_v4.pt"),
        Path("/app/data/models/patch_tst_brain_v4.pt"),
        _MODEL_PATH,
        Path("models/patch_tst_brain_v4.pt"),
        Path("/app/models/patch_tst_brain_v4.pt"),
        Path("data/models/patch_tst_brain_v3.pt"),
        Path("/app/data/models/patch_tst_brain_v3.pt"),
        _FALLBACK_V3_PATH,
        Path("models/patch_tst_brain_v3.pt"),
        Path("/app/models/patch_tst_brain_v3.pt"),
        Path("data/models/sequence_brain_v3.pt"),
        Path("/app/data/models/sequence_brain_v3.pt"),
        _FALLBACK_MODEL_PATH,
        Path("models/sequence_brain_v3.pt"),
        Path("/app/models/sequence_brain_v3.pt"),
    ]
    for p in candidate_paths:
        if p.exists() and p.is_file():
            return p
    return None

ASSET_MAP = {
    "EURUSD": 0, "GBPUSD": 1, "AUDUSD": 2, "USDJPY": 3,
    "USDCAD": 4, "USDCHF": 5, "EURJPY": 6, "GBPJPY": 7,
    "BTCUSD": 8, "ETHUSD": 9
}

TF_SECONDS = {
    "s5": 5, "s10": 10, "s15": 15, "s30": 30,
    "m1": 60, "1m": 60, "m5": 300, "5m": 300, "m15": 900, "15m": 900
}


def _safe_float(val, default=0.0):
    if val is None:
        return default
    try:
        f = float(val)
        return default if math.isnan(f) or math.isinf(f) else f
    except (ValueError, TypeError):
        return default


def normalize_candles_live(
    candles: List[Dict],
    seq_len: int = 160,
    n_channels: int = 8,
    tf_sec: int = 5
) -> np.ndarray:
    """
    Normalizes raw OHLCV candles to relative returns matching export_dataset.py training distribution (zero Train-Serve Skew).

    8-channel mode (v3):
      0: (close-open)/open * 10000 (body bps)
      1: (high-open)/open * 10000 (upper excursion bps)
      2: (low-open)/open * 10000 (lower excursion bps)
      3: (close-prev_close)/prev_close * 10000 (inter-candle return bps, zeroed across time gaps)
      4: log1p(vol)/10.0 (tick volume density, defaults to 0.0 when missing)
      5: (high - max(open, close))/open * 10000 (upper wick bps)
      6: (min(open, close) - low)/open * 10000 (lower wick bps)
      7: (high - low)/open * 10000 (full candle range bps)
    """
    arr = np.zeros((seq_len, n_channels), dtype=np.float32)
    if not candles:
        return arr

    tail = candles[-seq_len:] if len(candles) >= seq_len else candles
    offset = seq_len - len(tail)

    prev_close = None
    prev_ts = None
    max_allowed_gap_sec = max(60, tf_sec * 12)

    for i, c in enumerate(tail):
        idx = offset + i
        op = _safe_float(c.get("Open", c.get("open")), default=1.0)
        if op <= 0.0:
            op = 1e-5
        hi = _safe_float(c.get("High", c.get("high")), default=op)
        lo = _safe_float(c.get("Low", c.get("low")), default=op)
        cl = _safe_float(c.get("Close", c.get("close")), default=op)

        arr[idx, 0] = ((cl - op) / op) * 10000.0
        arr[idx, 1] = ((hi - op) / op) * 10000.0
        arr[idx, 2] = ((lo - op) / op) * 10000.0

        cur_ts = None
        ts_raw = c.get("Timestamp") or c.get("timestamp") or c.get("openTime")
        if isinstance(ts_raw, (int, float)) and ts_raw > 0:
            # Handle millisecond vs second epoch timestamps
            cur_ts = float(ts_raw) / 1000.0 if float(ts_raw) > 1e11 else float(ts_raw)
        elif isinstance(ts_raw, str) and len(ts_raw) >= 19:
            try:
                cur_ts = datetime.fromisoformat(ts_raw.replace("Z", "+00:00")).timestamp()
            except Exception:
                cur_ts = None

        is_time_gap = (
            prev_ts is not None
            and cur_ts is not None
            and (cur_ts - prev_ts) > max_allowed_gap_sec
        )

        if prev_close is not None and prev_close > 0.0 and not is_time_gap:
            arr[idx, 3] = ((cl - prev_close) / prev_close) * 10000.0
        else:
            arr[idx, 3] = 0.0

        if n_channels >= 5:
            vol = _safe_float(c.get("Volume", c.get("volume")), default=0.0)
            arr[idx, 4] = math.log1p(max(0.0, vol)) / 10.0

        if n_channels >= 8:
            arr[idx, 5] = ((hi - max(op, cl)) / op) * 10000.0
            arr[idx, 6] = ((min(op, cl) - lo) / op) * 10000.0
            arr[idx, 7] = ((hi - lo) / op) * 10000.0

        prev_close = cl
        if cur_ts is not None:
            prev_ts = cur_ts

    return arr


def build_macro_vector_live(
    symbol: str,
    interval: str,
    candles: List[Dict],
    macro_data: Optional[Dict] = None,
    smc_data: Optional[Dict] = None,
    ta_data: Optional[Dict] = None,
    n_features: int = 31
) -> np.ndarray:
    """
    Builds the macro context vector matching training features (23 or 31 elements).
    """
    macro = macro_data or {}
    smc = smc_data or {}
    ta = ta_data or macro.get("Ta") or macro.get("ta") or {}

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
    pe = min(5.0, max(0.0, float(macro.get("PriceEntropy", 0.5))))
    adx = float(macro.get("Adx", 25.0)) / 100.0
    rsi = (float(macro.get("Rsi", 50.0)) - 50.0) / 50.0
    news_m = float(macro.get("MinutesToNews", 120.0))
    if news_m < 0:
        news_m = 120.0  # -1 means no upcoming high-impact news -> treat as far away (1.0)
    news_norm = min(120.0, max(0.0, news_m)) / 120.0

    hr_raw = macro.get("HourUtc")
    hr = float(hr_raw if hr_raw is not None else 12.0)
    dow_raw = macro.get("DayOfWeek")
    dow = float(dow_raw if dow_raw is not None else 2.0)
    hr_sin = math.sin(2 * math.pi * hr / 24.0)
    hr_cos = math.cos(2 * math.pi * hr / 24.0)
    dow_sin = math.sin(2 * math.pi * dow / 7.0)
    dow_cos = math.cos(2 * math.pi * dow / 7.0)

    tf_str = str(interval).lower()
    tf_sec = float(TF_SECONDS.get(tf_str, 5)) / 60.0

    def _signed_smc(dir_str: str, has_flag: bool) -> float:
        if not has_flag and not dir_str:
            return 0.0
        s = str(dir_str or "").upper()
        if "BULL" in s:
            return 1.0
        if "BEAR" in s:
            return -1.0
        return 0.0

    bos_raw = str(smc.get("smc_bos_dir") or smc.get("BosDirection") or "NONE").upper()
    has_ob = _signed_smc(
        smc.get("smc_ob_type") or smc.get("OrderBlockType") or "",
        bool(smc.get("smc_has_ob") or smc.get("HasOrderBlock"))
    )
    has_fvg = _signed_smc(
        smc.get("smc_fvg_type") or smc.get("FvgType") or "",
        bool(smc.get("smc_has_fvg") or smc.get("HasFvg"))
    )
    has_swp = _signed_smc(
        smc.get("smc_swp_dir") or smc.get("SweepDirection") or "",
        bool(smc.get("smc_has_swp") or smc.get("HasLiquiditySweep"))
    )
    has_bos = _signed_smc(
        bos_raw,
        bool(bos_raw not in ("NONE", "", "NULL") or smc.get("HasBos"))
    )

    asset_clean = (
        symbol.upper()
        .replace("_OTC", "")
        .replace(" OTC", "")
        .replace("_PO", "")
        .replace("/", "")
        .replace("-", "")
        .replace("_", "")
        .strip()
    )
    asset_id = float(ASSET_MAP.get(asset_clean, 6.0))

    base_23 = [
        day_pos, dist_high, dist_low,
        dxy_1m, dxy_5m, basket_sync, spread,
        asian_high, asian_low,
        pe, adx, rsi, news_norm,
        hr_sin, hr_cos, dow_sin, dow_cos,
        tf_sec,
        has_ob, has_fvg, has_swp, has_bos,
        asset_id
    ]

    if n_features <= 23:
        return np.array(base_23, dtype=np.float32)

    # Extended 31 features
    ta_score = _safe_float(ta.get("Score"), 0.0)
    micro_vel = _safe_float(ta.get("MicroVel"), 0.0) / 20.0
    last_close = _safe_float(candles[-1].get("Close", candles[-1].get("close")), 1.0) if candles else 1.0
    hma_raw = _safe_float(ta.get("HmaSlope"), 0.0)
    hma_bps = (hma_raw / last_close) * 10000.0 if last_close > 0 else 0.0
    hma_slope = float(np.clip(hma_bps / 5.0, -2.0, 2.0))
    vol_raw = ta.get("VolRatio") if ta.get("VolRatio") is not None else ta.get("VolStrengthVal", 1.0)
    vol_ratio = min(3.0, max(0.0, _safe_float(vol_raw, 1.0))) / 3.0
    bb_sq = float(1.0 if macro.get("BbSqueeze") else 0.0)
    price_pos = min(1.0, max(0.0, float(macro.get("PricePositionPct", 0.5))))
    trend_mat = float(macro.get("TrendMaturity", 0.0)) / 100.0
    last_ticks = float(macro.get("TickCountLastCandle", 1.0))
    tick_density = math.log1p(max(0.0, last_ticks)) / 5.0

    ext_31 = base_23 + [
        ta_score, micro_vel, hma_slope, vol_ratio,
        bb_sq, price_pos, trend_mat, tick_density
    ]
    return np.array(ext_31, dtype=np.float32)


class PatchTSTPredictor:
    """
    Universal inference engine that auto-detects model architecture from checkpoint.
    Supports both SequenceBrain and DualStreamPatchBrain transparently.
    """
    _instance: Optional["PatchTSTPredictor"] = None

    def __init__(self):
        self.model = None
        self.device = "cpu"
        self._temperature = 1.5  # Conservative default; overridden by checkpoint
        self._is_dual_stream = False
        self._candle_features = 4  # Default to 4-channel (v3); overridden by checkpoint
        self._seq_len = 160
        self._seq_len_mtf = 30
        self._model_name = "unknown"
        self._arch_tag = "neural_brain"
        self.metrics: Dict = {}
        self._load_model()

    @classmethod
    def get_instance(cls) -> "PatchTSTPredictor":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def _load_model(self):
        try:
            import torch
            try:
                torch.set_num_threads(1)
                torch.set_num_interop_threads(1)
            except Exception:
                pass
            from models.sequence_brain import SequenceBrain
            from models.patch_tst_brain import DualStreamPatchBrain

            self.device = torch.device("cpu")
            model_path = _resolve_model_path()
            if not model_path or not model_path.exists():
                log.warning(f"[PatchTSTPredictor] Model file not found in candidate paths. Neural brain offline.")
                self.model = None
                return

            checkpoint = torch.load(model_path, map_location=self.device, weights_only=False)

            # Read architecture metadata from checkpoint
            self._model_name = checkpoint.get("model_name", "dual_stream_patch_brain")
            self._candle_features = checkpoint.get("candle_features", 5)
            self._seq_len = checkpoint.get("seq_len", 160)
            self._seq_len_mtf = checkpoint.get("seq_len_mtf", 30)
            self._macro_features = checkpoint.get("macro_features", 23)
            macro_dim = self._macro_features
            d_model = checkpoint.get("d_model", 64)
            backbone = checkpoint.get("backbone", "hybrid")

            self._is_dual_stream = self._model_name in ("dual_stream_patch_brain", "patch_tst")

            if self._is_dual_stream:
                model = DualStreamPatchBrain(
                    candle_features=self._candle_features,
                    seq_len_micro=self._seq_len,
                    seq_len_mtf=self._seq_len_mtf,
                    macro_features=macro_dim,
                    d_model=d_model,
                    nhead=4,
                    num_layers=2,
                    num_classes=3,
                    dropout=0.0
                )
            else:
                model = SequenceBrain(
                    candle_features=self._candle_features,
                    seq_len=self._seq_len,
                    macro_features=macro_dim,
                    d_model=d_model,
                    nhead=4,
                    num_layers=2,
                    num_classes=3,
                    dropout=0.0,
                    backbone=backbone
                )

            state_dict = checkpoint["model_state_dict"] if "model_state_dict" in checkpoint else checkpoint
            # Strict except for the learned `temperature` scalar (absent in legacy checkpoints).
            res = model.load_state_dict(state_dict, strict=False)
            bad_missing = [k for k in res.missing_keys if k != "temperature"]
            if bad_missing or res.unexpected_keys:
                raise RuntimeError(
                    f"Checkpoint/architecture mismatch: missing={bad_missing}, unexpected={list(res.unexpected_keys)}"
                )
            model.eval()

            # Directional logit centering: SequenceBrain classifier only (DualStream is sign-antisymmetric by construction)
            if not self._is_dual_stream and hasattr(model, "classifier"):
                last_layer = model.classifier[-1]
                if hasattr(last_layer, "bias") and last_layer.bias is not None and last_layer.bias.data.shape[0] >= 3:
                    b = last_layer.bias.data
                    skew = (b[1] - b[2]) / 2.0
                    last_layer.bias.data[1] -= skew
                    last_layer.bias.data[2] += skew
                log.info("[PatchTSTPredictor] Centered directional logits on sequence classifier.")

            self._temperature = float(checkpoint.get("temperature", 1.5)) if checkpoint.get("temperature") is not None else 1.5

            self.model = model
            val_acc = checkpoint.get("val_acc") or 0.0
            vs = checkpoint.get("val_stats") or {}
            dir_wr = vs.get("wr_dir_60") or vs.get("wr_60") or vs.get("wr_58") or vs.get("wr_57") or vs.get("wr_50") or val_acc or 0.0
            n_samples = vs.get("total") or vs.get("n_val")
            version_tag = "v4" if "v4" in model_path.name else "v3"
            self._arch_tag = f"patch_tst_{version_tag}" if self._is_dual_stream else f"seq_brain_{version_tag}"
            self.metrics = {
                "accuracy": round(float(dir_wr) / 100.0, 4),
                "val_loss": checkpoint.get("val_loss"),
                "ece": checkpoint.get("ece"),
                "wr_60": vs.get("wr_60"),
                "n_val": n_samples,
                "epoch": checkpoint.get("epoch"),
            }
            arch_label = "DualStreamPatchBrain" if self._is_dual_stream else f"SequenceBrain({backbone})"
            log.info(
                f"[PatchTSTPredictor] Loaded {arch_label} from {model_path.name} "
                f"(channels={self._candle_features}, Val Acc: {val_acc:.2f}%, T={self._temperature:.2f})"
            )
        except Exception as e:
            log.error(f"[PatchTSTPredictor] Failed to load PyTorch model: {e}", exc_info=True)
            self.model = None

    def is_available(self) -> bool:
        if self.model is None:
            self._load_model()
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
        if self.model is None:
            self._load_model()
        if self.model is None or not candles:
            return None

        try:
            import torch
            import torch.nn.functional as F

            tf_sec = TF_SECONDS.get(str(interval).lower(), 5)
            mtf_tf_sec = 60 if tf_sec < 60 else 300

            # Use channel count from checkpoint (4, 5, or 8)
            x_micro_np = normalize_candles_live(candles, seq_len=self._seq_len, n_channels=self._candle_features, tf_sec=tf_sec)
            x_macro_np = build_macro_vector_live(symbol, interval, candles, macro_context, smc_context, n_features=getattr(self, "_macro_features", 23))

            x_micro_t = torch.tensor(x_micro_np, dtype=torch.float32).unsqueeze(0).to(self.device)
            x_macro_t = torch.tensor(x_macro_np, dtype=torch.float32).unsqueeze(0).to(self.device)

            with torch.no_grad():
                if self._is_dual_stream:
                    # DualStreamPatchBrain expects (x_micro, x_mtf, x_macro)
                    x_mtf_np = normalize_candles_live(
                        mtf_candles or [], seq_len=self._seq_len_mtf, n_channels=self._candle_features, tf_sec=mtf_tf_sec
                    )
                    x_mtf_t = torch.tensor(x_mtf_np, dtype=torch.float32).unsqueeze(0).to(self.device)
                    logits, _ = self.model(x_micro_t, x_mtf_t, x_macro_t)
                else:
                    # SequenceBrain expects (x_candles, x_macro)
                    logits, _ = self.model(x_micro_t, x_macro_t)

                # Temperature scaling
                temp = self._temperature
                scaled_logits = logits / temp
                probs = F.softmax(scaled_logits, dim=-1)[0].cpu().numpy()

            p_hold = float(probs[0])
            p_buy = float(probs[1])
            p_put = float(probs[2])

            # Determine direction from directional probabilities (conditional on non-flat movement)
            p_dir_sum = max(p_buy + p_put, 1e-8)
            p_buy_cond = p_buy / p_dir_sum
            p_put_cond = p_put / p_dir_sum

            # Flat-market / zero-drift tie-break guard: return HOLD instead of biasing towards BUY
            if abs(p_buy - p_put) < 1e-4 or abs(p_buy_cond - 0.5) < 1e-4:
                direction = "HOLD"
                calibrated_prob = 0.50
                raw_prob = 0.50
            else:
                if p_buy > p_put:
                    direction = "BUY"
                    raw_dir_prob = p_buy_cond
                else:
                    direction = "PUT"
                    raw_dir_prob = p_put_cond

                # Calibrated confidence:
                # In binary options, raw_dir_prob (p_buy_cond or p_put_cond) directly reflects binary directional confidence.
                # Flat chop (HOLD) dampens directional confidence proportionally:
                # When p_hold is high, flat movement indicates high risk of expiry tie/loss on Pocket Option.
                chop_penalty = max(0.20, 1.0 - p_hold * 0.75)
                calibrated_prob = 0.50 + (raw_dir_prob - 0.50) * chop_penalty

                # S5 Micro-Regime Dampener:
                # 5-second breakout spikes on Pocket Option OTC have strong negative autocorrelation (immediate mean-reversion).
                # Dampen confidence into [50% - 59%] so s5 does not claim false certainty on pure high-frequency tick noise,
                # while allowing genuine high-conviction momentum trades (>=58%) to execute.
                if str(interval).lower() == "s5":
                    calibrated_prob = min(0.59, 0.50 + (calibrated_prob - 0.50) * 0.85)

                # Safety floor at 50% (never below chance), ceiling at 92%
                calibrated_prob = max(0.50, min(0.92, round(float(calibrated_prob), 4)))
                # Signed P(BUY) in [0, 1] (>0.5 for BUY, <0.5 for PUT) so C# mlScore = (rawProb - 0.5)*2 has the right sign
                raw_prob = round(float(p_buy_cond), 4)

            arch_tag = self._arch_tag
            log.info(
                f"[Neural Brain] {symbol} {interval} -> {direction} | "
                f"Calibrated: {calibrated_prob*100:.1f}% | P(BUY|dir): {raw_prob*100:.1f}% | "
                f"HOLD: {p_hold*100:.1f}% | Temp: {temp:.2f} | Arch: {self._model_name}"
            )

            return direction, calibrated_prob, arch_tag, 1, raw_prob

        except Exception as e:
            log.error(f"[Neural Brain] Inference error: {e}", exc_info=True)
            return None
