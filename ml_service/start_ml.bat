@echo off
if not defined TwelveDataApiKey set TwelveDataApiKey=CHANGE_ME_SET_VIA_ENV
if not defined TWELVE_DATA_API_KEY set TWELVE_DATA_API_KEY=%TwelveDataApiKey%
set TARGET_HORIZON_CANDLES=5
set MIN_CONFIDENCE=0.60
set RETRAIN_INTERVAL_H=24
cd /d "%~dp0"
py main.py
