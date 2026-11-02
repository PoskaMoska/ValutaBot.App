# AUDIT 1: C# .NET 10 SYSTEM CORE, CONCURRENCY, MATHEMATICS & TA INDICATORS CRITICAL PATH (v6)

## РОЛЬ:
Ты — Principal C# Systems Architect & High-Concurrency Systems Engineer (.NET 10).
Твоя задача — провести глубокий аудит горячего пути вычислений, математической устойчивости индикаторов и аллокаций памяти в ядре проекта ValutaBot.App (.NET 10 Web API).

## КОНТЕКСТ СИСТЕМЫ:
- Высокочастотный торговый робот для бинарных опционов Pocket Option OTC (экспирации s5, s10, s15, s30, m1).
- Модель: DualStream PatchTST Transformer (FastAPI ML микросервис на порту 8765) + эвристический ConfluenceMatrix (SMC, continuous velocity/acceleration dp/dt, MTF Alignment).
- Архитектурная парадигма: Pure Neural Brain (вердикт направления и вероятность на 100% определяются нейросетью; TA/SMC служат защитными фильтрами и телеметрией).

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ И РЕШЕНИЯ РАУНДА 5 (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **Валидация свечей:** Свечной контракт `OhlcCandle.IsValid` строго проверяет конечность (`double.IsFinite`), положительность `Open`, `Close`, `Low > 0`, соотношение `High >= Low` и неотрицательность объема `Volume >= 0`.
2. **Защита индикаторов от NaN/Infinity:** В `StatefulIndicators` и `TechnicalAnalysisEngine` внедрены жесткие гарды против деления на ноль и нулевого диапазона (`span <= 1e-12`) для RSI, ATR, SuperTrend, EMA, SMA и алгоритма Вельфорда (Welford) при абсолютном флэте.
3. **Контракт возврата TA:** Метод `AnalyzeMarketAsync` возвращает строгий DTO `TaScoringDetail` (запрещено предлагать замену на кортежи!). Поле `IndicatorSnapshot` содержит флаг `"CORRUPTED_DATA"` при невалидных котировках.
4. **Фильтрация нейтральных сигналов:** Флаг `isExplicitHoldSignal` в `MarketAnalysisOrchestrator` надежно изолирует нейтральные вердикты от генерации торговых ордеров.
5. **Сериализация HttpJsonOptions:** Глобально настроена через `AllowNamedFloatingPointLiterals = true`, `PropertyNameCaseInsensitive = true`, `PropertyNamingPolicy = CamelCase`.
6. **Параллелизм таймфреймов:** Расчет таймфреймов в `ConfluenceMatrixEngine` распараллелен через `Task.WhenAll(tfs.Select(ProcessTf))`.
7. **Очистка кэша индикаторов:** `IndicatorCache.PruneStates()` выполняется потокобезопасно через `Interlocked.CompareExchange` без блокировки горячего пути.

## ФОКУС РАССЛЕДОВАНИЯ:
- `MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs`
- `MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs`
- `MiniApp/Features/MarketAnalysis/Engines/ContinuousStateEngine.cs`
- `MiniApp/Engines/TechnicalAnalysisEngine.cs`
- `MiniApp/Engines/Indicators/StatefulIndicators.cs`
- `MiniApp/Engines/IndicatorCache.cs`
- `MiniApp/Core/PriceFeed.cs`

## ЧТО ИСКАТЬ (DEEP AUDIT V6):
1. **ZERO-ALLOCATION & GC PRESSURE ON HOT PATH:**
   - Создаются ли лишние аллокации замыканий (closures), `Task<T>` или LINQ-цепочки (`Select`, `Where`, `ToList`, `ToArray`) при каждом тике внутри `ConfluenceMatrixEngine` и `MarketAnalysisOrchestrator`?
   - Возможно ли применение `ReadOnlySpan<MiniAppController.OhlcCandle>` или `MemoryMarshal` для прохода по окну свечей без выделения промежуточных массивов?
2. **МАТЕМАТИЧЕСКАЯ УСТОЙЧИВОСТЬ ПРИ РЕЗКИХ РЫНОЧНЫХ АНОМАЛИЯХ:**
   - **Ценовые разрывы (гэпы):** Как реагируют ATR и SuperTrend на скачкообразные гэпы в 50–100 bps при открытии сессий OTC после пауз? Не происходит ли переполнения или искажения коэффициента волатильности?
   - **Длинные серии Doji:** Если 20 свечей подряд имеют `Close == Open`, сохраняется ли численная стабильность производных Савицкого-Голея и экспоненциальных скользящих?
3. **ПОТОКОБЕЗОПАСНОСТЬ И МНОГОПОТОЧНОЕ СОСТОЯНИЕ:**
   - Исключены ли состояния гонки в `IndicatorCache` при одновременном обращении 6 параллельных воркеров к индикаторам одного актива?
   - Гарантирована ли неизменяемость (immutability) возвращаемых срезов свечей?

## СТРОГИЕ ПРАВИЛА:
- Сверяйся с реальным кодом репозитория.
- Запрещено менять сигнатуры публичных контрактов, если это ломает смежные сервисы.
- Предоставляй только drop-in diff-u патчи.

## ФОРМАТ ОТЧЕТА:
- **CONFIRMED DEFECTS:** Подтвержденные баги со ссылками на файл и строку кода.
- **ALLOCATION & LATENCY BOTTLENECKS:** Анализ аллокаций памяти и задержек.
- **DROP-IN UNIFIED DIFF:** Точные C# патчи в формате `diff -u`.
