# AUDIT 1: C# .NET 10 SYSTEM CORE, CONCURRENCY & TRADING CRITICAL PATH (v5)

## РОЛЬ:
Ты — Principal C# Systems Architect & High-Concurrency Systems Engineer.
Твоя задача — провести глубокое расследование исходного кода C# ядра проекта ValutaBot.App (.NET 10 Web API).

## КОНТЕКСТ СИСТЕМЫ:
- Высокочастотный торговый робот для бинарных опционов Pocket Option OTC (экспирации s5, s10, s15, s30, m1).
- Модель: DualStream PatchTST Transformer (FastAPI ML микросервис на порту 8765) + эвристический ConfluenceMatrix (SMC, continuous velocity/acceleration dp/dt, MTF Alignment).
- Архитектурная парадигма: Pure Neural Brain (вердикт направления и вероятность на 100% определяются нейросетью; TA/SMC служат фильтрами и телеметрией).

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ И НЕДАВНИЕ РЕШЕНИЯ (НЕ СЧИТАТЬ ДЕФЕКТАМИ):
- Порог вероятности для исполнения ордера: >= 58%. На s5 микро-таймфрейме вероятность аппаратно ограничена калибровкой до 59%.
- Защита от деления на ноль и выбросов логарифма при нулевой цене в TechnicalAnalysisEngine уже реализована через нулевой возврат (0.0).
- Дисперсия и стандартные отклонения в ContinuousStateEngine переведены на алгоритм Вельфорда (Welford's algorithm) для численной устойчивости на плоском рынке.
- Очистка IndicatorCache.PruneStates() выполняется потокобезопасно с Interlocked.CompareExchange и 30-минутным таймаутом без блокировки горячего пути.
- Метод DominantDirection в ConfluenceMatrixEngine требует строгого консенсуса (maxAgree >= 2 && buyCount != putCount).
- Расчет таймфреймов в ConfluenceMatrixEngine распараллелен через Task.WhenAll(tfs.Select(ProcessTf)).

## ФОКУС РАССЛЕДОВАНИЯ (CRITICAL PATH):
- `MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs`
- `MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs`
- `MiniApp/Features/MarketAnalysis/Engines/ContinuousStateEngine.cs`
- `MiniApp/Engines/TechnicalAnalysisEngine.cs`
- `MiniApp/Engines/IndicatorCache.cs`
- `MiniApp/Services/CircuitBreakerService.cs`

## ЧТО ИСКАТЬ:
1. CONCURRENCY & MEMORY ALLOCATION ON HOT PATH:
   - Создаются ли лишние замыкания, Task/ValueTask аллокации или LINQ-выражения при вызове ProcessTf и ConfluenceMatrix на каждом тике?
   - Нет ли риска дедлока при одновременном обращении к IndicatorCache из нескольких потоков?
2. NUMERICAL STABILITY IN EXTREME MARKET CONDITIONS:
   - Проверь расчет ADX, RSI и SuperTrend в TechnicalAnalysisEngine: что происходит, если все свечи в окне имеют High == Low == Close (абсолютный флэт или зависший фид)? Нет ли скрытого деления на ноль в сглаживании Уайлдера (Wilder smoothing)?
   - Проверь расчет производных Savitzky-Golay при длине окна меньше минимальной — есть ли гарантированный безопасный fallback?
3. COLD START & BUFFER BOUNDARIES:
   - Как ведет себя MarketAnalysisOrchestrator при старте системы, когда в кольцевом буфере меньше необходимых 160 свечей?
   - Исключены ли NullReferenceException при пустых или частично заполненных DTO признаков?

## ПРАВИЛО:
Сверяйся с фактическим кодом. Запрещено придумывать дефекты там, где уже стоит проверка.

## ФОРМАТ ОТЧЕТА:
- CONFIRMED DEFECTS: Подтвержденные баги со ссылками на файл и строку кода.
- ARCHITECTURAL RISKS: Риски деградации под нагрузкой.
- CODE FIXES: Точные C# патчи.
