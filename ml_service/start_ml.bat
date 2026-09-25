@echo off
if not defined TwelveDataApiKey set TwelveDataApiKey=3e0d610500f0414282d471471f59504e
if not defined TWELVE_DATA_API_KEY set TWELVE_DATA_API_KEY=%TwelveDataApiKey%
set TARGET_HORIZON_CANDLES=3
set MIN_CONFIDENCE=0.51
set RETRAIN_INTERVAL_H=24
set DATABASE_URL=postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway
cd /d "%~dp0"
py main.py
