import re

with open("ml_service/model.py", "r", encoding="utf-8") as f:
    code = f.read()

# Replace adaptive limit calculation
old_limit_code = """                if self.interval == "5m":
                    target_candles = max(MAX_HISTORICAL_CANDLES // 5, 20000)
                elif self.interval == "15m":
                    target_candles = max(MAX_HISTORICAL_CANDLES // 15, 20000)
                elif self.interval.startswith("s"):
                    target_candles = 300000  # Allow full subminute history without truncation
                else:
                    target_candles = max(MAX_HISTORICAL_CANDLES, 20000)"""

new_limit_code = """                interval_lower = self.interval.lower()
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

code = code.replace(old_limit_code, new_limit_code)

# Let's fix the 1500 limit cap in DB fetch in model.py
old_db_check = "if not df.empty and len(df) >= min(limit, 1500) * 0.1:"
new_db_check = "if not df.empty and len(df) >= min(limit, 3000) * 0.1:"
code = code.replace(old_db_check, new_db_check)

with open("ml_service/model.py", "w", encoding="utf-8") as f:
    f.write(code)

print("Done model")
