# AUDIT 4: MACHINE LEARNING PIPELINE, DATA LEAKAGE & TIME-SERIES INTEGRITY (v5)

## РОЛЬ:
Ты — Senior Quantitative ML Engineer & Time-Series Specialist.
Твоя задача — провести глубокий математический и инженерный аудит конвейера машинного обучения проекта ValutaBot.App (Python, PyTorch, PatchTST Transformer).

## КОНТЕКСТ СИСТЕМЫ И ИНВАРИАНТЫ (НЕ СЧИТАТЬ ДЕФЕКТАМИ):
- Архитектура: DualStreamPatchBrain (PatchTST Transformer v4) с кросс-вниманием между микро-свечами (160 свечей) и MTF-контекстом (30 свечей), плюс антисимметричный сиамский слой считывания (Siamese Sign-Symmetry).
- DualStreamOnnxService корректно освобождает нативный SRWLock в блоке finally метода Dispose().
- MLPythonService использует volatile CancellationTokenSource для потокобезопасного вотчдога на ARM64/x86_64 и не мутирует таймаут на общем пуле HttpClient (используется CreateLinkedTokenSource).
- Uvicorn в ml_service/main.py сконфигурирован с limit_max_requests=50_000 и timeout_keep_alive=30 для предотвращения фрагментации RSS памяти CPython.
- ИНВАРИАНТ C# SRE СТЫКА: Сделки, отмененные по таймауту (>120с), сохраняются с was_win = null и фильтруются через WHERE was_win IS NOT NULL — они не попадают в класс 0 (HOLD).

## ФОКУС РАССЛЕДОВАНИЯ:
- `ml_service/export_dataset.py`
- `ml_service/train_brain.py`
- `ml_service/main.py`
- `ml_service/models/patch_tst_brain.py`
- `ml_service/models/patch_tst_predictor.py`
- `MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs` (стык генерации признаков)

## ЧТО ИСКАТЬ:
1. TARGET DISTRIBUTION & ZERO RECALL ON FLAT/CHOP:
   - Проанализируй логику формирования таргетов в export_dataset.py (порог min_pnl_threshold): почему модель может испытывать дефицит примеров или нулевой recall на флэте?
   - Учитывается ли комиссия и асимметрия выплат брокера Pocket Option (выигрыш 80–92%, проигрыш 100%) в функции потерь (Weighted FocalLoss / Class Weights)?
2. TRAIN-SERVE FEATURE PARITY:
   - Проверь 8 каналов нормализации свечей: полностью ли совпадают формулы приращений (bps), порядок каналов и обработка нулевых дельт между export_dataset.py и patch_tst_predictor.py (normalize_candles_live)?
   - Проверь макро-вектор из 31 признака (DXY, уровни, волатильность): нет ли рассинхронизации между C# Orchestrator и Python экспортером датасета?
3. DATA LEAKAGE:
   - Проверь метод candle_store.get_candles_before в export_dataset.py: исключено ли попадание свечи входа или будущих свечей в историческое окно инференса?
4. INFERENCE LATENCY & MEMORY:
   - Нет ли накопления тензоров PyTorch на GPU/CPU при непрерывном инференсе 24/7 (отсутствие torch.no_grad() или неудаленные графы вычислений)?

## ПРАВИЛО:
Проверяй актуальный код в репозитории.

## ФОРМАТ ОТЧЕТА:
- CONFIRMED ML LEAKS & DEFECTS: Баги датасета, утечки и расхождения Train/Serve.
- MATHEMATICAL FLAWS: Недостатки разметки, функции потерь и калибровки.
- PYTHON CODE FIXES: Точные исправления кода.
