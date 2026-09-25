import os
from model import ForexPredictor

def retrain():
    pairs = ["EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF"]
    intervals = ["1m"]
    regimes = ["ALL", "FLAT", "TREND", "CHAOS"]
    
    for pair in pairs:
        for interval in intervals:
            for regime in regimes:
                print(f"--- Training {pair} {interval} [{regime}] ---")
                try:
                    predictor = ForexPredictor(pair, interval, regime)
                    result = predictor.train(candles=None)
                    print(f"RESULT: {result}")
                except Exception as e:
                    print(f"ERROR: {e}")

if __name__ == '__main__':
    retrain()
