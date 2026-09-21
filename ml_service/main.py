"""
FastAPI ML microservice — LightGBM Forex/Crypto direction predictor.

Endpoints:
  GET  /health       -> service status + model list
  POST /predict      -> predict next candle direction
  POST /train        -> train/retrain a model
  GET  /models       -> list all loaded models
"""

from __future__ import annotations

import asyncio
import logging
import os
import time
import threading
import sqlite3
from typing import Dict, List, Optional

import orjson
from fastapi import FastAPI, HTTPException, BackgroundTasks, Header, Depends, Request, Response
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

from model import ForexPredictor, TF_MAP, is_forex_symbol

_API_SECRET = os.environ.get("INTERNAL_API_SECRET", "default_secret")

def verify_secret(x_internal_secret: str = Header(None)):
    if x_internal_secret != _API_SECRET:
        raise HTTPException(status_code=403, detail="Forbidden: Invalid Internal Secret")

# ┌────────────────────────────────────────────────────────────────────────┐
# │ Logging                                                                │
# └────────────────────────────────────────────────────────────────────────┘
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
)
log = logging.getLogger("ml-service")

from contextlib import asynccontextmanager

_DEFAULT_SYMBOLS = os.getenv("PRETRAIN_SYMBOLS", "EURUSD,GBPUSD,USDJPY,USDCAD,USDCHF,AUDUSD").split(",")
_DEFAULT_INTERVALS = os.getenv("PRETRAIN_INTERVALS", "s5,s10,s15,s30,1m").split(",")


async def _train_all():
    await asyncio.sleep(5)   # give FastAPI time to finish startup
    for sym in _DEFAULT_SYMBOLS:
        sym = sym.strip().upper()
        if not sym:
            continue
        is_forex = is_forex_symbol(sym)
        
        for tf in _DEFAULT_INTERVALS:
            tf = tf.strip().lower()
            if not tf:
                continue
            
            needs = False
            for r in ["FLAT", "TREND", "CHAOS"]:
                p = _get_predictor(sym, tf, r)
                if p.needs_retrain():
                    needs = True
                    break
                    
            if needs:
                # Stagger to avoid TwelveData 8 requests/min rate limit (12s space = 5 reqs/min)
                delay = 12.0 if is_forex else 1.5
                
                log.info(f"[Startup] Training {sym} ({tf}) | is_forex={is_forex}. Stagger delay={delay}s")
                
                t = threading.Thread(
                    target=_background_train,
                    args=(sym, tf, None),
                    daemon=True,
                )
                t.start()
                
                await asyncio.sleep(delay)

@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info("[Startup] Launching background pre-training for all timeframes...")
    asyncio.create_task(_train_all())
    asyncio.create_task(_auto_crawler_loop())
    asyncio.create_task(_weekly_global_retrain_loop())
    yield


# -------------------------------------------------------------------------
#   Autonomous Background Tasks
# -------------------------------------------------------------------------

async def _auto_crawler_loop():
    """Autonomously fetches 1m REST data to prevent database gaps."""
    # Wait 30 seconds after startup
    await asyncio.sleep(30)
    
    while True:
        log.info("[AutoCrawler] Starting scheduled REST backfill for historical gaps.")
        try:
            # We import here so we don't pollute global namespace
            import twelvedata_crawler_pg
            loop = asyncio.get_running_loop()
            # run_crawler is blocking, so run in executor
            await loop.run_in_executor(None, twelvedata_crawler_pg.run_crawler)
            log.info("[AutoCrawler] Backfill complete. Sleeping for 4 hours.")
        except Exception as e:
            log.error(f"[AutoCrawler] Error running crawler: {e}")
            
        # Run every 4 hours (ensures no gaps larger than 240 candles, perfectly safe for 5000 API limit)
        await asyncio.sleep(4 * 3600)

WEEKLY_RETRAIN_INTERVAL_H = int(os.getenv("WEEKLY_RETRAIN_INTERVAL_H", "168"))  # 7 days
_BOT_BASE_URL = os.getenv("BOT_BASE_URL", "")   # e.g. https://valutatbot.railway.app

# Retrain ALL timeframes weekly, including subminute
_WEEKLY_INTERVALS = _DEFAULT_INTERVALS


async def _weekly_global_retrain_loop():
    """
    Background loop that forces a full retrain on all pairs every Friday evening (after market close).
    Uses 100k candles from PostgreSQL historical_candles table.
    Sends a summary Telegram notification via the C# bot's internal webhook.
    """
    import datetime

    # Wait 10 minutes after startup before first check
    await asyncio.sleep(600)

    while True:
        now_utc = datetime.datetime.now(datetime.timezone.utc)
        
        # Trigger window: Friday 22:00 UTC through Saturday 02:00 UTC
        is_friday_night = (now_utc.weekday() == 4 and now_utc.hour >= 22)
        is_saturday_morning = (now_utc.weekday() == 5 and now_utc.hour < 2)
        in_schedule_window = is_friday_night or is_saturday_morning

        results = []

        for sym in _DEFAULT_SYMBOLS:
            sym = sym.strip().upper()
            if not sym:
                continue
            for tf in _WEEKLY_INTERVALS:
                tf = tf.strip().lower()
                for regime in ["ALL", "FLAT", "TREND", "CHAOS"]:
                    predictor = _get_predictor(sym, tf, regime)
                    
                    with predictor._lock:
                        meta = predictor._meta
                        
                    age_h = (time.time() - meta.trained_at) / 3600 if meta else 9999

                    # Trigger if it's the Friday schedule window (and hasn't been trained in the last 24h)
                    # OR if the model is missing / extremely old (> 168h)
                    if (in_schedule_window and age_h > 24) or age_h > 168:
                        log.info(f"[WeeklyRetrain] Forcing retrain: {sym} ({tf}) [{regime}], age={age_h:.0f}h")
                        try:
                            with predictor._lock:
                                predictor._meta = None
                            predictor.train(candles=None)
                            if predictor._meta:
                                results.append({
                                    "symbol": sym,
                                    "interval": tf,
                                    "regime": regime,
                                    "accuracy": predictor._meta.accuracy,
                                    "auc": predictor._meta.auc,
                                    "n_train": predictor._meta.n_train,
                                })
                        except Exception as e:
                            log.error(f"[WeeklyRetrain] Error retraining {sym}/{tf}/{regime}: {e}")
                            results.append({"symbol": sym, "interval": tf, "regime": regime, "error": str(e)})

                        await asyncio.sleep(15)  # rate limit between pairs

        if results:
            _send_weekly_summary(results)

        # Sleep 1 hour then check again
        await asyncio.sleep(3600)


def _send_weekly_summary(results: list):
    """Send weekly retrain summary to C# bot's admin notification endpoint."""
    import requests as req_lib

    lines = ["\U0001f4c5 <b>Еженедельное глобальное переобучение</b>\n"]
    for r in results:
        sym = r.get("symbol", "?")
        tf  = r.get("interval", "?")
        if "error" in r:
            lines.append(f"❌ {sym} ({tf}): {r['error']}")
        else:
            acc  = f"{r['accuracy']*100:.1f}%"
            auc  = f"{r['auc']:.3f}"
            n    = f"{r['n_train']:,}"
            qual = "\U0001f7e2" if r['accuracy'] >= 0.57 else "\U0001f7e1" if r['accuracy'] >= 0.54 else "\U0001f534"
            lines.append(f"{qual} <b>{sym}</b> ({tf}): Точность <b>{acc}</b> | AUC <b>{auc}</b> | {n} свечей")

    message = "\n".join(lines)
    log.info(f"[WeeklyRetrain] Summary:\n{message}")

    # Try to notify via C# bot webhook if configured
    if _BOT_BASE_URL:
        try:
            resp = req_lib.post(
                f"{_BOT_BASE_URL}/internal/notify-admins",
                json={"message": message, "parse_mode": "HTML"},
                headers={"X-Internal-Secret": _API_SECRET},
                timeout=10
            )
            log.info(f"[WeeklyRetrain] Notification sent: {resp.status_code}")
        except Exception as e:
            log.warning(f"[WeeklyRetrain] Could not send notification: {e}")


# ┌────────────────────────────────────────────────────────────────────────┐
# │ App                                                                    │
# └────────────────────────────────────────────────────────────────────────┘
app = FastAPI(
    title="ValutaBot ML Service",
    description="LightGBM direction predictor for Forex/Crypto scalping",
    version="1.0.0",
    lifespan=lifespan,
)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# ┌────────────────────────────────────────────────────────────────────────┐
# │ Global model registry                                                  │
# └────────────────────────────────────────────────────────────────────────┘
# key: "SYMBOL_interval"  (e.g. "BTCUSDT_1m")
_predictors: Dict[str, ForexPredictor] = {}
_registry_lock = threading.Lock()

START_TIME = time.time()


def _get_predictor(symbol: str, interval: str, regime: str = "ALL") -> ForexPredictor:
    regime = regime.upper()
    if regime == "ALL":
        key = f"{symbol.upper()}_{interval.lower()}"
    else:
        key = f"{symbol.upper()}_{interval.lower()}_{regime}"
        
    with _registry_lock:
        if key not in _predictors:
            p = ForexPredictor(symbol, interval, regime)
            p._try_load()         # load from disk if exists
            _predictors[key] = p
        return _predictors[key]


# ┌────────────────────────────────────────────────────────────────────────┐
# │ Request / Response schemas                                             │
# └────────────────────────────────────────────────────────────────────────┘

class CandleItem(BaseModel):
    openTime: Optional[int] = None
    open: float
    high: float
    low: float
    close: float
    volume: float


class PredictRequest(BaseModel):
    symbol: str                     # e.g. "BTCUSDT" or "EURUSD"
    interval: str                   # e.g. "1m" or "m5"
    candles: List[CandleItem]       # OHLCV history, latest last
    mtf_candles: Optional[List[CandleItem]] = None # Higher timeframe OHLCV
    is_forex: bool = False


class PredictResponse(BaseModel):
    direction: str                  # "BUY" | "PUT" | "NEUTRAL"
    confidence: float               # 0.0 – 1.0
    model_version: str
    accuracy: Optional[float] = None
    auc: Optional[float] = None
    n_train: Optional[int] = None
    variance_estimate: Optional[float] = None   # B6: predicted uncertainty [0,1], higher = less reliable
    raw_confidence: Optional[float] = None       # B6: confidence before variance-based dampening


class TrainRequest(BaseModel):
    symbol: str
    interval: str
    candles: Optional[List[CandleItem]] = None   # if None -> fetch from DB
    mtf_candles: Optional[List[CandleItem]] = None


class TrainResponse(BaseModel):
    symbol: str
    interval: str
    n_train: int = 0
    accuracy: float = 0.0
    auc: float = 0.0
    version: str = ""
    error: Optional[str] = None


class TrainFeedback(BaseModel):
    asset: str
    timeframe: str
    entry_price: float
    exit_price: float
    direction: str
    was_win: bool
    timestamp: str


# ┌────────────────────────────────────────────────────────────────────────┐
# │ Helpers                                                                │
# └────────────────────────────────────────────────────────────────────────┘

def _normalize_interval(interval: str) -> str:
    """Unify interval string: 'm1'->'1m', '5m'->'5m', etc."""
    iv = interval.lower().strip()
    # Already canonical (Binance-style): "1m", "5m", "15m", "1h" etc.
    if iv in TF_MAP.values():
        return iv
    # ValutaBot-style: "m1", "m5", "h1" etc.
    return TF_MAP.get(iv, "1m")


def _fetch_local_sqlite_main(symbol: str, interval: str, limit: int) -> list:
    """Read recent candles from SQLite for SGD partial_fit in /feedback."""
    import sqlite3 as _sqlite3
    db_path = os.path.join(os.path.dirname(__file__), "data", "ValutaTicks.db")
    if not os.path.exists(db_path):
        return []
    try:
        conn = _sqlite3.connect(db_path, timeout=10.0)
        # Try HistoricalCandles first (larger dataset)
        cursor = conn.cursor()
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='HistoricalCandles'")
        if cursor.fetchone():
            norm = _normalize_interval(interval)
            df = __import__("pandas").read_sql_query(
                "SELECT Open as open, High as high, Low as low, Close as close, Volume as volume "
                "FROM HistoricalCandles WHERE Asset=? AND Interval=? ORDER BY OpenTime DESC LIMIT ?",
                conn, params=(symbol, norm, limit)
            )
            if not df.empty and interval.startswith("s"):
                from model import _interpolate_subminute
                dicts = df.iloc[::-1].to_dict(orient="records")
                dicts = _interpolate_subminute(dicts, interval)
                df = __import__("pandas").DataFrame(dicts[-limit:][::-1])
        else:
            df = __import__("pandas").read_sql_query(
                "SELECT Open as open, High as high, Low as low, Close as close, Volume as volume "
                "FROM SubminuteCandles WHERE Asset=? AND Interval=? ORDER BY OpenTime DESC LIMIT ?",
                conn, params=(symbol, interval, limit)
            )
        conn.close()
        return df.iloc[::-1].to_dict(orient="records")
    except Exception as e:
        log.warning(f"[SQLite] fetch for SGD failed: {e}")
        return []


def _fetch_candles_at_entry(symbol: str, interval: str, entry_timestamp: str, limit: int = 200) -> list:
    """
    FIX #5: Читать свечи строго До момента входа (БЕЗ строго ДО момента входа (entry_timestamp).
    Это устраняет SGD Look-Ahead Bias.
    """
    import os
    import pandas as _pd
    import logging
    log = logging.getLogger("SGD")
    
    try:
        from datetime import datetime, timezone as _tz, timedelta
        entry_dt = datetime.fromisoformat(entry_timestamp.replace("Z", "+00:00"))
        
        # Calculate strict closed-candle cutoff (ROOT CAUSE #2 FIX)
        def _get_sec(iv: str) -> int:
            val = int(iv[1:])
            if iv.startswith("s"): return val
            if iv.startswith("m"): return val * 60
            if iv.startswith("h"): return val * 3600
            return 60
            
        interval_sec = _get_sec(interval)
        cutoff_dt = entry_dt - timedelta(seconds=interval_sec)
        
        # Use cutoff_dt for both unix and string comparisons
        cutoff_unix = int(cutoff_dt.timestamp())
        cutoff_str = cutoff_dt.strftime("%Y-%m-%dT%H:%M:%S.0000000Z") # Format as expected by DB
        
        db_url = os.getenv("DATABASE_URL")
        df = _pd.DataFrame()
        if db_url:
            import psycopg2
            try:
                conn = psycopg2.connect(db_url)
                norm = _normalize_interval(interval)
                
                # Попробуем subminute_candles (если младший таймфрейм)
                if interval.startswith("s"):
                    query = """
                        SELECT open_time as "openTime", open_price as "open", high_price as "high", 
                               low_price as "low", close_price as "close", volume as "volume"
                        FROM subminute_candles
                        WHERE asset = %s AND interval = %s AND open_time <= %s
                        ORDER BY open_time DESC LIMIT %s
                    """
                    df = _pd.read_sql_query(query, conn, params=(symbol, interval, cutoff_str, limit))
                
                # Если пустой датафрейм (или не s-таймфрейм), берем historical_candles
                if df.empty:
                    query = """
                        SELECT open_time as "openTime", open as "open", high as "high",
                               low as "low", close as "close", volume as "volume"
                        FROM historical_candles
                        WHERE asset = %s AND interval = %s AND open_time <= %s
                        ORDER BY open_time DESC LIMIT %s
                    """
                    df = _pd.read_sql_query(query, conn, params=(symbol, norm, cutoff_str, limit))
                    
                    if not df.empty and interval.startswith("s"):
                        # FIX BUG-3: Если мы взяли 1m свечи для s5/s10/s15/s30, их ОБЯЗАТЕЛЬНО
                        # нужно проинтерполировать. Иначе SGD учится на 1m свечах, а предиктит на s10.
                        # Это ломало distribution всех индикаторов.
                        from model import _interpolate_subminute
                        dicts = df.iloc[::-1].to_dict(orient="records")
                        dicts = _interpolate_subminute(dicts, interval)
                        # Берем последние limit штук, и переворачиваем обратно как ожидает логика ниже
                        df = _pd.DataFrame(dicts[-limit:][::-1])
                
                conn.close()
            except Exception as e:
                log.warning(f"PostgreSQL fetch failed in _fetch_candles_at_entry: {e}")
        
        # Fallback to local SQLite if PostgreSQL not available or failed
        if df.empty:
            import sqlite3 as _sqlite3
            db_path = os.path.join(os.path.dirname(__file__), "data", "ValutaTicks.db")
            if os.path.exists(db_path):
                conn = _sqlite3.connect(db_path, timeout=10.0)
                cursor = conn.cursor()
                cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='HistoricalCandles'")
                norm = _normalize_interval(interval)
                if cursor.fetchone():
                    df = _pd.read_sql_query(
                        "SELECT Open as open, High as high, Low as low, Close as close, Volume as volume "
                        "FROM HistoricalCandles WHERE Asset=? AND Interval=? AND OpenTime <= ? "
                        "ORDER BY OpenTime DESC LIMIT ?",
                        conn, params=(symbol, norm, cutoff_str, limit)
                    )
                    if not df.empty and interval.startswith("s"):
                        from model import _interpolate_subminute
                        dicts = df.iloc[::-1].to_dict(orient="records")
                        dicts = _interpolate_subminute(dicts, interval)
                        df = _pd.DataFrame(dicts[-limit:][::-1])
                else:
                    df = _pd.read_sql_query(
                        "SELECT Open as open, High as high, Low as low, Close as close, Volume as volume "
                        "FROM SubminuteCandles WHERE Asset=? AND Interval=? AND OpenTime <= ? "
                        "ORDER BY OpenTime DESC LIMIT ?",
                        conn, params=(symbol, interval, cutoff_unix, limit)
                    )
                conn.close()

        if df.empty:
            log.warning(f"[SGD] No candles before entry for {symbol}/{interval}. Fallback to recent.")
            return _fetch_local_sqlite_main(symbol, interval, limit)

        # Переворачиваем колонки в нужный реверс, если не высшие алиасы уже заданы
        return df.iloc[::-1].to_dict(orient="records")
    except Exception as e:
        log.warning(f"[SGD] _fetch_candles_at_entry failed: {e}. Fallback to recent.")
        return _fetch_local_sqlite_main(symbol, interval, limit)


def _candles_to_dicts(items: List[CandleItem]) -> List[dict]:
    # FIX C-10: openTime was missing -> features.py had no 'opentime' column ->
    # hour_sin/hour_cos were always 0.0 at inference (but real values during training).
    # This caused a permanent input space shift between train and inference.
    return [{"openTime": c.openTime, "open": c.open, "high": c.high, "low": c.low,
             "close": c.close, "volume": c.volume} for c in items]


# ┌────────────────────────────────────────────────────────────────────────┐
# │ Routes                                                                 │
# └────────────────────────────────────────────────────────────────────────┘

@app.get("/health")
def health():
    uptime = round(time.time() - START_TIME)
    with _registry_lock:
        models = [p.get_status() for p in _predictors.values()]
    return {
        "status": "ok",
        "uptime_seconds": uptime,
        "models_loaded": len(models),
        "models": models,
    }


@app.get("/models")
def list_models():
    with _registry_lock:
        return [p.get_status() for p in _predictors.values()]


# Cache to hold the latest live candles per symbol/interval for truthful SGD feedback
_live_candles_cache = {}
_cache_lock = threading.Lock()

@app.post("/predict")
async def predict(request: Request):
    raw_body = await request.body()
    data = orjson.loads(raw_body)
    
    symbol = data["symbol"]
    interval_raw = data["interval"]
    candles = data["candles"]
    mtf_candles = data.get("mtf_candles")
    is_forex = data.get("is_forex", False)

    n_candles = len(candles) if candles else 0
    if n_candles < 60:
        raise HTTPException(
            status_code=422,
            detail=f"Need at least 60 candles for reliable prediction, got {n_candles}",
        )

    # Forex-only policy: block crypto symbols
    if not is_forex_symbol(symbol):
        log.warning(f"[Predict] Blocked crypto symbol: {symbol}. Only forex is supported.")
        resp = {
            "direction": "NEUTRAL",
            "confidence": 0.5,
            "model_version": "forex-only",
        }
        return Response(content=orjson.dumps(resp), media_type="application/json")

    interval = _normalize_interval(interval_raw)
    
    # Store truthful live candles for SGD feedback
    with _cache_lock:
        _live_candles_cache[(symbol, interval)] = candles

    candle_dicts = candles
    mtf_candle_dicts = mtf_candles
    
    # Determine current market regime using GMM
    regime = "ALL"
    try:
        from features import build_features
        from model import get_regime_router
        
        # C# already drops the actively forming unclosed candle before sending the payload.
        # We use the payload directly to avoid double-dropping and Train-Serve skew.
        closed_candles = candle_dicts
        closed_mtf = mtf_candle_dicts
        
        feats = build_features(closed_candles, closed_mtf)
        if not feats.empty:
            router = get_regime_router(symbol, interval)
            regime = router.predict_live(feats.iloc[-10:])
    except Exception as e:
        log.error(f"[Predict] Failed to calc regime, fallback to ALL: {e}")

    predictor = _get_predictor(symbol, interval, regime)

    if predictor._model is None and regime == "ALL":
        for fallback in ["FLAT", "TREND", "CHAOS"]:
            fb_pred = _get_predictor(symbol, interval, fallback)
            if fb_pred._model is not None:
                predictor = fb_pred
                regime = fallback
                break

    # Auto-train in background if model is missing.
    if predictor._model is None and not predictor.is_training:
        predictor.is_training = True
        log.info(f"[Predict] Model missing for {symbol} ({interval}). Triggering background training.")
        import threading
        t = threading.Thread(
            target=_background_train, 
            args=(symbol, interval, None, None), 
            daemon=True
        )
        t.start()

    direction, confidence, version = predictor.predict(candle_dicts, mtf_candle_dicts)

    # B6: Predictive Variance Model — estimate how uncertain this prediction is
    # given the current feature state, and dampen confidence accordingly.
    raw_confidence = confidence
    variance_estimate = None
    try:
        from model import get_variance_predictor
        var_predictor = get_variance_predictor(symbol, interval, regime)
        variance_estimate = var_predictor.predict_variance(candle_dicts, mtf_candle_dicts)
        # Dampen confidence toward 0.5 proportionally to estimated uncertainty.
        # variance_estimate=0 -> no change; variance_estimate=1 -> fully neutral.
        confidence = 0.5 + (confidence - 0.5) * (1.0 - variance_estimate)
    except Exception as e:
        log.warning(f"[Predict] VariancePredictor failed, using raw confidence: {e}")

    meta = predictor._meta
    resp = {
        "direction": direction,
        "confidence": round(float(confidence), 4),
        "model_version": version,
        "accuracy": round(float(meta.accuracy), 4) if meta else None,
        "auc": round(float(meta.auc), 4) if meta else None,
        "n_train": meta.n_train if meta else None,
        "variance_estimate": round(float(variance_estimate), 4) if variance_estimate is not None else None,
        "raw_confidence": round(float(raw_confidence), 4),
    }
    return Response(content=orjson.dumps(resp), media_type="application/json")


@app.post("/train", response_model=TrainResponse)
def train(req: TrainRequest, background_tasks: BackgroundTasks):
    interval = _normalize_interval(req.interval)
    candle_dicts = _candles_to_dicts(req.candles) if req.candles else None
    mtf_candle_dicts = _candles_to_dicts(req.mtf_candles) if req.mtf_candles else None
    background_tasks.add_task(_background_train, req.symbol, interval, candle_dicts, mtf_candle_dicts)
    return TrainResponse(
        symbol=req.symbol,
        interval=interval,
        version="training-started",
    )


@app.post("/train/sync", response_model=TrainResponse)
def train_sync(req: TrainRequest):
    """Blocking train (useful for testing / initial setup)."""
    interval = _normalize_interval(req.interval)
    predictor = _get_predictor(req.symbol, interval)
    candle_dicts = _candles_to_dicts(req.candles) if req.candles else None
    mtf_candle_dicts = _candles_to_dicts(req.mtf_candles) if req.mtf_candles else None
    report = predictor.train(candle_dicts, mtf_candle_dicts)

    if "error" in report:
        return TrainResponse(symbol=req.symbol, interval=interval, error=report["error"])

    return TrainResponse(
        symbol=report.get("symbol", req.symbol),
        interval=report.get("interval", interval),
        n_train=report.get("n_train", 0),
        accuracy=report.get("accuracy", 0.0),
        auc=report.get("auc", 0.0),
        version=report.get("version", ""),
    )


def _background_train(symbol: str, interval: str, candles: Optional[list] = None, mtf_candles: Optional[list] = None):
    log.info(f"[BG Train] Starting clustered training for {symbol}_{interval}")
    for regime in ["ALL", "FLAT", "TREND", "CHAOS"]:
        predictor = _get_predictor(symbol, interval, regime)
        report = predictor.train(candles, mtf_candles)
        log.info(f"[BG Train] Done {regime}: {report}")


# ┌────────────────────────────────────────────────────────────────────────┐
# │ D11: Shadow Challenger endpoints                                       │
# └────────────────────────────────────────────────────────────────────────┘

class ChallengerTrainRequest(BaseModel):
    symbol: str
    interval: str
    regime: str = "ALL"   # which regime-specific predictor gets a challenger


@app.post("/challenger/train")
def challenger_train(req: ChallengerTrainRequest, background_tasks: BackgroundTasks):
    """Start background training of a shadow challenger model (D11).

    Production model is untouched. The challenger will be scored against
    production on every subsequent /feedback until it either proves
    statistically superior (auto-promotion) or is replaced by a newer challenger.
    """
    interval = _normalize_interval(req.interval)
    predictor = _get_predictor(req.symbol, interval, req.regime)

    def _run():
        try:
            report = predictor.train_challenger()
            log.info(f"[ShadowChallenger] Background challenger train done: {report}")
        except Exception as e:
            log.error(f"[ShadowChallenger] Background challenger train failed: {e}")

    background_tasks.add_task(_run)
    return {"status": "challenger-training-started", "symbol": req.symbol,
            "interval": interval, "regime": req.regime}


@app.get("/challenger/status")
def challenger_status(symbol: str, interval: str, regime: str = "ALL"):
    """Shadow A/B test stats: production vs challenger win rates over real outcomes."""
    normalized = _normalize_interval(interval)
    predictor = _get_predictor(symbol, normalized, regime)
    with predictor._challenger_lock:
        has_challenger = predictor._challenger_model is not None
        shadow_log = list(predictor._shadow_log)
    n = len(shadow_log)
    if n == 0:
        return {"has_challenger": has_challenger, "n_shadow_samples": 0}
    prod_wins = sum(1 for p, _ in shadow_log if p)
    chal_wins = sum(1 for _, c in shadow_log if c)
    return {
        "has_challenger": has_challenger,
        "n_shadow_samples": n,
        "production_win_rate": round(prod_wins / n, 4),
        "challenger_win_rate": round(chal_wins / n, 4),
        "challenger_edge_pp": round((chal_wins - prod_wins) / n * 100, 2),
    }

# ┌────────────────────────────────────────────────────────────────────────┐
# │ End Shadow Challenger endpoints                                        │
# └────────────────────────────────────────────────────────────────────────┘


@app.post("/feedback")
def feedback(req: TrainFeedback, background_tasks: BackgroundTasks):
    log.info(f"[Online RL] Received feedback for {req.asset} ({req.timeframe}): {'WIN' if req.was_win else 'LOSS'} "
             f"| Dir: {req.direction} Entry: {req.entry_price} Exit: {req.exit_price}")
    
    def process_feedback_bg():
        db_path = os.path.join(os.path.dirname(__file__), "data", "ValutaTicks.db")
        os.makedirs(os.path.dirname(db_path), exist_ok=True)
        try:
            conn = sqlite3.connect(db_path, timeout=30.0)
            cursor = conn.cursor()
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS OnlineFeedback (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Asset TEXT NOT NULL,
                    Interval TEXT NOT NULL,
                    Direction TEXT NOT NULL,
                    EntryPrice REAL NOT NULL,
                    ExitPrice REAL NOT NULL,
                    WasWin INTEGER NOT NULL,
                    Timestamp TEXT NOT NULL
                )
            ''')
            cursor.execute('''
                INSERT INTO OnlineFeedback (Asset, Interval, Direction, EntryPrice, ExitPrice, WasWin, Timestamp)
                VALUES (?, ?, ?, ?, ?, ?, ?)
            ''', (req.asset, req.timeframe, req.direction, req.entry_price, req.exit_price, int(req.was_win), req.timestamp))
            conn.commit()
            conn.close()

            norm_interval = req.timeframe.replace("s", "") if req.timeframe.endswith("s") else req.timeframe
            from model import ForexPredictor
            regime = "TREND"
            recent_candles = _fetch_candles_at_entry(req.asset, norm_interval, req.timestamp, limit=200)
            predictor = _get_predictor(req.asset, norm_interval, regime)
            higher_tf = predictor._get_higher_tf()
            mtf_candles = _fetch_candles_at_entry(req.asset, higher_tf, req.timestamp, limit=100)

            if len(recent_candles) >= 60:
                predictor.partial_fit_online(recent_candles, mtf_candles, req.was_win, req.direction)
                try:
                    predictor.evaluate_shadow(recent_candles, mtf_candles, req.entry_price, req.exit_price)
                except Exception as shadow_ex:
                    log.debug(f"[ShadowChallenger] evaluation skipped: {shadow_ex}")
        except Exception as e:
            log.error(f"[Online RL] DB Error: {e}")

    background_tasks.add_task(process_feedback_bg)
    return {"status": "ok", "message": "Feedback queued for processing."}


# ┌────────────────────────────────────────────────────────────────────────┐
# │ Entry point                                                            │
# └────────────────────────────────────────────────────────────────────────┘

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", 8765))
    uvicorn.run("main:app", host="0.0.0.0", port=port, reload=False)

