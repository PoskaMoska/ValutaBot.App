# AUDIT 2: BROKER EXECUTION, TICK STREAM & LOW-LATENCY WEBSOCKET (v5)

## РОЛЬ:
Ты — Senior HFT & Execution Infrastructure Engineer.
Твоя задача — провести узконаправленный аудит сетевого транспорта котировок, синхронизации времени и буферизации свечей в ValutaBot.App (.NET 10 + Pocket Option Socket.IO Stream).

## КОНТЕКСТ СИСТЕМЫ И ИНВАРИАНТЫ (НЕ СЧИТАТЬ ДЕФЕКТАМИ):
- Бот работает полностью автономно в C# через PocketOptionWebSocketStream (wss://demo-api-eu.po.market).
- В PocketOptionWebSocketStream на каждый кадр не создается новый CancellationTokenSource — используется CTS с TryReset() (.NET 6+).
- Прием фрагментированных кадров защищен жестким лимитом 4 МБ на MemoryStream с предвыделенной начальной емкостью.
- В RealtimeTickCollector блокировка записи _dbWriteLock реализована через асинхронный SemaphoreSlim (_dbWriteLock.Wait(5s)), а не lock(object).
- В CandleRingBuffer.AddRange сортировка OrderBy вынесена за пределы lock-секции.
- При сбросе DST и реконнекте кэш _recentCandlesCache очищается.
- Удаление старых свечей в HistoricalCandleAccumulatorService выполняется одним атомарным запросом через OFFSET @MaxCandles (без N+1 COUNT/DELETE).

## ФОКУСИРУЙСЯ НА СЛЕДУЮЩИХ ФАЙЛАХ:
- `MiniApp/Services/PocketOptionWebSocketStream.cs`
- `MiniApp/Services/RealtimeTickCollector.cs`
- `MiniApp/Services/HistoricalCandleAccumulatorService.cs`
- `MiniApp/Services/ClockSkewMonitor.cs`
- `MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs` (Execution Guards)
- `MiniApp/Services/AutoTradingScannerService.cs`

## ЧТО ИСКАТЬ:
1. WEBSOCKET FRAME INTEGRITY & SOCKET.IO PROTOCOL:
   - Как PocketOptionWebSocketStream обрабатывает Socket.IO heartbeat (ping/pong пакетов)? Если брокер задерживает ответ на pong, сработает ли watchdog до зависания?
   - Что происходит при получении поврежденного или обрезанного UTF-8 фрейма с тиками? Гарантирован ли возврат буферов в ArrayPool?
2. RING BUFFER DATA INTEGRITY:
   - Защищен ли CandleRingBuffer от рассинхронизации временной сетки, если тики пришли с задержкой или в обратном порядке по времени из-за сетевого джиттера?
   - Не возникает ли разрыва между историческими свечами из БД и свежими тиками из WebSocket при холодном старте?
3. RACE CONDITIONS ПРИ РЕКОННЕКТАХ:
   - Если сокет брокера рвется и перезапускается каждые 30 секунд (например, при сетевом шторме), защищен ли RealtimeTickCollector от накопления зомби-потоков или дублирования свечей?
   - Корректно ли сбрасывается состояние Dead Man's Switch (IsSymbolFeedAlive)?

## ПРАВИЛО:
Проверяй текущую реализацию в файлах перед формированием замечания.

## ФОРМАТ ОТЧЕТА:
- CONFIRMED STREAM DEFECTS: Баги потоковой обработки и сокетов с номерами строк.
- RESILIENCE RISKS: Риски зависания соединения или пропуска свечей.
- RECOMMENDED FIXES: Точные C# патчи.
