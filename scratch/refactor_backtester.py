import re

with open("ml_service/backtester.py", "r", encoding="utf-8") as f:
    code = f.read()

# 1. Remove global TRAIN_WINDOW and RETRAIN_EVERY
code = re.sub(r"TRAIN_WINDOW\s*=\s*\d+\n", "", code)
code = re.sub(r"RETRAIN_EVERY\s*=\s*\d+\n", "", code)

# 2. Add get_window_params and get_higher_tf
new_funcs = """
def get_higher_tf(interval):
    interval = interval.lower()
    if interval in ("5s", "s5", "10s", "s10", "15s", "s15"): return "1m"
    if interval in ("1m", "m1"): return "15m"
    if interval in ("5m", "m5"): return "1h"
    if interval in ("15m", "m15"): return "4h"
    if interval in ("1h", "h1"): return "1d"
    return "1d"

def get_window_params(interval):
    interval = interval.lower()
    if interval in ("5s", "s5", "10s", "s10", "15s", "s15"):
        return 17280, 2880  # 24 hours, retrain 4h
    elif interval in ("1m", "m1"):
        return 10080, 480  # 7 days, retrain 8h
    elif interval in ("5m", "m5"):
        return 8640, 288   # 30 days, retrain 24h
    elif interval in ("15m", "m15"):
        return 5760, 288   # 60 days, retrain 3d
    else:
        return 1500, 200

def is_forex_symbol(symbol):
"""
code = code.replace("def is_forex_symbol(symbol):", new_funcs)

# 3. Update train_model and predict_signal signatures
code = code.replace("def train_model(candles):", "def train_model(candles, mtf_candles=None):")
code = code.replace("feats = build_features(candles)", "feats = build_features(candles, mtf_candles)")

code = code.replace("def predict_signal(model, candles):", "def predict_signal(model, candles, mtf_candles=None):")

# 4. Update run_backtest signature and body
code = code.replace("def run_backtest(all_candles, payout):", "def run_backtest(all_candles, mtf_candles, payout, train_window, retrain_every):")
code = code.replace("TRAIN_WINDOW", "train_window")
code = code.replace("RETRAIN_EVERY", "retrain_every")

code = code.replace("train_model(all_candles[:train_window])", "train_model(all_candles[:train_window], mtf_candles)")
code = code.replace("train_model(all_candles[ws:i])", "train_model(all_candles[ws:i], mtf_candles)")
code = code.replace("predict_signal(model, all_candles[ws:i+1])", "predict_signal(model, all_candles[ws:i+1], mtf_candles)")

# 5. Update main()
main_repl = """
    train_window, retrain_every = get_window_params(interval)
    limit    = max(args.candles, train_window + FORECAST_HORIZON + 100)

    print(f"\\n[Setup] Symbol={symbol} | Interval={interval} | Candles={limit} | Payout={args.payout*100:.0f}%")
    is_forex = is_forex_symbol(symbol)
    print(f"[Fetch] {'TwelveData (Forex)' if is_forex else 'Binance (Crypto)'}...")
    t0 = time.time()
    candles = fetch_twelvedata(symbol, interval, limit) if is_forex else fetch_binance(symbol, interval, limit)
    print(f"[Fetch] {len(candles)} candles in {time.time()-t0:.1f}s")
    
    # Fetch MTF candles
    mtf_interval = get_higher_tf(interval)
    print(f"[Fetch MTF] {mtf_interval}...")
    mtf_limit = 2000
    mtf_candles = fetch_twelvedata(symbol, mtf_interval, mtf_limit) if is_forex else fetch_binance(symbol, mtf_interval, mtf_limit)
    print(f"[Fetch MTF] Loaded {len(mtf_candles)} MTF candles.")

    if len(candles) < train_window + FORECAST_HORIZON + 100:
        print(f"[ERROR] Not enough candles: {len(candles)}")
        sys.exit(1)

    result = run_backtest(candles, mtf_candles, args.payout, train_window, retrain_every)
"""

main_pattern = re.compile(r"    limit    = max\(args\.candles, train_window \+ FORECAST_HORIZON \+ 100\).*?result = run_backtest\(candles, args\.payout\)", re.DOTALL)
code = main_pattern.sub(main_repl.strip('\n'), code)

with open("ml_service/backtester.py", "w", encoding="utf-8") as f:
    f.write(code)

print("Done")
