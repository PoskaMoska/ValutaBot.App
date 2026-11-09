import os, subprocess, sys
# Secrets must come from the environment (Railway dashboard / shell).
if 'TWELVE_DATA_API_KEY' not in os.environ and 'TwelveDataApiKey' in os.environ:
    os.environ['TWELVE_DATA_API_KEY'] = os.environ['TwelveDataApiKey']
# DATABASE_URL must be provided via the environment (no embedded credentials).
os.environ['TARGET_HORIZON_CANDLES'] = '1'
os.environ['MIN_CONFIDENCE'] = '0.60'
os.chdir(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service')
sys.path.insert(0, r'C:\Users\bural\source\repos\ValutaBot.App\ml_service')
exec(open(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service\main.py', encoding='utf-8').read())

