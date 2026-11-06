# AUDIT 4: ML SERVICE, PYTORCH TRAINING PIPELINE & IN-PROCESS ONNX RUNTIME (v12.0)

## РОЛЬ:
Ты — Lead Machine Learning Engineer & PyTorch/ONNX Systems Architect.
Твоя задача — провести исчерпывающий аудит пайплайна обучения нейросети, архитектуры PatchTST, валидации графа ONNX и устойчивости процесса обучения к взрыву градиентов и дисбалансу классов.

## КОНТЕКСТ СИСТЕМЫ:
- Модель: DualStream PatchTST (Macro Stream M1: 30x8, Micro Stream S5: 30x8, Cross-Attention Bridge).
- Скрипты обучения: `ml_service/train_objective_dual_stream.py` и `ml_service/train_dual_stream_v5.py`.
- Рruntime инференса: In-process Microsoft.ML.OnnxRuntime в C# (.NET 10).
- Датасет: `dataset_objective_dual_stream.npz` (52 768 объективно размеченных сэмплов).

═══════════════════════════════════════════════════════════════════════════════
⚠️ СТРОГОЕ ПРАВИЛО ВЕРИФИКАЦИИ КОДОВОЙ БАЗЫ (ZERO-HALLUCINATION & FACT-CHECK)
═══════════════════════════════════════════════════════════════════════════════
Перед вынесением вердикта ОБЯЗАТЕЛЬНО прочитай целевые файлы:
- ml_service/train_objective_dual_stream.py
- ml_service/train_dual_stream_v5.py
- ml_service/models/patch_tst_brain.py
- ml_service/models/patch_tst_predictor.py
- ml_service/export_objective_dataset.py
- MiniApp/Features/MarketAnalysis/Engines/DualStreamOnnxService.cs

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **Имена тензоров графа ONNX:** В `train_dual_stream_v5.py` и `train_objective_dual_stream.py` строго соблюдаются имена входных тензоров `macro_x`, `micro_x`, `bridge_x` и выходного тензора `logit` для 100% совместимости с `DualStreamOnnxService.cs:144-147`.
2. **Opset 17 и численный клэмп:** Экспорт модели выполняется строго в `opset_version=17` с предварительной защитой `torch.nan_to_num(..., nan=0.0, posinf=50.0, neginf=-50.0)`.
3. **Point-in-Time отсечение:** В `MarketAnalysisOrchestrator.cs` макро-свечи отсекаются по времени `microClose.AddSeconds(-60)`, полностью исключая Lookahead Bias.
4. **Гистерезис детектора дрифта:** В `OnlineDriftDetector` внедрен триггер Шмитта с зоной нечувствительности 4% для устранения дребезга переключения.

## ЧТО ИСКАТЬ (DEEP INVESTIGATION V12.0):
1. **Баланс классов и функция потерь при обучении:**
   - Если в датасете соотношение Win / Loss / Draw смещено (например, ничьих Draw очень мало), защищен ли скрипт обучения от деградации модели в мажоритарный класс? Используются ли взвешенные веса классов (`pos_weight` в `BCEWithLogitsLoss` или `class_weights`)?
2. **Валидационный сплит без утечек (No Future Leakage in Train/Val):**
   - Используется ли строго хронологический `TimeSeriesSplit` (или отсечение по временной метке), либо датасет случайно перемешивается через `train_test_split(shuffle=True)`? В HFT случайное перемешивание недопустимо!
3. **Learning Rate Scheduler и Gradient Clipping:**
   - Настроен ли `torch.nn.utils.clip_grad_norm_` для предотвращения взрыва градиентов в механизмах Attention?
   - Используется ли Cosine Annealing или OneCycleLR для гарантированной сходимости?
4. **Валидация экспортированного `.onnx` через `onnx.checker`:**
   - Проверяется ли валидность графа ONNX сразу после экспорта скриптом `onnx.checker.check_model` и тестовым прогоном через `ort.InferenceSession`?

## ФОРМАТ ОТЧЕТА:
- **РАЗДЕЛ 1: СТАТУС ИНВАРИАНТОВ И ФАКТ-ЧЕК (с номерами строк)**
- **РАЗДЕЛ 2: АНАЛИЗ ГИПЕРПАРАМЕТРОВ ОБУЧЕНИЯ И СТАБИЛЬНОСТИ СХОДИМОСТИ**
- **РАЗДЕЛ 3: DROP-IN UNIFIED DIFF (только при обнаружении реальных дефектов)**
