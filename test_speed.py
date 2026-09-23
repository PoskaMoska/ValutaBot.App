import pandas as pd
import numpy as np
import time

N = 10000
H = 5
closes_s = pd.Series(np.random.rand(N))
highs_s = pd.Series(np.random.rand(N) + 1.0)
lows_s = pd.Series(np.random.rand(N) - 1.0)
barrier_width = pd.Series(np.random.rand(N) * 0.01)

target_raw = np.zeros(N)
magnitude_weight = np.ones(N)

start = time.time()
closes_np = closes_s.values
highs_np = highs_s.values
lows_np = lows_s.values
barrier_np = barrier_width.values

for i in range(N - H):
    close_t = closes_np[i]
    width = barrier_np[i]
    
    ub = close_t * (1.0 + width)
    lb = close_t * (1.0 - width)
    
    window_highs = highs_np[i+1 : i+1+H]
    window_lows = lows_np[i+1 : i+1+H]
    
    hit_ub = window_highs > ub
    hit_lb = window_lows < lb
    
    ub_idx = np.argmax(hit_ub) if hit_ub.any() else H + 1
    lb_idx = np.argmax(hit_lb) if hit_lb.any() else H + 1
    
    if ub_idx < lb_idx:
        target_raw[i] = 1
        magnitude_weight[i] = 1.0
    elif lb_idx < ub_idx:
        target_raw[i] = 0
        magnitude_weight[i] = 1.0
    else:
        future_c = closes_np[i+H]
        target_raw[i] = 1 if future_c > close_t else 0
        magnitude_weight[i] = 0.5
print(f'Numpy array approach: {time.time() - start:.4f}s')

start = time.time()
for i in range(N - H):
    close_t = closes_s.iloc[i]
    width = barrier_width.iloc[i]
    ub = close_t * (1.0 + width)
    lb = close_t * (1.0 - width)
    window_highs = highs_s.iloc[i+1 : i+1+H]
    window_lows = lows_s.iloc[i+1 : i+1+H]
    hit_ub = (window_highs > ub).values
    hit_lb = (window_lows < lb).values
    ub_idx = np.argmax(hit_ub) if hit_ub.any() else H + 1
    lb_idx = np.argmax(hit_lb) if hit_lb.any() else H + 1
print(f'Pandas iloc approach: {time.time() - start:.4f}s')
