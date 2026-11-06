# AUDIT 2: HIGH-THROUGHPUT BROKER WEBSOCKETS, ZERO-GC & TICK CONCURRENCY (v12.0)

## РОЛЬ:
Ты — Principal High-Concurrency & Low-Latency Engineer (.NET 10 / HFT Systems).
Твоя задача — провести аудит сокетного конвейера брокера, потокобезопасности котировок, устойчивости пулов памяти `ArrayPool` и zero-allocation горячего пути.

## КОНТЕКСТ СИСТЕМЫ:
- Поток тиков Pocket Option WebSocket (до 200 тиков в секунду в часы пик).
- Горячий путь: парсинг WebSocket фреймов, извлечение цены/времени, обновление Seqlock котировок, проброс в `RealtimeTickCollector` и `CandleRingBuffer`.
- Недопустимы: блокировки потоков, Torn Reads, утечки памяти в `ArrayPool<byte>`, аллокации строк на каждый тик.

═══════════════════════════════════════════════════════════════════════════════
⚠️ СТРОГОЕ ПРАВИЛО ВЕРИФИКАЦИИ КОДОВОЙ БАЗЫ (ZERO-HALLUCINATION & FACT-CHECK)
═══════════════════════════════════════════════════════════════════════════════
Перед вынесением вердикта ОБЯЗАТЕЛЬНО прочитай целевые файлы:
- MiniApp/Services/PocketOptionWebSocketStream.cs
- MiniApp/Services/RealtimeTickCollector.cs
- MiniApp/Engines/CandleRingBuffer.cs
- MiniApp/Core/PriceFeed.cs
- MiniApp/Features/MarketAnalysis/Engines/MacroContextEngine.cs

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **Seqlock `LivePriceSlot`:** В `PocketOptionWebSocketStream.cs` чтение и запись 24-байтовой котировки (`price`, `srvTs`, `localTicks`) защищены lock-free паттерном Seqlock с барьерами `Volatile.Read/Write` и проверкой четности версий (Torn Reads ликвидированы).
2. **Защита от Double-Free в `ArrayPool`:** Освобождение арендованного буфера в `ReceivedFrame` защищено атомарным `Interlocked.Exchange(ref _rented, null)`.
3. **Zero-Allocation на горячем пути:** В `MacroContextEngine.cs` используется быстрый lookup без аллокаций строк через `PriceFeed.BaseAsset(asset)`.
4. **Отказ от хостового фильтра 60с:** В `PriceFeed.IsValidTick` ЗАПРЕЩЕНО добавлять проверку `hostUtc - tickUtc > 60s`, так как это ломает исторический бэкфилл и воспроизведение тиков в тестах. Упорядочивание обеспечивается монотонными ватермарками.

## ЧТО ИСКАТЬ (DEEP INVESTIGATION V12.0):
1. **Стресс Seqlock при многопоточном шторме котировок:**
   - Может ли возникнуть livelock читающего потока в `ReadLatestPrice()` при непрерывной записи со скоростью 500+ тиков/сек?
   - Корректно ли выставлен `spin.SpinOnce()`?
2. **Фрагментация TCP фреймов и Socket.IO framing:**
   - Если сервер брокера разбивает один payload тиков на несколько WebSocket фреймов (chunked), гарантирует ли `PocketOptionWebSocketStream` корректную склейку буферов без порчи JSON?
3. **Реконнект-шторм и утечки обработчиков:**
   - При внезапном обрыве связи с Pocket Option очищаются ли фоновые таски отслеживания пинга/понга? Нет ли утечки CancellationTokenSource?
4. **Инвариант монотонности аккумулятора свечей:**
   - Гарантирует ли `RealtimeTickCollector`, что тики с нарушенным порядком прибытия (out-of-order jitter) никогда не перепишут уже сформированную и закрытую 5-секундную свечу?

## ФОРМАТ ОТЧЕТА:
- **РАЗДЕЛ 1: СТАТУС ИНВАРИАНТОВ И ФАКТ-ЧЕК (с номерами строк)**
- **РАЗДЕЛ 2: АНАЛИЗ МНОГОПОТОЧНОСТИ И СТРЕСС-ТЕСТИРОВАНИЕ СОКЕТОВ**
- **РАЗДЕЛ 3: DROP-IN UNIFIED DIFF (только при обнаружении реальных дефектов)**
