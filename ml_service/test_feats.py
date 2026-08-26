from features import build_features
import pandas as pd
import numpy as np

candles = [{'open': 1.1, 'high': 1.2, 'low': 1.0, 'close': 1.1, 'volume': 100, 'opentime': 1720000000+i*60} for i in range(100)]
df = build_features(candles)
print(f"Num features: {len(df.columns)}")
print(df.columns.tolist())
