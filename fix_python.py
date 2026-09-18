import json
import re

def rewrite_python():
    with open('ml_service/main.py', 'r', encoding='utf-8') as f:
        c = f.read()
    c = re.sub(r'from model import[^;]+;\s*', '', c)
    c = 'from model import predict, predict_variance\nfrom tactician import partial_fit_online\n' + c
    c = re.sub(r'from data_processor import build_features\s*', 'from data_processor import build_features\n', c)
    c = re.sub(r'_live_candles_cache = \{\}\s*', '', c)
    c = re.sub(r'def predict\(', 'def predict(', c) # Not touching imports if they are already at top
    with open('ml_service/main.py', 'w', encoding='utf-8') as f:
        f.write(c)

rewrite_python()
