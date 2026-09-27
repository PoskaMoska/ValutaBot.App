"""
local_train_all.py — обучение всех моделей локально с последующей загрузкой на Railway.

Запуск:
    cd ml_service
    py ../local_train_all.py [--upload] [--pairs EURUSD GBPUSD ...] [--intervals s5 1m ...]

Переменные окружения (уже в start_ml.bat):
    DATABASE_URL  — PostgreSQL Railway
    TwelveDataApiKey — ключ API
"""

import os, sys, time, argparse, io, pathlib, pickle, importlib, subprocess

# --- Настройки ---
PAIRS     = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
INTERVALS = ["1m", "s5", "s10", "s15", "s30"]
REGIMES   = ["ALL", "FLAT", "TREND", "CHAOS"]

RAILWAY_ML_URL = "https://mlphythonservice-production.up.railway.app"
DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

# Убедимся что скрипт запущен из корня репо, а sys.path содержит ml_service
SCRIPT_DIR  = pathlib.Path(__file__).parent
ML_DIR      = SCRIPT_DIR / "ml_service"
MODEL_DIR   = ML_DIR / "data" / "models"

if str(ML_DIR) not in sys.path:
    sys.path.insert(0, str(ML_DIR))

# --- Аргументы ---
def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--pairs",     nargs="+", default=PAIRS,     help="Пары для обучения")
    p.add_argument("--intervals", nargs="+", default=INTERVALS, help="Интервалы")
    p.add_argument("--regimes",   nargs="+", default=REGIMES,   help="Режимы")
    p.add_argument("--upload",    action="store_true",          help="После обучения загрузить на Railway")
    p.add_argument("--upload-only", action="store_true",        help="Только загрузить существующие .pkl на Railway")
    return p.parse_args()


def upload_model_to_railway(pkl_path: pathlib.Path, symbol: str, interval: str, regime: str) -> bool:
    """Загружает обученную модель на Railway через endpoint /upload_model."""
    import requests
    url = f"{RAILWAY_ML_URL}/upload_model"
    try:
        with open(pkl_path, "rb") as f:
            data = f.read()
        resp = requests.post(url,
            data={"symbol": symbol, "interval": interval, "regime": regime},
            files={"model_file": (pkl_path.name, data, "application/octet-stream")},
            timeout=60
        )
        if resp.status_code == 200:
            print(f"  ✅ Загружено на Railway: {pkl_path.name}")
            return True
        else:
            print(f"  ❌ Ошибка загрузки {pkl_path.name}: {resp.status_code} {resp.text[:200]}")
            return False
    except Exception as e:
        print(f"  ❌ Ошибка загрузки {pkl_path.name}: {e}")
        return False


def train_one(symbol: str, interval: str, regime: str) -> dict:
    """Обучает одну модель через ForexPredictor."""
    from model import ForexPredictor
    key = f"{symbol}_{interval}" + (f"_{regime}" if regime != "ALL" else "")
    print(f"\n{'='*60}")
    print(f"[{time.strftime('%H:%M:%S')}] Training: {key}")
    print(f"{'='*60}")
    t0 = time.time()
    try:
        predictor = ForexPredictor(symbol, interval, regime)
        result = predictor.train(candles=None)
        elapsed = time.time() - t0
        if "error" in result:
            print(f"  ❌ ОШИБКА: {result['error']}")
            return {"key": key, "success": False, "error": result["error"]}
        auc = result.get("auc", 0)
        n = result.get("n_train", 0)
        print(f"  ✅ Готово за {elapsed:.0f}с | AUC={auc:.4f} | n_train={n:,}")
        return {"key": key, "success": True, "auc": auc, "n_train": n, "elapsed": elapsed}
    except Exception as e:
        elapsed = time.time() - t0
        print(f"  ❌ Исключение: {e}")
        return {"key": key, "success": False, "error": str(e), "elapsed": elapsed}


def main():
    args = parse_args()

    # Устанавливаем переменные окружения для ml_service
    os.environ.setdefault("DATABASE_URL", DB_URL)
    os.environ.setdefault("TwelveDataApiKey", "3e0d610500f0414282d471471f59504e")
    os.environ.setdefault("TWELVE_DATA_API_KEY", os.environ["TwelveDataApiKey"])
    os.environ.setdefault("TARGET_HORIZON_CANDLES", "3")
    os.environ.setdefault("MIN_CONFIDENCE", "0.48")
    # НЕ ограничиваем MAX_HISTORICAL_CANDLES — используем полные 250k
    os.environ.pop("MAX_HISTORICAL_CANDLES", None)

    MODEL_DIR.mkdir(parents=True, exist_ok=True)

    if args.upload_only:
        print("=== Режим: только загрузка существующих .pkl на Railway ===")
        for pkl in sorted(MODEL_DIR.glob("*.pkl")):
            name = pkl.stem  # e.g. EURUSD_s5_FLAT
            parts = name.split("_")
            if len(parts) >= 2:
                symbol = parts[0]
                interval = parts[1]
                regime = parts[2] if len(parts) > 2 else "ALL"
                upload_model_to_railway(pkl, symbol, interval, regime)
        return

    total = len(args.pairs) * len(args.intervals) * len(args.regimes)
    print(f"\n🚀 Локальное обучение всех моделей")
    print(f"   Пары: {args.pairs}")
    print(f"   Интервалы: {args.intervals}")
    print(f"   Режимы: {args.regimes}")
    print(f"   Всего: {total} моделей\n")

    results = []
    done = 0
    failed = 0

    for symbol in args.pairs:
        for interval in args.intervals:
            for regime in args.regimes:
                result = train_one(symbol, interval, regime)
                results.append(result)

                if result["success"]:
                    done += 1
                    # Загружаем на Railway сразу после обучения
                    if args.upload:
                        key = f"{symbol}_{interval}" + (f"_{regime}" if regime != "ALL" else "")
                        pkl_path = MODEL_DIR / f"{key}.pkl"
                        if pkl_path.exists():
                            upload_model_to_railway(pkl_path, symbol, interval, regime)
                else:
                    failed += 1

                remaining = total - done - failed
                elapsed_total = sum(r.get("elapsed", 0) for r in results)
                avg_time = elapsed_total / len(results) if results else 0
                eta = avg_time * remaining
                print(f"\n  Прогресс: {done+failed}/{total} | ✅ {done} | ❌ {failed} | ETA: {eta/60:.0f} мин")

    # Итоговый отчёт
    print(f"\n{'='*60}")
    print(f"ИТОГ: {done} успешно, {failed} ошибок из {total}")
    print(f"{'='*60}")
    ok = [r for r in results if r["success"]]
    if ok:
        avg_auc = sum(r["auc"] for r in ok) / len(ok)
        print(f"Средний AUC: {avg_auc:.4f}")
        print(f"\nТоп-5 моделей:")
        for r in sorted(ok, key=lambda x: x["auc"], reverse=True)[:5]:
            print(f"  {r['key']}: AUC={r['auc']:.4f} | n={r['n_train']:,}")

    if not args.upload and done > 0:
        print(f"\n💡 Модели сохранены в: {MODEL_DIR}")
        print(f"   Чтобы загрузить на Railway: py local_train_all.py --upload-only")


if __name__ == "__main__":
    main()
