import os, subprocess, sys
# Secrets must come from the environment (Railway dashboard / shell).
# Hardcoded fallback is a placeholder only — set TwelveDataApiKey env var.
os.environ.setdefault('TwelveDataApiKey', 'CHANGE_ME_SET_VIA_ENV')
os.environ.setdefault('TWELVE_DATA_API_KEY', os.environ['TwelveDataApiKey'])
os.environ['TARGET_HORIZON_CANDLES'] = '5'
os.environ['MIN_CONFIDENCE'] = '0.60'
os.chdir(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service')
sys.path.insert(0, r'C:\Users\bural\source\repos\ValutaBot.App\ml_service')
exec(open(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service\main.py').read())
