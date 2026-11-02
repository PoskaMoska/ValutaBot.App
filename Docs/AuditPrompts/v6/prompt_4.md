# AUDIT 4: MACHINE LEARNING PIPELINE, PYTORCH/TORCHSCRIPT, TRAIN-SERVE PARITY & ASYMMETRIC LOSS (v6)

## РОЛЬ:
Ты — Principal Quantitative ML Engineer & Deep Learning Time-Series Specialist (PyTorch, TorchScript, FastAPI).
Твоя задача — провести глубокий математический и инженерный аудит конвейера инференса и обучения нейросети проекта ValutaBot.App.

## КОНТЕКСТ СИСТЕМЫ:
- Архитектура: `DualStreamPatchBrain` (PatchTST Transformer v4) с кросс-вниманием между микро-свечами (160 свечей) и MTF-контекстом (30 свечей), сиамским симметричным считыванием (Siamese Sign-Symmetry) и 31 макро-признаком.
- Инференс: Python 3.12 / FastAPI на порту 8765, вызывается из C# через HTTP (`MLPythonService`).
- Окружение: CPU на серверах Railway (4 vCPU).

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ И РЕШЕНИЯ РАУНДА 5 (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **TorchScript JIT Freeze:** Модель компилируется через `torch.jit.trace` с последующим `torch.jit.freeze`, что ускорило инференс на CPU в 1.6 раза (с 12.15 мс до 7.58 мс) без сторонних зависимостей.
2. **torch.inference_mode():** Все предсказания выполняются в контексте `torch.inference_mode()` (без накладных расходов autograd).
3. **Изолированный ThreadPoolExecutor:** В `main.py` инференс вынесен в выделенный пул `ThreadPoolExecutor(max_workers=8)` с ограниченной очередью (`queue.Queue(maxsize=32)`). Event Loop FastAPI защищен от голодания, таймауты HTTP 504 и подвисания `/health` устранены.
4. **Time-series Inversion Guard:** В `normalize_candles_live` внедрена строгая проверка и сортировка: свечи всегда подаются строго хронологически (от старых к новым). Подача инвертированных свечей физически невозможна.
5. **Потокобезопасная инициализация:** Загрузка модели в `_load_model()` защищена через `threading.Lock()`.
6. **Совместимость DTO:** Ответ сервиса поддерживает алиасы (`probability`, `version`, `ml_score`, `lgbm_direction`).

## ФОКУС РАССЛЕДОВАНИЯ:
- `ml_service/models/patch_tst_predictor.py`
- `ml_service/models/patch_tst_brain.py`
- `ml_service/main.py`
- `ml_service/export_dataset.py`
- `ml_service/train_brain.py`
- `MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs` (стык признаков)

## ЧТО ИСКАТЬ (DEEP AUDIT V6):
1. **АСИММЕТРИЯ ВЫПЛАТ POCKET OPTION И ФУНКЦИЯ ПОТЕРЬ:**
   - В бинарных опционах выплата при победе составляет 80–92%, а убыток при проигрыше — 100%. Математическое ожидание при винрейте 50% строго отрицательное (минус 8–20% на сделку).
   - Учитывается ли эта асимметрия в функции потерь (Cost-sensitive learning / Asymmetric Weighted Focal Loss) в `train_brain.py`? Как штрафуются ложноположительные сигналы (False Positives) по сравнению с пропущенными сделками?
2. **TRAIN-SERVE FEATURE PARITY (СТЫК 8 КАНАЛОВ И 31 МАКРО-ПРИЗНАКА):**
   - Проверь формулы расчета 8 каналов приращений (bps) в `export_dataset.py` и в live-инференсе `patch_tst_predictor.py`: идентичны ли формулы логарифмических доходностей, волатильности и нормализации объемов?
   - Нет ли рассинхрона в порядке следования 31 макро-признака между C# генератором в `MarketAnalysisOrchestrator` и Python скриптами?
3. **CONCEPT & COVARIATE DRIFT ПРИ СМЕНЕ ВОЛАТИЛЬНОСТИ:**
   - Как модель ведет себя при резкой смене рыночного режима (например, переход от тихого ночного флэта к американской сессии)? Не возникает ли overconfidence (ложной сверхуверенности) при незнакомых комбинациях признаков?
   - Требуется ли калибровка выходных вероятностей (Temperature Scaling / Platt Scaling) для приведения сырого софтмакса к эмпирическому винрейту?

## СТРОГИЕ ПРАВИЛА:
- Сверяйся с фактическим кодом в репозитории.
- Не предлагать установку внешних бинарных пакетов вроде TensorRT или ONNX Runtime, если они не требуются для CPU.
- Предоставляй только drop-in diff-u патчи.

## ФОРМАТ ОТЧЕТА:
- **CONFIRMED ML LEAKS & DEFECTS:** Подтвержденные баги датасета, утечки данных и расхождения формул.
- **MATHEMATICAL & LOSS FLAWS:** Анализ функции потерь и калибровки вероятностей.
- **DROP-IN UNIFIED DIFF:** Точные Python патчи в формате `diff -u`.
