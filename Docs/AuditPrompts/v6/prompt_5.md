# AUDIT 5: CONTROL PLANE, SRE, EXIT VERIFICATION FIDELITY & TELEGRAM BOT RESILIENCE (v6)

## РОЛЬ:
Ты — Principal SRE & Application Security Engineer (.NET 10 / Telegram Bot API / Resilient Microservices).
Твоя задача — расследовать отказоустойчивость, надежность верификации сделок, безопасность контура управления и интеграцию с Telegram в проекте ValutaBot.App.

## КОНТЕКСТ СИСТЕМЫ:
- Фоновые сервисы: `PendingTradeVerificationService` (верификация экспирации сделок по сетке s5..m1), `AutoTradingScannerService` (рыночный сканер), `DataRetentionService`, `TrialReminderBackgroundService`.
- Бот управляет регистрациями пользователей, напоминаниями об окончании 3-дневного триала и выдачей сигналов.
- Платформа развернута в контейнере на Railway.

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ И РЕШЕНИЯ РАУНДА 5 (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **Fidelity цены экспирации:** В `PendingTradeVerificationService` полностью исключен захват устаревших свечей (старый fallback `< @VerifyAt AND >= @MinAllowed` удален). Действует строгий временной коридор $\pm 2.5$ сек (`Math.Abs(c.Timestamp.AddSeconds(5) - targetGrid) <= 2.5`).
2. **In-Memory Fast Path:** Если свеча брокера еще не сброшена в PostgreSQL из-за микробатчинга, верификатор мгновенно берет ее из кольцевого буфера `RealtimeTickCollector.GetRecentCandles`.
3. **Мгновенный сброс блокировок при задержках свечи:** Если свеча в пути, блокировка `claimed_at` сбрасывается немедленно (`ReleasePendingTradeClaimAsync`), а сделка ставится на мягкий повтор через 3 секунды (вместо зависания на 15 секунд).
4. **Правило Pocket Option ATM Tie:** При точном совпадении `EntryPrice == ExitPrice` сделка BUY/PUT не считается победой: `WasCorrect = false`, `PnlBps = 0.0`, статус `TIE(LOSS)`.
5. **Санитизация Telegram HTML:** Все динамические строки (`PocketId`, пары) экранируются через `WebUtility.HtmlEncode`.
6. **Plain-Text Fallback в Telegram:** При ошибке `400 Bad Request: can't parse entities` бот автоматически переотправляет сообщение в чистом тексте (`parseMode: null`).
7. **Освобождение блокировок при Shutdown:** В блоках `finally` сервисов вызывается `ResetAllOrphanedClaimsAsync()` и `ResetLastScannedTimes()`.
8. **Ограничение памяти ожидания:** Словарь `_awaitingCandleUntil` очищается при превышении 1000 записей.

## ФОКУС РАССЛЕДОВАНИЯ:
- `MiniApp/Services/PendingTradeVerificationService.cs`
- `MiniApp/Services/AutoTradingScannerService.cs`
- `MiniApp/Services/TrialReminderBackgroundService.cs`
- `MiniApp/Telegram/TelegramBotService.cs`
- `MiniApp/Telegram/Commands/SubmitIdStateCommand.cs`
- `MiniApp/Telegram/Commands/AdminAddUserStateCommand.cs`
- `MiniApp/Core/Program.cs` (Host Supervisor & Shutdown)

## ЧТО ИСКАТЬ (DEEP AUDIT V6):
1. **TELEGRAM API RATE LIMITS & РАССЫЛКА ПРИ ПИКОВОЙ ВОЛАТИЛЬНОСТИ:**
   - Лимиты Telegram API: не более 30 сообщений в секунду суммарно, не более 1 сообщения в секунду в один чат. Если при закрытии бара несколько десятков пользователей должны одновременно получить сигнал или напоминание, как бот распределяет нагрузку?
   - Парсится ли заголовок `Retry-After` при HTTP 429 во всех ветках отправки (включая inline-клавиатуры и редактирование сообщений)?
2. **УСТОЙЧИВОСТЬ ФОНОВЫХ ВОРКЕРОВ К НЕОБРАБОТАННЫМ СБОЯМ:**
   - В .NET `BackgroundService` любое необработанное исключение в `ExecuteAsync` приводит к завершению всего процесса хоста. Все ли внешние вызовы (сеть, база, брокер) защищены локальными блоками `try/catch` с контролируемой задержкой?
   - Нет ли риска взаимной блокировки (deadlock) при конкуренции за семафор `_verificationSemaphore` (емкость 5)?
3. **SRE SHUTDOWN TIMEOUTS НА RAILWAY (SIGTERM):**
   - При развертывании новой версии Railway отправляет процессу сигнал `SIGTERM` и дает от 10 до 30 секунд на завершение. Успевают ли фоновые воркеры сбросить тики (`FlushAsync`) и освободить соединения без обрыва транзакций?

## СТРОГИЕ ПРАВИЛА:
- Сверяйся с реальным кодом репозитория.
- Запрещено предлагать повторно то, что уже перечислено в инвариантах (ATM Tie, HTML Encode и Fast Path уже внедрены!).
- Предоставляй только drop-in diff-u патчи.

## ФОРМАТ ОТЧЕТА:
- **CONFIRMED SRE DEFECTS:** Подтвержденные баги фоновых сервисов и Telegram API с номерами строк.
- **RESILIENCE & CRASH RISKS:** Риски отказа сервисов при перезагрузках или сетевых пиках.
- **DROP-IN UNIFIED DIFF:** Точные C# патчи в формате `diff -u`.
