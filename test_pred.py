import requests
res = requests.post("http://localhost:8080/predict", json={
    "symbol": "EURUSD", 
    "interval": "1m",
    "candles": [{"openTime": 1720000000+i*60, "open": 1.1, "high": 1.1, "low": 1.1, "close": 1.1, "volume": 100} for i in range(100)]
})
print(res.json())
