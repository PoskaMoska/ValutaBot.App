"""
FastAPI ML microservice - SOTA Neural Brain (PatchTST DualStream Transformer)
Dedicated high-performance inference engine for ValutaBot.
"""
from __future__ import annotations

import asyncio
import logging
import os
import time
from typing import Any, Dict, List, Optional

# Prevent OpenMP / MKL / OpenBLAS thread contention on fractional container vCPUs
os.environ.setdefault("OMP_NUM_THREADS", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")

import orjson
from fastapi import FastAPI, HTTPException, Header, Depends, Request, Response
from pydantic import BaseModel
from contextlib import asynccontextmanager

from common import TF_MAP, is_forex_symbol, normalize_interval
from models.patch_tst_predictor import PatchTSTPredictor

_API_SECRET = os.environ.get("INTERNAL_API_SECRET", "default_secret")
START_TIME = time.time()

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
log = logging.getLogger("ml-service")

# Sentry APM & Error Tracking
_sentry_dsn = os.getenv("SENTRY_DSN") or os.getenv("SENTRY_DSN_PYTHON")
if _sentry_dsn:
    try:
        import sentry_sdk
        sentry_sdk.init(
            dsn=_sentry_dsn,
            traces_sample_rate=0.2,
            environment=os.getenv("ENVIRONMENT", "production"),
        )
        log.info("[Sentry] Python ML microservice monitoring initialized.")
    except Exception as _sentry_err:
        log.warning(f"[Sentry] Initialization error: {_sentry_err}")

def verify_secret(x_internal_secret: str | None = Header(default=None)):
    if x_internal_secret != _API_SECRET:
        raise HTTPException(status_code=403, detail="Forbidden: Invalid Internal Secret")

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Warm up PatchTST Neural Brain so weights are in memory immediately
    try:
        predictor = PatchTSTPredictor.get_instance()
        active = predictor.is_available()
        log.info(f"[Startup] PatchTST Neural Brain warmed up (available: {active}, tag: {predictor._arch_tag})")
    except Exception as e:
        log.error(f"[Startup] PatchTST warm-up failed: {e}", exc_info=True)

    yield

app = FastAPI(
    title="ValutaBot Neural Brain Service",
    version="3.0.0",
    lifespan=lifespan
)

# ┌────────────────────────────────────────────────────────────────────────┐
# │ Pydantic Request / Response Models                                     │
# └────────────────────────────────────────────────────────────────────────┘

class CandleItem(BaseModel):
    openTime: Any = 0
    open: float
    high: float
    low: float
    close: float
    volume: float = 1.0

class PredictResponse(BaseModel):
    direction: str
    confidence: float
    model_version: str
    horizon_candles: int = 1
    accuracy: float | None = None
    auc: float | None = None
    n_train: int | None = None
    variance_estimate: float | None = None
    raw_confidence: float | None = None
    top_features: list[str] | None = None

class TrainRequest(BaseModel):
    symbol: str
    interval: str
    regime: str = "ALL"
    candles: list[CandleItem] | None = None
    mtf_candles: list[CandleItem] | None = None

class TrainResponse(BaseModel):
    symbol: str
    interval: str
    n_train: int = 0
    accuracy: float = 0.0
    auc: float = 0.0
    version: str = ""
    error: str | None = None

class TrainFeedback(BaseModel):
    asset: str
    timeframe: str
    entry_price: float
    exit_price: float
    direction: str
    was_win: bool
    timestamp: str
    is_forex: bool | None = None

# ┌────────────────────────────────────────────────────────────────────────┐
# │ API Endpoints                                                          │
# └────────────────────────────────────────────────────────────────────────┘

@app.get("/health")
async def health(full: bool = False):
    uptime = round(time.time() - START_TIME)
    patch_pred = PatchTSTPredictor.get_instance()
    patch_ok = patch_pred.is_available()
    patch_ver = patch_pred._arch_tag if patch_ok else None

    result = {
        "status": "ok",
        "uptime_seconds": uptime,
        "models_loaded": 1 if patch_ok else 0,
        "patch_tst_available": patch_ok,
        "patch_tst_version": patch_ver,
        "architecture": patch_pred._model_name if patch_ok else "offline",
        "metrics": patch_pred.metrics if patch_ok else {},
        "models": [{"name": patch_pred._model_name, "version": patch_ver, "accuracy": patch_pred.metrics.get("accuracy")}] if patch_ok else []
    }
    return result

@app.api_route("/reload-models", methods=["GET", "POST"])
def reload_models():
    patch_pred = PatchTSTPredictor.get_instance()
    patch_pred._load_model()
    return {
        "status": "ok",
        "available": patch_pred.is_available(),
        "architecture": patch_pred._model_name,
        "metrics": patch_pred.metrics
    }

@app.get("/models")
def list_models():
    patch_pred = PatchTSTPredictor.get_instance()
    if patch_pred.is_available():
        return [{
            "symbol": "ALL",
            "interval": "ALL",
            "model_name": patch_pred._model_name,
            "version": patch_pred._arch_tag,
            "accuracy": patch_pred.metrics.get("accuracy"),
            "val_loss": patch_pred.metrics.get("val_loss"),
            "ece": patch_pred.metrics.get("ece")
        }]
    return []

@app.post("/predict")
async def predict(request: Request):
    raw_body = await request.body()
    if await request.is_disconnected():
        return Response(status_code=499)
    data = orjson.loads(raw_body)

    symbol = data.get("symbol") or data.get("asset")
    if not symbol:
        raise HTTPException(status_code=422, detail="Missing required field: 'symbol' or 'asset'")
    interval_raw = data.get("interval") or data.get("timeframe") or "1m"
    candles = data.get("candles") or []
    mtf_candles = data.get("mtf_candles")

    # Forex-only policy
    if not is_forex_symbol(symbol):
        log.warning(f"[Predict] Blocked crypto symbol: {symbol}. Only forex is supported.")
        resp = {
            "direction": "NEUTRAL",
            "confidence": 0.5,
            "model_version": "forex-only",
            "horizon_candles": 1,
            "accuracy": None,
            "raw_confidence": 0.5
        }
        return Response(content=orjson.dumps(resp), media_type="application/json")

    n_candles = len(candles)
    if n_candles < 35:
        raise HTTPException(
            status_code=422,
            detail=f"Need at least 35 candles for reliable prediction, got {n_candles}",
        )

    # Attach rich context for SMC
    smc_ctx = {
        "smc_bos_dir": data.get("smc_bos_dir", "NONE"),
        "smc_has_ob": data.get("smc_has_ob", False),
        "smc_ob_type": data.get("smc_ob_type", "NONE"),
        "smc_has_fvg": data.get("smc_has_fvg", False),
        "smc_fvg_type": data.get("smc_fvg_type", "NONE"),
        "smc_has_swp": data.get("smc_has_swp", False),
        "smc_swp_dir": data.get("smc_swp_dir", "NONE"),
    }
    macro_ctx = data.get("macro_context")
    interval = normalize_interval(interval_raw)

    patch_predictor = PatchTSTPredictor.get_instance()
    if not patch_predictor.is_available():
        raise HTTPException(status_code=503, detail="Neural Brain model not available")

    if await request.is_disconnected():
        return Response(status_code=499)
    neural_res = await asyncio.to_thread(
        patch_predictor.predict,
        symbol, interval, candles, mtf_candles,
        macro_ctx, smc_ctx
    )

    if neural_res is None:
        raise HTTPException(status_code=500, detail="Inference returned null")

    n_dir, n_conf, n_ver, n_hor, n_raw = neural_res
    m = patch_predictor.metrics or {}
    resp = {
        "direction": n_dir,
        "confidence": round(float(n_conf), 4),
        "model_version": n_ver,
        "horizon_candles": n_hor,
        "accuracy": m.get("accuracy"),
        "auc": None,
        "n_train": m.get("n_val"),
        "variance_estimate": None,
        "raw_confidence": round(float(n_raw), 4),
        "top_features": ["PatchTST_MicroCrossAttn", "DualStream_SequenceTransformer", "MacroContext", "SMCAnchors"],
    }
    return Response(content=orjson.dumps(resp), media_type="application/json")

@app.post("/feedback")
def feedback(req: TrainFeedback):
    """Receives live trade outcome feedback. Neural Brain trains on dataset_v3.npz via train_brain.py."""
    log.info(f"[Trade Feedback] {req.asset} ({req.timeframe}): {'WIN' if req.was_win else 'LOSS'} | Dir: {req.direction} Entry: {req.entry_price} Exit: {req.exit_price}")
    return {"status": "ok", "message": "Feedback recorded."}

@app.post("/train", response_model=TrainResponse)
def train(req: TrainRequest):
    return TrainResponse(
        symbol=req.symbol,
        interval=normalize_interval(req.interval),
        version="patch_tst_v3",
        accuracy=PatchTSTPredictor.get_instance().metrics.get("accuracy", 0.0)
    )

@app.post("/train/sync", response_model=TrainResponse)
def train_sync(req: TrainRequest):
    return TrainResponse(
        symbol=req.symbol,
        interval=normalize_interval(req.interval),
        version="patch_tst_v3",
        accuracy=PatchTSTPredictor.get_instance().metrics.get("accuracy", 0.0)
    )

@app.get("/diagnostics/audit")
def diagnostics_audit(notify: bool = False):
    """Autonomous Dataset & Pipeline Sanity Audit."""
    from diagnostics import run_dataset_sanity_audit
    return run_dataset_sanity_audit(sample_limit=500, notify_telegram=notify)

@app.post("/debug/prune_disk", dependencies=[Depends(verify_secret)])
def debug_prune_disk():
    return {"status": "ok", "freed_mb": 0.0}

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", 8765))
    uvicorn.run("main:app", host="0.0.0.0", port=port, reload=False)
