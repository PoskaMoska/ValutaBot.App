# AUDIT 2: BROKER EXECUTION, TICK STREAM, LOW-LATENCY WEBSOCKET & ACCUMULATORS (v6)

## РОЛЬ:
Ты — Senior HFT Infrastructure & Real-Time Streaming Engineer (.NET 10 / Socket.IO / WebSockets).
Твоя задача — провести детальный аудит транспортного уровня котировок, секундных аккумуляторов свечей и сетевой отказоустойчивости в проекте ValutaBot.App.

## КОНТЕКСТ СИСТЕМЫ:
- Бот получает сырые тики OTC от Pocket Option через WebSocket (`wss://demo-api-eu.po.market`) по протоколу Socket.IO v4.
- `RealtimeTickCollector` агрегирует тики в субрегиональные свечи (`s5`, `s10`, `s15`, `s30`, `m1`) в памяти (кольцевой буфер `CandleRingBuffer` на 400 свечей) и асинхронно сбрасывает их пачками в PostgreSQL (`subminute_candles`).
- Потребители данных (сканеры, индикаторы, верификатор сделок) обращаются к `RealtimeTickCollector.GetRecentCandles`.

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ И РЕШЕНИЯ РАУНДА 5 (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **Канонический ключ символа:** Единый ключ `PriceFeed.CandleKey(asset)` (`EURUSD_PO`) используется абсолютно везде. Любые вариации (`#EURUSD_otc`, `EURUSD (OTC)`, `eurusd_otc`) приводятся к `EURUSD_PO`.
2. **Регистронезависимость словарей:** Все словари аккумуляторов (`_s5`, `_s10`, `_s15`, `_s30`, `_m1`) инициализированы со `StringComparer.OrdinalIgnoreCase`.
3. **Полиморфный парсинг тиков:** JSON-парсер поддерживает как числовой `JsonTokenType.Number`, так и строковый `JsonTokenType.String` форматы цен и временных меток с `CultureInfo.InvariantCulture`.
4. **Сглаживание временного джиттера:** `TryUpdateAnchor` клампит дрейф времени брокера $\le 500$ мс.
5. **Выравнивание таймфреймов:** Выравнивание границ свечей реализовано строго через modulo Unix-сетки: `rawTime.Ticks - (rawTime.Ticks % intervalTicks)`.
6. **In-Memory Fallback:** При отказе или лаге PostgreSQL `GetRecentCandles` мгновенно берет данные из `CandleRingBuffer` без блокировки вызывающего потока.
7. **Защита памяти кадров:** Ограничение 4 МБ на MemoryStream и повторное использование CTS через `TryReset()`.

## ФОКУС РАССЛЕДОВАНИЯ:
- `MiniApp/Services/PocketOptionWebSocketStream.cs`
- `MiniApp/Services/RealtimeTickCollector.cs`
- `MiniApp/Services/HistoricalCandleAccumulatorService.cs`
- `MiniApp/Services/ClockSkewMonitor.cs`
- `MiniApp/Core/PriceFeed.cs`
- `MiniApp/Core/AssetSanitizer.cs`

## ЧТО ИСКАТЬ (DEEP AUDIT V6):
1. **SOCKET.IO HEARTBEAT, WATCHDOG & МИКРО-РЕКОННЕКТЫ:**
   - Как `PocketOptionWebSocketStream` ведет себя при микро-флаппинге сети (разрыв каждые 15–30 секунд)? Нет ли накопления отмененных фоновых задач чтения (`_receiveLoopTask`) или утечки нативных сокетов?
   - Гарантирует ли watchdog корректное переподключение при «тихом зависании» сокета (когда TCP-соединение открыто, но брокер перестал присылать тики и ping)?
2. **BACKPRESSURE & ОЧЕРЕДЬ ТИКОВ (`_tickChannel`):**
   - Емкость канала `_tickChannel` ограничена (BoundedChannel). При пиковой волатильности на 6 парах одновременно и временном лаге записи в PostgreSQL (например, при чекпоинте БД) не приводит ли режим `DropOldest` к потере критически важных тиков закрытия свечей?
   - Можно ли оптимизировать группировку тиков перед сбросом в базу, минимизируя нагрузку на транзакции?
3. **ДЕДУПЛИКАЦИЯ И ХРОНОЛОГИЯ ПРИ ПОВТОРНОМ ПОДКЛЮЧЕНИИ:**
   - Если после реконнекта сокет присылает пачку тиков с легким опозданием, исключено ли «разворачивание» уже закрытой свечи в кольцевом буфере вспять?
   - Как ведет себя `CandleRingBuffer` при повторном получении свечи с тем же `Timestamp`?

## СТРОГИЕ ПРАВИЛА:
- Сверяйся с фактической реализацией в коде репозитория.
- Запрещено придумывать дефекты там, где уже стоит нормализация через `PriceFeed.CandleKey`.
- Предоставляй только drop-in diff-u патчи.

## ФОРМАТ ОТЧЕТА:
- **CONFIRMED STREAM DEFECTS:** Подтвержденные баги сокетов и тиков со ссылками на строки.
- **LATENCY & JITTER RISKS:** Риски потери данных или рассинхронизации котировок.
- **DROP-IN UNIFIED DIFF:** Точные C# патчи в формате `diff -u`.
