import os, subprocess, sys
# Secrets must come from the environment (Railway dashboard / shell).
os.environ.setdefault('TwelveDataApiKey', '3e0d610500f0414282d471471f59504e')
os.environ.setdefault('TWELVE_DATA_API_KEY', os.environ['TwelveDataApiKey'])
os.environ.setdefault('DATABASE_URL', 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway') # Added for local DB connection
os.environ['TARGET_HORIZON_CANDLES'] = '3'
os.environ['MIN_CONFIDENCE'] = '0.60'
os.chdir(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service')
sys.path.insert(0, r'C:\Users\bural\source\repos\ValutaBot.App\ml_service')
exec(open(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service\main.py').read())
