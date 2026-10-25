@echo off
setlocal
cd /d "%~dp0"
echo ===============================================================
echo [*] ValutaBot Neural Brain V4: 1-Click Retraining Pipeline
echo ===============================================================
py ml_service\auto_retrain.py %*
pause
