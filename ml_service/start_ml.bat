@echo off
if not defined TWELVE_DATA_API_KEY if defined TwelveDataApiKey set TWELVE_DATA_API_KEY=%TwelveDataApiKey%
set TARGET_HORIZON_CANDLES=1
set MIN_CONFIDENCE=0.51
set RETRAIN_INTERVAL_H=24
if not defined DATABASE_URL (echo [ERROR] DATABASE_URL is not set. Export it before starting. & exit /b 1)
cd /d "%~dp0"
py main.py

