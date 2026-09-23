import requests
import json

candles = [{"openTime": 1720000000+i*60, "open": 1.1, "high": 1.1, "low": 1.1, "close": 1.1, "volume": 100} for i in range(60)]
try:
    res = requests.post("http://localhost:8765/predict", json={
        "symbol": "EURUSD_OTC", 
        "interval": "1m",
        "candles": candles
    }, timeout=5)
    print("Ответ:", res.text)
except Exception as e:
    print("Ошибка подключения:", e)
