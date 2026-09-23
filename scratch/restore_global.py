import re

# 1. FIX BACKTESTER
with open("ml_service/backtester.py", "r", encoding="utf-8") as f:
    code = f.read()

new_funcs = """def get_window_params(interval):
    interval = interval.lower()
    if interval in ("5s", "s5", "10s", "s10", "15s", "s15"):
        # Global Strategist: Retrain once a week (120960), Memory ~17 days (300000)
        return 300000, 120960
    elif interval in ("1m", "m1"):
        # Global Strategist: Retrain once a week (10080), Memory ~4 weeks (40320)
        return 40320, 10080
    elif interval in ("5m", "m5"):
        # Global Strategist: Retrain once a week (2016), Memory ~3 months (25000)
        return 25000, 2016
    elif interval in ("15m", "m15"):
        # Global Strategist: Retrain once a week (672), Memory ~6 months (17000)
        return 17000, 672
    else:
        return 300000, 120960"""

# Regex replace get_window_params
pattern = re.compile(r"def get_window_params\(interval\):.*?return 1500, 200", re.DOTALL)
code = pattern.sub(new_funcs, code)

with open("ml_service/backtester.py", "w", encoding="utf-8") as f:
    f.write(code)

# 2. FIX MODEL.PY
with open("ml_service/model.py", "r", encoding="utf-8") as f:
    code = f.read()

old_limit_code = """                interval_lower = self.interval.lower()
                if interval_lower.startswith("s"):
                    target_candles = 17280  # 24 hours for 5s
                elif interval_lower in ("1m", "m1"):
                    target_candles = 10080  # 7 days for 1m
                elif interval_lower in ("5m", "m5"):
                    target_candles = 8640   # 30 days for 5m
                elif interval_lower in ("15m", "m15"):
                    target_candles = 5760   # 60 days for 15m
                else:
                    target_candles = 3000   # Default fallback"""

new_limit_code = """                interval_lower = self.interval.lower()
                if interval_lower.startswith("s"):
                    target_candles = 300000  # ~17 days for Global Strategist memory
                elif interval_lower in ("1m", "m1"):
                    target_candles = 40320   # ~4 weeks for 1m
                elif interval_lower in ("5m", "m5"):
                    target_candles = 25000   # ~3 months for 5m
                elif interval_lower in ("15m", "m15"):
                    target_candles = 17000   # ~6 months for 15m
                else:
                    target_candles = 300000  # Default fallback"""

code = code.replace(old_limit_code, new_limit_code)

with open("ml_service/model.py", "w", encoding="utf-8") as f:
    f.write(code)

print("Restored Global Strategist params")
