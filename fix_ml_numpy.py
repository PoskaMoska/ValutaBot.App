import os
import re

file = 'ml_service/model.py'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

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
            
            closes_np = closes_s.values
            highs_np = highs_s.values
            lows_np = lows_s.values
            barrier_np = barrier_width.values
            
            # Vectorized Triple-Barrier Scan (Numpy Optimized)
            for i in range(len(candles) - H):
                close_t = closes_np[i]
                width = barrier_np[i]
                
                # Barriers
                ub = close_t * (1.0 + width)
                lb = close_t * (1.0 - width)
                
                window_highs = highs_np[i+1 : i+1+H]
                window_lows = lows_np[i+1 : i+1+H]
                
                hit_ub = window_highs > ub
                hit_lb = window_lows < lb
                
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
                    future_c = closes_np[i+H]
                    target_raw[i] = 1 if future_c > close_t else 0
                    # Penalize magnitude weight for non-committal moves
                    magnitude_weight[i] = 0.5
'''

text = re.sub(r'# NEW: Phase 3 - Triple-Barrier Method \+ Smart Magnitude Weights.*?magnitude_weight\[i\] = 0\.5', new_target_logic, text, flags=re.DOTALL)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

