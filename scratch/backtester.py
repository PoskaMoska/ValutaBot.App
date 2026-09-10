"""
Walk-Forward Backtester for ValutaBot ML Engine.

Usage:
  python backtester.py --symbol EURUSD --interval s5 --candles 5000
  python backtester.py --symbol BTCUSDT --interval 1m --candles 5000
  python backtester.py --symbol EURUSD --interval 1m --candles 3000 --payout 0.80
"""

import argparse
import sys
import os
import time
import math
import requests
import numpy as np
import pandas as pd

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SCRIPT_DIR)

from features import build_features

try:
    import lightgbm as lgb
    from sklearn.model_selection import TimeSeriesSplit
    from sklearn.metrics import accuracy_score, roc_auc_score
    HAS_LGBM = True
except ImportError:
    print("[ERROR] LightGBM not installed. Run: pip install lightgbm scikit-learn")
    sys.exit(1)

BINANCE_BASE     = "https://api.binance.com"
TWELVE_DATA_BASE = "https://api.twelvedata.com"
TWELVE_DATA_API_KEY = os.getenv("TwelveDataApiKey") or os.getenv("TWELVE_DATA_API_KEY", "")

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
    "verbose": -1,
}

TRAIN_WINDOW     = 1500
RETRAIN_EVERY    = 200
MIN_CONFIDENCE   = 0.60
FORECAST_HORIZON = 3


def is_forex_symbol(symbol):
    """Forex-only policy: returns True only for forex/commodity pairs, False for crypto."""
    sym = symbol.upper()
    # Explicit crypto blocklist
    if sym.endswith("USDT") or sym.endswith("BTC") or sym.endswith("ETH"):
        return False
    if sym in ["BTC", "ETH", "SOL", "BNB", "XRP", "ADA", "DOGE",
               "BTCUSD", "ETHUSD", "SOLUSD"]:
        return False
    # Forex / commodities
    if sym in ["GOLD", "SILVER", "BRENT", "OIL", "XAUUSD", "XAGUSD"]:
        return True
    if len(sym) == 6:
        return True
    return False


def to_twelvedata_symbol(symbol):
    sym = symbol.upper()
    if sym in ["GOLD", "XAUUSD"]:
        return "XAU/USD"
    if sym in ["SILVER", "XAGUSD"]:
        return "XAG/USD"
    if len(sym) == 6:
        return f"{sym[:3]}/{sym[3:]}"
    return sym


def interpolate_subminute(m1_candles, interval):
    sec = int(interval[1:]) if (interval.startswith("s") and len(interval) > 1) else 60
    if sec >= 60:
        return m1_candles
    sub_per_min = 60 // sec
    interpolated = []
    for m in m1_candles:
        sp = m["open"]
        ep = m["close"]
        pr = ep - sp
        hl = m["high"]
        ll = m["low"]
        vs = (hl - ll) / sub_per_min
        for i in range(sub_per_min):
            o = sp + pr * (i / sub_per_min)
            c = sp + pr * ((i + 1) / sub_per_min)
            mw = vs * 0.25 * math.sin(i * math.pi / 2.0)
            h = min(max(o, c) + abs(mw), hl)
            l = max(min(o, c) - abs(mw), ll)
            interpolated.append({"open": o, "high": h, "low": l, "close": c,
                                  "volume": m["volume"] / sub_per_min})
    return interpolated


def fetch_binance(symbol, interval, limit):
    needs_interp = interval.startswith("s")
    raw_interval = "1m" if needs_interp else interval
    if needs_interp:
        sec = int(interval[1:]) if len(interval) > 1 else 5
        sub_per_min = 60 // sec
        fetch_limit = math.ceil(limit / sub_per_min) + 50
    else:
        fetch_limit = limit

    tf_map = {"1m":"1m","m1":"1m","3m":"3m","m3":"3m","5m":"5m","m5":"5m",
               "15m":"15m","m15":"15m","30m":"30m","h1":"1h","1h":"1h","4h":"4h","h4":"4h"}
    api_tf = tf_map.get(raw_interval, "1m")

    candles = []
    end_time = None
    while len(candles) < fetch_limit:
        batch = min(fetch_limit - len(candles), 1000)
        params = {"symbol": symbol.upper(), "interval": api_tf, "limit": batch}
        if end_time:
            params["endTime"] = end_time
        try:
            resp = requests.get(f"{BINANCE_BASE}/api/v3/klines", params=params, timeout=15)
            data = resp.json()
        except Exception as e:
            print(f"  [WARN] Binance error: {e}")
            break
        if not data:
            break
        batch_c = [{"open":float(k[1]),"high":float(k[2]),"low":float(k[3]),
                    "close":float(k[4]),"volume":float(k[5])} for k in data]
        candles = batch_c + candles
        end_time = int(data[0][0]) - 1
        if len(data) < batch:
            break

    if needs_interp:
        candles = interpolate_subminute(candles, interval)
        candles = candles[-limit:]
    return candles


def fetch_twelvedata(symbol, interval, limit):
    if not TWELVE_DATA_API_KEY:
        print("  [WARN] TwelveDataApiKey not set.")
        return []

    needs_interp = interval.startswith("s")
    raw_interval = "1m" if needs_interp else interval
    if needs_interp:
        sec = int(interval[1:]) if len(interval) > 1 else 5
        sub_per_min = 60 // sec
        td_limit = math.ceil(limit / sub_per_min) + 50
    else:
        td_limit = limit

    td_map = {"1m":"1min","m1":"1min","5m":"5min","m5":"5min",
               "15m":"15min","m15":"15min","1h":"1h","h1":"1h","4h":"4h","h4":"4h"}
    api_tf = td_map.get(raw_interval, "1min")

    params = {"symbol": to_twelvedata_symbol(symbol), "interval": api_tf,
              "outputsize": min(td_limit, 5000), "apikey": TWELVE_DATA_API_KEY, "order": "ASC"}
    try:
        resp = requests.get(f"{TWELVE_DATA_BASE}/time_series", params=params, timeout=20)
        data = resp.json()
    except Exception as e:
        print(f"  [WARN] TwelveData error: {e}")
        return []

    if "values" not in data:
        print(f"  [WARN] TwelveData: {data.get('message','no values')}")
        return []

    candles = [{"open":float(v["open"]),"high":float(v["high"]),"low":float(v["low"]),
                "close":float(v["close"]),"volume":float(v.get("volume",0))} for v in data["values"]]

    if needs_interp:
        candles = interpolate_subminute(candles, interval)
        candles = candles[-limit:]
    return candles


def train_model(candles):
    feats = build_features(candles)
    if feats.empty or len(feats) < 100:
        return None
    closes = np.array([c["close"] for c in candles])
    target = np.zeros(len(closes), dtype=int)
    target[:-FORECAST_HORIZON] = (closes[FORECAST_HORIZON:] > closes[:-FORECAST_HORIZON]).astype(int)
    fi = feats.index.values
    mask = fi < (len(closes) - FORECAST_HORIZON)
    fv = feats.loc[fi[mask]]
    y = target[fi[mask]]
    X = fv.values.astype(np.float32)
    if len(X) < 100:
        return None
    model = lgb.LGBMClassifier(**LGBM_PARAMS)
    model.fit(X, y)
    return model


def predict_signal(model, candles):
    feats = build_features(candles)
    if feats.empty or len(feats) < 5:
        return "NEUTRAL", 0.5
    X_last = feats.iloc[[-1]].values.astype(np.float32)
    prob = float(model.predict_proba(X_last)[0, 1])
    if prob >= MIN_CONFIDENCE:
        return "BUY", prob
    elif prob <= (1.0 - MIN_CONFIDENCE):
        return "PUT", 1.0 - prob
    return "NEUTRAL", 0.5


def run_backtest(all_candles, payout):
    n = len(all_candles)
    print(f"\n[Backtest] Свечей: {n} | Обучение: {TRAIN_WINDOW} | Переобучение каждые: {RETRAIN_EVERY}")
    print(f"[Backtest] Мин. уверенность: {MIN_CONFIDENCE} | Горизонт: {FORECAST_HORIZON} свечи | Payout: {payout*100:.0f}%\n")

    print("[Phase 1] Начальное обучение...")
    model = train_model(all_candles[:TRAIN_WINDOW])
    if model is None:
        print("[ERROR] Обучение провалено. Недостаточно данных.")
        return {}
    print("[Phase 1] Готово.\n")

    trades = []
    last_retrain = TRAIN_WINDOW
    retrain_count = 0
    start = TRAIN_WINDOW
    end   = n - FORECAST_HORIZON

    print(f"[Phase 2] Симуляция с {start} по {end} ({end-start} шагов)...")
    for i in range(start, end):
        if (i - last_retrain) >= RETRAIN_EVERY:
            ws = max(0, i - TRAIN_WINDOW)
            new_model = train_model(all_candles[ws:i])
            if new_model is not None:
                model = new_model
                retrain_count += 1
            last_retrain = i

        ws = max(0, i - TRAIN_WINDOW + 1)
        signal, confidence = predict_signal(model, all_candles[ws:i+1])
        if signal == "NEUTRAL":
            continue

        entry  = all_candles[i]["close"]
        future = all_candles[i + FORECAST_HORIZON]["close"]
        actual_up = future > entry
        win = (signal == "BUY" and actual_up) or (signal == "PUT" and not actual_up)
        trades.append({"candle_idx": i, "direction": signal,
                       "confidence": round(confidence, 4),
                       "entry_price": round(entry, 6),
                       "exit_price": round(future, 6), "win": win})

        done = i - start
        total = end - start
        if done > 0 and done % 500 == 0:
            wr = sum(1 for t in trades if t["win"]) / len(trades) * 100
            print(f"  {done/total*100:.1f}% | Сделок: {len(trades)} | WR: {wr:.1f}%")

    return {"trades": trades, "retrain_count": retrain_count,
            "total_candles": n, "payout": payout}


def print_report(result, symbol, interval):
    if not result or not result.get("trades"):
        print("[ERROR] Сделок нет.")
        return None
    trades   = result["trades"]
    payout   = result["payout"]
    total    = len(trades)
    wins     = sum(1 for t in trades if t["win"])
    losses   = total - wins
    wr       = wins / total if total > 0 else 0
    buy_t    = [t for t in trades if t["direction"] == "BUY"]
    put_t    = [t for t in trades if t["direction"] == "PUT"]
    buy_w    = sum(1 for t in buy_t if t["win"])
    put_w    = sum(1 for t in put_t if t["win"])
    max_dd = cur_dd = 0
    for t in trades:
        cur_dd = 0 if t["win"] else cur_dd + 1
        max_dd = max(max_dd, cur_dd)
    ev_100 = ((wr * payout) - ((1 - wr) * 1.0)) * 100

    print("\n" + "=" * 52)
    print("  BACKTEST REPORT")
    print("=" * 52)
    print(f"  Символ:          {symbol} | {interval.upper()}")
    print(f"  Всего свечей:    {result['total_candles']}")
    print(f"  Переобучений:    {result['retrain_count']}")
    print("-" * 52)
    print(f"  Сделок всего:    {total}")
    print(f"    BUY:  {len(buy_t):4d}  WIN: {buy_w} ({buy_w/max(1,len(buy_t))*100:.1f}%)")
    print(f"    PUT:  {len(put_t):4d}  WIN: {put_w} ({put_w/max(1,len(put_t))*100:.1f}%)")
    print("-" * 52)
    print(f"  Win Rate:        {wr*100:.2f}%")
    print(f"  Max Drawdown:    {max_dd} убытков подряд")
    print(f"  EV (100 сделок): {ev_100:+.2f}$ на $1 ставку")
    print("=" * 52)
    print("\n  ВЕРДИКТ:")
    if wr >= 0.60:
        print("  [+] Win Rate >= 60% — алгоритм прибыльный.")
        print("      Если реально хуже — проблема в брокере/исполнении.")
    elif wr >= 0.555:
        print("  [~] Win Rate 55-60% — прибыльный, малый запас.")
        print("      Задержка входа/комиссии могут съедать прибыль.")
    elif wr >= 0.50:
        print("  [-] Win Rate 50-55% — на уровне случайного угадывания.")
        print("      Нужна архитектурная переработка ML.")
    else:
        print("  [--] Win Rate < 50% — алгоритм убыточен.")
        print("       Сигналы систематически неверны.")
    return trades


def save_csv(trades, symbol, interval):
    filename = f"backtest_{symbol}_{interval}_{int(time.time())}.csv"
    pd.DataFrame(trades).to_csv(filename, index=False)
    print(f"\n  CSV: {filename}")


def main():
    parser = argparse.ArgumentParser(description="ValutaBot Walk-Forward Backtester")
    parser.add_argument("--symbol",   default="BTCUSDT")
    parser.add_argument("--interval", default="1m")
    parser.add_argument("--candles",  type=int, default=5000)
    parser.add_argument("--payout",   type=float, default=0.80)
    parser.add_argument("--save-csv", action="store_true")
    args = parser.parse_args()

    symbol   = args.symbol.upper().replace("-", "")
    interval = args.interval.lower()
    limit    = max(args.candles, TRAIN_WINDOW + FORECAST_HORIZON + 100)

    print(f"\n[Setup] Symbol={symbol} | Interval={interval} | Candles={limit} | Payout={args.payout*100:.0f}%")
    is_forex = is_forex_symbol(symbol)
    print(f"[Fetch] {'TwelveData (Forex)' if is_forex else 'Binance (Crypto)'}...")
    t0 = time.time()
    candles = fetch_twelvedata(symbol, interval, limit) if is_forex else fetch_binance(symbol, interval, limit)
    print(f"[Fetch] {len(candles)} свечей за {time.time()-t0:.1f}s")

    if len(candles) < TRAIN_WINDOW + FORECAST_HORIZON + 100:
        print(f"[ERROR] Недостаточно свечей: {len(candles)}")
        sys.exit(1)

    result = run_backtest(candles, args.payout)
    trades = print_report(result, symbol, interval)
    if args.save_csv and trades:
        save_csv(trades, symbol, interval)


if __name__ == "__main__":
    main()
