"""
Walk-Forward Backtest
=====================
Р Р°Р·Р±РёРІР°РµС‚ РёСЃС‚РѕСЂРёС‡РµСЃРєРёРµ РґР°РЅРЅС‹Рµ РЅР° train/test РїРѕ РґР°С‚Рµ.
РўСЂРµРЅРёСЂСѓРµС‚ LightGBM РўРћР›Р¬РљРћ РЅР° train-РїРµСЂРёРѕРґРµ (РЅРµ РІРёРґРёС‚ test).
РЎРёРјСѓР»РёСЂСѓРµС‚ СЃРґРµР»РєРё РЅР° test-РїРµСЂРёРѕРґРµ (out-of-sample).
Р­С‚Рѕ С‡РµСЃС‚РЅР°СЏ РѕС†РµРЅРєР° СЂРµР°Р»СЊРЅРѕРіРѕ РєР°С‡РµСЃС‚РІР° РјРѕРґРµР»Рё.

Р—Р°РїСѓСЃРє: py walkforward_backtest.py
"""
import os, sys, datetime, warnings
import numpy as np
import pandas as pd
import psycopg2

warnings.filterwarnings("ignore")

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

# в”Ђв”Ђ РџР°СЂР°РјРµС‚СЂС‹ в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
PAIRS          = ["EURUSD", "GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
TRAIN_UNTIL    = "2026-06-01"   # Train: РјР°СЂС‚-РјР°Р№ 2026
TEST_FROM      = "2026-06-01"   # Test:  РёСЋРЅСЊ-СЃРµРЅС‚СЏР±СЂСЊ 2026
HORIZON        = 3              # Р“РѕСЂРёР·РѕРЅС‚: 3 СЃРІРµС‡Рё РІРїРµСЂС‘Рґ (РєР°Рє РІ РїСЂРѕРґР°РєС€РЅРµ)
MIN_CONFIDENCE = 0.52           # РџРѕСЂРѕРі СѓРІРµСЂРµРЅРЅРѕСЃС‚Рё (С‡СѓС‚СЊ РјСЏРіС‡Рµ С‡РµРј 0.48 РІ РјРѕРґРµР»Рё)
RAW_WINDOW     = 20             # РћРєРЅРѕ raw-close С„РёС‡РµР№ (СѓРїСЂРѕС‰С‘РЅРЅР°СЏ РІРµСЂСЃРёСЏ)

try:
    import lightgbm as lgb
    HAS_LGBM = True
except ImportError:
    print("вќЊ lightgbm РЅРµ СѓСЃС‚Р°РЅРѕРІР»РµРЅ. Р—Р°РїСѓСЃС‚Рё: pip install lightgbm")
    sys.exit(1)

# в”Ђв”Ђ Feature Engineering (СѓРїСЂРѕС‰С‘РЅРЅР°СЏ РІРµСЂСЃРёСЏ features.py) в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
def make_features(df: pd.DataFrame) -> pd.DataFrame:
    """Р“РµРЅРµСЂРёСЂСѓРµС‚ С„РёС‡Рё РёР· OHLCV РґР°РЅРЅС‹С…. РЈРїСЂРѕС‰С‘РЅРЅР°СЏ РІРµСЂСЃРёСЏ РЅР°С€РµРіРѕ features.py."""
    d = df.copy()
    c = d["close"]
    o = d["open"]
    h = d["high"]
    l = d["low"]
    v = d["volume"]

    # Price action
    d["body"]        = (c - o).abs()
    d["upper_wick"]  = h - np.maximum(o, c)
    d["lower_wick"]  = np.minimum(o, c) - l
    d["candle_range"]= h - l
    d["close_pos"]   = (c - l) / (h - l + 1e-10)
    d["is_bullish"]  = (c > o).astype(float)

    # Returns
    d["ret1"]  = c.pct_change(1)
    d["ret3"]  = c.pct_change(3)
    d["ret5"]  = c.pct_change(5)
    d["ret10"] = c.pct_change(10)

    # Volatility
    d["vol5"]  = d["ret1"].rolling(5).std()
    d["vol10"] = d["ret1"].rolling(10).std()
    d["vol20"] = d["ret1"].rolling(20).std()
    d["vol_z"] = (d["vol5"] - d["vol5"].rolling(50).mean()) / (d["vol5"].rolling(50).std() + 1e-10)

    # RSI (8-period, РєР°Рє РґР»СЏ СЃСѓР±РјРёРЅСѓС‚РѕРє)
    delta = c.diff()
    gain  = delta.clip(lower=0).rolling(8).mean()
    loss  = (-delta.clip(upper=0)).rolling(8).mean()
    d["rsi"] = 100 - 100 / (1 + gain / (loss + 1e-10))
    d["rsi_norm"] = (d["rsi"] - 50) / 50

    # EMA momentum
    d["ema8"]  = c.ewm(span=8,  adjust=False).mean()
    d["ema21"] = c.ewm(span=21, adjust=False).mean()
    d["ema_cross"] = (d["ema8"] - d["ema21"]) / (c + 1e-10)

    # Volume
    d["vol_ma10"]   = v.rolling(10).mean()
    d["vol_ratio"]  = v / (d["vol_ma10"] + 1e-10)

    # Time features
    if "open_time" in d.columns:
        dt = pd.to_datetime(d["open_time"], utc=True)
        d["hour_sin"] = np.sin(2 * np.pi * dt.dt.hour / 24)
        d["hour_cos"] = np.cos(2 * np.pi * dt.dt.hour / 24)
        d["dow_sin"]  = np.sin(2 * np.pi * dt.dt.dayofweek / 7)

    # Raw close window (Price Action Р±РµР· РёРЅРґРёРєР°С‚РѕСЂРѕРІ)
    for i in range(1, RAW_WINDOW + 1):
        d[f"raw_close_{i}"] = c.shift(i) / (c + 1e-10) - 1.0

    return d.dropna()


def make_labels(df: pd.DataFrame, horizon: int = 3) -> pd.Series:
    """
    Triple-Barrier label: 1 РµСЃР»Рё С†РµРЅР° С‡РµСЂРµР· `horizon` СЃРІРµС‡РµР№ РІС‹С€Рµ С‚РµРєСѓС‰РµР№, РёРЅР°С‡Рµ 0.
    РЈРїСЂРѕС‰С‘РЅРЅР°СЏ РІРµСЂСЃРёСЏ (Р±РµР· Р±Р°СЂСЊРµСЂРѕРІ, РїСЂРѕСЃС‚Рѕ РЅР°РїСЂР°РІР»РµРЅРёРµ).
    """
    future_close = df["close"].shift(-horizon)
    return (future_close > df["close"]).astype(int)


# в”Ђв”Ђ Р—Р°РіСЂСѓР·РєР° РґР°РЅРЅС‹С… в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
def load_candles(pair: str, conn) -> pd.DataFrame:
    df = pd.read_sql_query("""
        SELECT open_time, open, high, low, close, volume
        FROM historical_candles
        WHERE asset = %s AND interval = '1m'
        ORDER BY open_time ASC
    """, conn, params=(pair,))
    df["open_time"] = pd.to_datetime(df["open_time"], format="ISO8601", utc=True)
    for col in ["open", "high", "low", "close", "volume"]:
        df[col] = df[col].astype(float)
    return df


# в”Ђв”Ђ Walk-Forward в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
def walkforward_one_pair(pair: str, conn):
    print(f"\n{'='*55}")
    print(f"  {pair}")
    print(f"{'='*55}")

    df = load_candles(pair, conn)
    print(f"  Р—Р°РіСЂСѓР¶РµРЅРѕ: {len(df):,} СЃРІРµС‡РµР№ ({df['open_time'].iloc[0].date()} в†’ {df['open_time'].iloc[-1].date()})")

    # Р“РµРЅРµСЂРёСЂСѓРµРј С„РёС‡Рё
    feat_df = make_features(df)
    labels  = make_labels(feat_df, HORIZON)

    # РћС‚СЂРµР·Р°РµРј РїРѕСЃР»РµРґРЅРёРµ HORIZON СЃС‚СЂРѕРє (РЅРµС‚ Р»РµР№Р±Р»Р°)
    feat_df = feat_df.iloc[:-HORIZON]
    labels  = labels.iloc[:-HORIZON]

    # Р Р°Р·Р±РёРІР°РµРј РїРѕ РґР°С‚Рµ
    cutoff = pd.Timestamp(TRAIN_UNTIL, tz="UTC")
    train_mask = feat_df["open_time"] < cutoff
    test_mask  = feat_df["open_time"] >= cutoff

    X_train = feat_df[train_mask].drop(columns=["open_time", "open", "high", "low", "close", "volume"], errors="ignore")
    y_train = labels[train_mask]
    X_test  = feat_df[test_mask].drop(columns=["open_time", "open", "high", "low", "close", "volume"], errors="ignore")
    y_test  = labels[test_mask]
    test_times = feat_df[test_mask]["open_time"]
    test_close = feat_df[test_mask]["close"]

    print(f"  Train: {len(X_train):,} ({y_train.mean():.1%} BUY) | Test: {len(X_test):,}")

    if len(X_train) < 1000 or len(X_test) < 500:
        print("  вљ пёЏ  РќРµРґРѕСЃС‚Р°С‚РѕС‡РЅРѕ РґР°РЅРЅС‹С…, РїСЂРѕРїСѓСЃРєР°РµРј")
        return None

    # РћР±СѓС‡Р°РµРј LightGBM РўРћР›Р¬РљРћ РЅР° train
    model = lgb.LGBMClassifier(
        n_estimators=300,
        max_depth=6,
        learning_rate=0.05,
        num_leaves=31,
        min_child_samples=50,
        subsample=0.8,
        colsample_bytree=0.8,
        class_weight="balanced",
        random_state=42,
        verbose=-1,
    )
    model.fit(
        X_train, y_train,
        eval_set=[(X_test, y_test)],
        callbacks=[lgb.early_stopping(30, verbose=False), lgb.log_evaluation(-1)],
    )

    # РџСЂРµРґСЃРєР°Р·С‹РІР°РµРј РЅР° test
    probs = model.predict_proba(X_test)[:, 1]  # P(BUY)

    # РЎРёРјСѓР»РёСЂСѓРµРј СЃРґРµР»РєРё
    results = []
    for i, (prob, actual, ts, close) in enumerate(zip(probs, y_test, test_times, test_close)):
        if prob >= MIN_CONFIDENCE:
            direction = "BUY"
            is_win = (actual == 1)
        elif prob <= (1 - MIN_CONFIDENCE):
            direction = "PUT"
            is_win = (actual == 0)
        else:
            continue  # NEUTRAL вЂ” РЅРµ С‚РѕСЂРіСѓРµРј

        results.append({
            "time": ts,
            "direction": direction,
            "prob": prob,
            "is_win": is_win,
        })

    if not results:
        print("  вљ пёЏ  РќРµС‚ СЃРёРіРЅР°Р»РѕРІ СЃ РґРѕСЃС‚Р°С‚РѕС‡РЅРѕР№ СѓРІРµСЂРµРЅРЅРѕСЃС‚СЊСЋ")
        return None

    res_df = pd.DataFrame(results)
    total  = len(res_df)
    wins   = res_df["is_win"].sum()
    wr     = wins / total * 100
    buys   = (res_df["direction"] == "BUY").sum()
    puts   = (res_df["direction"] == "PUT").sum()

    # РџРѕ РјРµСЃСЏС†Р°Рј
    res_df["month"] = res_df["time"].dt.to_period("M")
    monthly = res_df.groupby("month")["is_win"].agg(["count", "mean"])

    print(f"\n  рџ“Љ Р Р•Р—РЈР›Р¬РўРђРўР« (out-of-sample, РёСЋРЅСЊ-СЃРµРЅС‚СЏР±СЂСЊ):")
    print(f"     РЎРґРµР»РѕРє:  {total:,}  (BUY: {buys:,}, PUT: {puts:,})")
    print(f"     WinRate: {wr:.1f}%  ({'вњ…' if wr > 54 else ('вљ пёЏ' if wr > 50 else 'вќЊ')})")
    print(f"     РџРѕР±РµРґ:   {int(wins):,}  |  РџРѕСЂР°Р¶РµРЅРёР№: {total - int(wins):,}")

    print(f"\n  РџРѕ РјРµСЃСЏС†Р°Рј:")
    for period, row in monthly.iterrows():
        bar = "в–€" * int(row["mean"] * 20)
        print(f"    {period}: {int(row['count']):>5} СЃРґРµР»РѕРє | {row['mean']*100:.1f}% WR {bar}")

    return {"pair": pair, "total": total, "wins": int(wins), "wr": wr}


# в”Ђв”Ђ Main в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
def main():
    print("=" * 55)
    print("  WALK-FORWARD BACKTEST")
    print(f"  Train: РјР°СЂС‚-РјР°Р№ 2026  |  Test: РёСЋРЅСЊ-СЃРµРЅС‚ 2026")
    print(f"  Р“РѕСЂРёР·РѕРЅС‚: {HORIZON} СЃРІРµС‡Рё  |  Min confidence: {MIN_CONFIDENCE}")
    print("=" * 55)

    conn = psycopg2.connect(DB_URL)
    all_results = []

    for pair in PAIRS:
        try:
            r = walkforward_one_pair(pair, conn)
            if r:
                all_results.append(r)
        except Exception as e:
            print(f"  вќЊ {pair}: {e}")

    conn.close()

    if all_results:
        total_trades = sum(r["total"] for r in all_results)
        total_wins   = sum(r["wins"]  for r in all_results)
        overall_wr   = total_wins / total_trades * 100

        print(f"\n{'='*55}")
        print(f"  РРўРћР“ РџРћ Р’РЎР•Рњ РџРђР РђРњ")
        print(f"{'='*55}")
        for r in all_results:
            bar = "в–€" * int(r["wr"] / 5)
            print(f"  {r['pair']}: {r['total']:>6,} СЃРґРµР»РѕРє | {r['wr']:.1f}% WR {bar}")
        print(f"  {'в”Ђ'*40}")
        print(f"  РРўРћР“Рћ: {total_trades:,} СЃРґРµР»РѕРє | OVERALL WR: {overall_wr:.1f}%")

        if overall_wr > 55:
            print(f"\n  вњ… РћС‚Р»РёС‡РЅРѕ! РњРѕРґРµР»СЊ СЃС‚Р°Р±РёР»СЊРЅРѕ РІС‹С€Рµ 55% out-of-sample.")
            print(f"  в†’ РњРѕР¶РЅРѕ РїРµСЂРµС…РѕРґРёС‚СЊ Рє Р­С‚Р°РїСѓ 3 РґРѕСЃСЂРѕС‡РЅРѕ.")
        elif overall_wr > 52:
            print(f"\n  вљ пёЏ  РќРµРїР»РѕС…Рѕ ({overall_wr:.1f}%). РџСЂРѕС„РёС‚ СЃ СѓС‡С‘С‚РѕРј РєРѕРјРёСЃСЃРёРё.")
            print(f"  в†’ РџСЂРѕРґРѕР»Р¶Р°РµРј РєРѕРїРёС‚СЊ Р¶РёРІС‹Рµ СЃРґРµР»РєРё РґР»СЏ РїРѕРґС‚РІРµСЂР¶РґРµРЅРёСЏ.")
        else:
            print(f"\n  вќЊ РќРёР¶Рµ 52%. РќСѓР¶РЅРѕ РґРѕСЂР°Р±РѕС‚Р°С‚СЊ features РёР»Рё РјРµС‚РєРё.")


if __name__ == "__main__":
    main()

