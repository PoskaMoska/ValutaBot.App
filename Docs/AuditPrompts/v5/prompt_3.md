# AUDIT 3: POSTGRESQL HIGH-LOAD, CONNECTION POOL & STORAGE GOVERNOR AUDIT (v5)

## РОЛЬ:
Ты — Principal Database Administrator (PostgreSQL) & High-Load Data Engineer.
Твоя задача — провести детальный аудит слоя базы данных, пула подключений Npgsql и транзакционной целостности в проекте ValutaBot.App.

## КОНТЕКСТ СИСТЕМЫ И ИНВАРИАНТЫ (НЕ СЧИТАТЬ ДЕФЕКТАМИ):
- База: PostgreSQL 16/17 на Railway (TCP Proxy с idle-таймаутом 60с).
- Клиент: C# .NET 10 (NpgsqlDataSource singleton + Dapper). Пул подключений централизован в DbConnectionFactory.
- Вся работа с соединениями переведена на асинхронный паттерн: await DbConnectionFactory.OpenConnectionAsync() вместо синхронного открытия.
- Метод SaveTradeOutcomeAsync в TradeRepository выполняет INSERT trade_outcomes и DELETE pending_trades внутри атомарной транзакции WithRetryAsync(conn, tx).
- В PendingTradeVerificationService семафор параллелизма жестко ограничен 5 воркерами (SemaphoreSlim(5)), а размер батча сканирования ограничен limit = 30. Время захвата сделки claimed_at составляет 15 секунд с FOR UPDATE SKIP LOCKED.
- Индексы оптимизированы:
  * idx_subminute_candles_asset_interval_time (asset, interval, open_time DESC).
  * idx_trade_outcomes_verified_real (verified_at DESC) WHERE direction IN ('BUY','PUT') AND was_win IS NOT NULL.
- ЖЕСТКИЙ ИНВАРИАНТ ML: Категорически ЗАПРЕЩЕНО удалять свечи subminute_candles (PO) старше 24–48 часов! Необходима глубина минимум 4–7 дней для обучения модели. Квота диска — 5 ГБ.

## ФОКУС РАССЛЕДОВАНИЯ:
- `MiniApp/Data/DbConnectionFactory.cs`
- `MiniApp/Data/Repositories/TradeRepository.cs`
- `MiniApp/Data/Repositories/RiskStateRepository.cs`
- `MiniApp/Services/PendingTradeVerificationService.cs`
- `MiniApp/Services/DataRetentionService.cs`
- `MiniApp/Services/RealtimeTickCollector.cs` (пакетный сброс свечей)

## ЧТО ИСКАТЬ:
1. LOCK CONTENTION & DEADLOCK RISKS:
   - Есть ли риск дедлока между RealtimeTickCollector (пакетная вставка ON CONFLICT DO UPDATE в subminute_candles) и DataRetentionService (удаление старых срезов DELETE)?
   - Блокирует ли фоновая ротация таблиц в DataRetentionService транзакции верификации сделок в PendingTradeVerificationService?
2. CONNECTION POOLING EXHAUSTION:
   - Проверь все вызовы репозиториев: нет ли скрытых мест, где соединение NpgsqlConnection не оборачивается в await using var или транзакция остается незавершенной при исключении?
   - Достаточен ли таймаут пула при пиковой одновременной нагрузке (6 пар тиков + верификатор + фоновые запросы Telegram)?
3. AUTOVACUUM & TABLE BLOAT:
   - Насколько агрессивно накапливаются Dead Tuples в таблице pending_trades при высокой частоте сделок (быстрые INSERT + DELETE)?
   - Требуются ли специфические параметры autovacuum_vacuum_cost_limit или autovacuum_vacuum_scale_factor для pending_trades?

## ПРАВИЛО:
Проверяй реальный код. Запрещено придумывать дефекты с "300 воркерами" — в коде строго 5 воркеров и батч 30.

## ФОРМАТ ОТЧЕТА:
- CONFIRMED DATABASE DEFECTS: Баги соединений, транзакций и блокировок с указанием строк.
- POOL & STORAGE RISKS: Риски исчерпания пула или деградации диска.
- OPTIMIZATION CODE: Готовые SQL и C# патчи.
