import os
from model import ForexPredictor

def retrain():
    pairs = ["EURUSD", "GBPUSD", "USDJPY", "EURUSD_OTC"]
    intervals = ["1m", "5m", "15m"]
    
    for pair in pairs:
        for interval in intervals:
            print(f"--- Training {pair} {interval} ---")
            try:
                predictor = ForexPredictor(pair, interval)
                result = predictor.train(candles=None)
                print(f"RESULT: {result}")
            except Exception as e:
                print(f"ERROR: {e}")

if __name__ == '__main__':
    retrain()
