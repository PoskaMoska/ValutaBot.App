"""
Feature engineering for Forex/Crypto LightGBM predictor.
Takes OHLCV candle arrays and returns a feature DataFrame.
"""

import numpy as np
import pandas as pd
from typing import List, Dict
import ta
from datetime import datetime, timezone

def _rolling_std(close: np.ndarray, period: int = 10) -> np.ndarray:
    return pd.Series(close).rolling(period).std().values


def _volume_ma(volume: np.ndarray, period: int = 20) -> np.ndarray:
    return pd.Series(volume).rolling(period).mean().values


def _linreg_slope(close: np.ndarray, period: int = 20) -> np.ndarray:
    """Rolling linear regression slope (normalized by price) using Pandas vectorization."""
    s = pd.Series(close)
    x = np.arange(period, dtype=float)
    x_centered = x - x.mean()
    var_x = (x_centered ** 2).sum() + 1e-10

    def calc_slope(y):
        y_mean = y.mean()
        slope = (x_centered * (y - y_mean)).sum() / var_x
        return slope / (y_mean + 1e-10)
        
    return s.rolling(period).apply(calc_slope, raw=True).fillna(0.0).values


def _hurst_approx(close: np.ndarray, lag_max: int = 16) -> np.ndarray:
    """Approximate rolling Hurst exponent (simplified, window=lag_max*2)."""
    s = pd.Series(close)
    win = lag_max * 2
    
    diff2 = s.diff(2)
    diff16 = s.diff(16)
    
    std2 = diff2.rolling(win).std() + 1e-12
    std16 = diff16.rolling(win).std() + 1e-12
    
    h = np.log(std16 / std2) / np.log(8)
    return h.clip(0.0, 1.0).fillna(0.5).values


def _order_flow_features(o: np.ndarray, h: np.ndarray, lo: np.ndarray, c: np.ndarray, v: np.ndarray, vol_ma: np.ndarray) -> tuple:
    candle_range = (h - lo) + 1e-10
    buy_ratio = (c - lo) / candle_range
    sell_ratio = (h - c) / candle_range
    
    buy_vol = v * buy_ratio
    sell_vol = v * sell_ratio
    
    delta_ratio = buy_vol / (sell_vol + 1e-10)
    
    # Block trade anomaly: 1 if volume > 1.7x MA, else 0
    block_trade = (v > (vol_ma * 1.7)).astype(float)
    
    return buy_vol, sell_vol, delta_ratio, block_trade


def _fvg_features(h: np.ndarray, lo: np.ndarray) -> tuple:
    """Fair Value Gaps: Returns arrays for Bullish and Bearish FVG sizes via NumPy vectorization."""
    fvg_bullish = np.zeros(len(h))
    fvg_bearish = np.zeros(len(h))
    
    if len(h) < 3:
        return fvg_bullish, fvg_bearish
        
    bullish_mask = lo[2:] > h[:-2]
    fvg_bullish[2:][bullish_mask] = lo[2:][bullish_mask] - h[:-2][bullish_mask]
    
    bearish_mask = h[2:] < lo[:-2]
    fvg_bearish[2:][bearish_mask] = lo[:-2][bearish_mask] - h[2:][bearish_mask]
            
    return fvg_bullish, fvg_bearish


def build_features(candles: List[Dict]) -> pd.DataFrame:
    """
    Build feature matrix from list of OHLCV candle dicts.
    Each dict: {'open', 'high', 'low', 'close', 'volume'}
    Returns DataFrame with one row per candle, features only (no NaN rows).
    """
    df = pd.DataFrame(candles)
    df.columns = [c.lower() for c in df.columns]

    o = df['open'].values.astype(float)
    h = df['high'].values.astype(float)
    lo = df['low'].values.astype(float)
    c = df['close'].values.astype(float)
    v = df['volume'].values.astype(float)

    feats = {}

    # в”Ђв”Ђ Trend / Momentum (Using ta library) в”Ђв”Ђ
    df['ema9'] = ta.trend.ema_indicator(df['close'], window=9)
    df['ema21'] = ta.trend.ema_indicator(df['close'], window=21)
    df['ema50'] = ta.trend.ema_indicator(df['close'], window=50)
    
    feats['ema9']       = df['ema9'].values
    feats['ema21']      = df['ema21'].values
    feats['ema50']      = df['ema50'].values
    feats['ema_ratio_9_21']  = feats['ema9'] / (feats['ema21'] + 1e-10) - 1
    feats['close_vs_ema9']   = c / (feats['ema9'] + 1e-10) - 1
    feats['close_vs_ema21']  = c / (feats['ema21'] + 1e-10) - 1
    feats['close_vs_ema50']  = c / (feats['ema50'] + 1e-10) - 1

    macd = ta.trend.MACD(df['close'], window_slow=26, window_fast=12, window_sign=9)
    macd_line = macd.macd().values
    macd_hist = macd.macd_diff().values
    
    feats['macd']       = macd_line / (np.abs(c) + 1e-10)
    feats['macd_hist']  = macd_hist / (np.abs(c) + 1e-10)

    feats['linreg_slope'] = _linreg_slope(c, 20)
    feats['hurst']        = _hurst_approx(c, 16)

    # в”Ђв”Ђ Oscillators (Using ta library) в”Ђв”Ђ
    feats['rsi14'] = ta.momentum.rsi(df['close'], window=14).values / 100.0 - 0.5
    feats['rsi7']  = ta.momentum.rsi(df['close'], window=7).values / 100.0 - 0.5
    
    bb = ta.volatility.BollingerBands(df['close'], window=20, window_dev=2)
    bb_mavg = bb.bollinger_mavg().values
    bb_std = pd.Series(c).rolling(20).std().values
    feats['bb_z']  = ((c - bb_mavg) / (bb_std + 1e-10))

    # в”Ђв”Ђ Volatility в”Ђв”Ђ
    atr = ta.volatility.average_true_range(df['high'], df['low'], df['close'], window=14).values
    feats['atr_norm']     = atr / (c + 1e-10)
    feats['rolling_std']  = _rolling_std(c, 10) / (c + 1e-10)

    # в”Ђв”Ђ Price Returns в”Ђв”Ђ
    for lag in [1, 2, 3, 5, 10]:
        ret = np.zeros(len(c))
        ret[lag:] = (c[lag:] - c[:-lag]) / (c[:-lag] + 1e-10)
        feats[f'ret{lag}'] = ret

    # в”Ђв”Ђ Candle Structure в”Ђв”Ђ
    candle_range = (h - lo) + 1e-10
    feats['body_ratio']    = np.abs(c - o) / candle_range
    feats['upper_wick']    = (h - np.maximum(o, c)) / candle_range
    feats['lower_wick']    = (np.minimum(o, c) - lo) / candle_range
    feats['candle_dir']    = np.sign(c - o)

    # в”Ђв”Ђ Volume & Order Flow & SMC в”Ђв”Ђ
    vol_ma = _volume_ma(v, 20)
    feats['vol_ratio']     = v / (vol_ma + 1e-10)
    # FIX: Р±С‹Р»Р° Look-Ahead Bias вЂ” vol_ma.mean() СЃС‡РёС‚Р°Р» РіР»РѕР±Р°Р»СЊРЅРѕРµ СЃСЂРµРґРЅРµРµ РїРѕ РІСЃРµРјСѓ РґР°С‚Р°СЃРµС‚Сѓ,
    # РІРєР»СЋС‡Р°СЏ Р±СѓРґСѓС‰РёРµ СЃРІРµС‡Рё. РўРµРїРµСЂСЊ РЅРѕСЂРјР°Р»РёР·Р°С†РёСЏ РёРґС‘С‚ РїРѕ rolling СЃСЂРµРґРЅРµРјСѓ (С‚РѕР»СЊРєРѕ РїСЂРѕС€Р»РѕРµ).
    rolling_vol_mean = pd.Series(vol_ma).rolling(20, min_periods=1).mean().values
    feats['vol_ma']        = vol_ma / (rolling_vol_mean + 1e-10)
    
    buy_vol, sell_vol, delta_ratio, block_trade = _order_flow_features(o, h, lo, c, v, vol_ma)
    feats['of_buy_vol_norm'] = buy_vol / (vol_ma + 1e-10)
    feats['of_sell_vol_norm'] = sell_vol / (vol_ma + 1e-10)
    feats['of_delta_ratio'] = np.clip(delta_ratio, 0.0, 5.0)  # clip to avoid extreme outliers
    feats['of_block_trade'] = block_trade
    
    # Rolling Delta Ratio
    rolling_buy = pd.Series(buy_vol).rolling(5).sum().values
    rolling_sell = pd.Series(sell_vol).rolling(5).sum().values
    feats['of_rolling_delta_5'] = np.clip(rolling_buy / (rolling_sell + 1e-10), 0.0, 5.0)
    
    # Fair Value Gaps
    fvg_bull, fvg_bear = _fvg_features(h, lo)
    feats['smc_fvg_bullish'] = fvg_bull / (c + 1e-10)
    feats['smc_fvg_bearish'] = fvg_bear / (c + 1e-10)

    # в”Ђв”Ђ High/Low channel position в”Ђв”Ђ
    high20 = pd.Series(h).rolling(20).max().values
    low20  = pd.Series(lo).rolling(20).min().values
    range20 = high20 - low20 + 1e-10
    feats['channel_pos'] = (c - low20) / range20

    # в”Ђв”Ђ Time / Session (sinusoidal encoding so hour=23 is close to hour=0) в”Ђв”Ђ
    if 'opentime' in df.columns:
        def get_hour(ts):
            if pd.isna(ts) or ts == 0: return 0.0
            if isinstance(ts, str):
                if ts.isdigit():
                    ts = int(ts)
                else:
                    try:
                        dt = datetime.strptime(ts, "%Y-%m-%d %H:%M:%S")
                    except ValueError:
                        dt = pd.to_datetime(ts)
                    return dt.hour + dt.minute / 60.0
                
            # convert from milliseconds if necessary
            if ts > 1e11: ts = ts / 1000.0
            dt = datetime.fromtimestamp(ts, tz=timezone.utc)
            return dt.hour + dt.minute / 60.0
        
        hours = df['opentime'].apply(get_hour)
        feats['hour_sin'] = np.sin(2 * np.pi * hours / 24.0)
        feats['hour_cos'] = np.cos(2 * np.pi * hours / 24.0)
    else:
        feats['hour_sin'] = np.zeros(len(c))
        feats['hour_cos'] = np.zeros(len(c))

    # Remove raw unscaled prices (lethal for SGD gradient descent)
    for k in ['ema9', 'ema21', 'ema50']:
        if k in feats: feats.pop(k)

    result = pd.DataFrame(feats, index=df.index)

    # Slice off initial rolling warmup window (first 25 rows) and safely fill any residual NaNs with 0.0
    result = result.iloc[25:].fillna(0.0)

    return result

