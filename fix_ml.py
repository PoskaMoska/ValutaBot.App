import os
import re

file = 'ml_service/model.py'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

# I want to replace the target logic from line 855:
# # NEW: Strict Time-Barrier Target + Smart Magnitude Weights
# H = TARGET_HORIZON_CANDLES
# closes_s = pd.Series([cl["close"] for cl in candles])
# future_close = closes_s.shift(-H)
# target_raw = (future_close > closes_s).astype(int).values

new_target_logic = '''
            # NEW: Phase 3 - Triple-Barrier Method + Smart Magnitude Weights
            H = TARGET_HORIZON_CANDLES
            
            # Use pandas directly for vectorized operations
            closes_s = pd.Series([cl["close"] for cl in candles])
            highs_s = pd.Series([cl["high"] for cl in candles])
            lows_s = pd.Series([cl["low"] for cl in candles])
            
            # Calculate dynamic volatility for barrier width
            abs_return = np.abs(np.log(closes_s / closes_s.shift(1).fillna(closes_s.iloc[0])))
            local_vol = abs_return.ewm(span=1000, min_periods=1).mean()
            
            # Define barrier width (1.5x local volatility)
            barrier_width = local_vol * 1.5
            
            target_raw = np.zeros(len(candles))
            magnitude_weight = np.ones(len(candles))
            
            # Vectorized Triple-Barrier Scan
            for i in range(len(candles) - H):
                close_t = closes_s.iloc[i]
                width = barrier_width.iloc[i]
                
                # Barriers
                ub = close_t * (1.0 + width)
                lb = close_t * (1.0 - width)
                
                window_highs = highs_s.iloc[i+1 : i+1+H]
                window_lows = lows_s.iloc[i+1 : i+1+H]
                
                hit_ub = (window_highs > ub).values
                hit_lb = (window_lows < lb).values
                
                ub_idx = np.argmax(hit_ub) if hit_ub.any() else H + 1
                lb_idx = np.argmax(hit_lb) if hit_lb.any() else H + 1
                
                if ub_idx < lb_idx:
                    target_raw[i] = 1 # Buy wins (Take Profit hit first)
                    magnitude_weight[i] = 1.0
                elif lb_idx < ub_idx:
                    target_raw[i] = 0 # Put wins (Stop Loss hit first)
                    magnitude_weight[i] = 1.0
                else:
                    # Time barrier hit (neither TP nor SL was hit before H candles)
                    future_c = closes_s.iloc[i+H]
                    target_raw[i] = 1 if future_c > close_t else 0
                    # Penalize magnitude weight for non-committal moves
                    magnitude_weight[i] = 0.5
'''

text = re.sub(r'# NEW: Strict Time-Barrier Target \+ Smart Magnitude Weights.*?magnitude_weight = np\.clip\(magnitude_weight, 0\.1, 5\.0\)\.fillna\(1\.0\)\.values', new_target_logic, text, flags=re.DOTALL)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

