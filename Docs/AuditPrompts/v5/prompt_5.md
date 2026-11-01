# AUDIT 5: CONTROL PLANE, TELEGRAM BOT, CRASH RECOVERY & SRE (v5)

## РОЛЬ:
Ты — Senior SRE & Application Security Engineer (.NET 10 / Telegram Bot API).
Твоя задача — расследовать отказоустойчивость, восстановление после сбоев и безопасность контура управления ValutaBot.App.

## КОНТЕКСТ СИСТЕМЫ И ИНВАРИАНТЫ (НЕ СЧИТАТЬ ДЕФЕКТАМИ):
- Поток тиков работает автономно в C# через WebSocket (без внешних браузерных процессов).
- Супервизор хоста в Program.cs перехватывает фатальные сбои и выполняет безопасный аварийный сброс буфера тиков в PostgreSQL (RealtimeTickCollector.FlushAsync).
- Уязвимость угона Pocket ID в Telegram callback check_dep_ устранена (проверяется reg.ChatId > 0 && reg.ChatId != chatId).
- Легаси-токены без auth_date полностью запрещены в AuthService (строго требуется валидный auth_date с TTL 24 часа).
- Деструктивные эндпоинты (/api/chaos/run, /api/admin/db-maintenance?full=true) требуют системного уровня доступа (requireSystemLevel: true), доступ для обычных Telegram-администраторов без секретного ключа заблокирован.

## ФОКУСИРУЙСЯ НА СЛЕДУЮЩИХ ФАЙЛАХ:
- `MiniApp/Core/Program.cs` (Host Supervisor, UnhandledException, Shutdown)
- `MiniApp/Telegram/TelegramBotService.cs`
- `MiniApp/Telegram/TelegramBotService.Callbacks.cs`
- `MiniApp/Controllers/MiniAppController.cs`
- `MiniApp/Controllers/MiniAppController.Routes.cs`
- `MiniApp/Services/AuthService.cs`
- `MiniApp/Resilience/TelegramInitDataValidator.cs`
- `MiniApp/Services/AutoTradingScannerService.cs`
- `MiniApp/Services/PendingTradeVerificationService.cs`

## ЧТО ИСКАТЬ:
1. BACKGROUND HOSTED SERVICES FAULT TOLERANCE:
   - Если в AutoTradingScannerService, RealtimeTickCollector или PendingTradeVerificationService возникает необработанное исключение (например, SocketException или NpgsqlException), не роняет ли оно весь веб-сервер Kestrel (.NET BackgroundService crash behavior)? Обернут ли цикл выполнения в надежный try/catch с задержкой?
2. TELEGRAM API RATE LIMITS & WEBHOOK RESILIENCE:
   - Как бот реагирует на HTTP 429 от серверов Telegram при массовой рассылке сигналов? Парсится ли параметр retry_after?
   - Нет ли утечки памяти в словарях сессий пользователей, контекстов меню или трекерах анти-спама при непрерывной работе в течение месяцев?
3. SRE SHUTDOWN & CLEANUP:
   - При получении сигнала SIGTERM (перезагрузка контейнера на Railway) успевают ли все 5 воркеров PendingTradeVerificationService корректно отпустить блокировки claimed_at в БД?
   - Не остаются ли висячие транзакции при аварийном завершении?

## ПРАВИЛО:
Проверяй актуальный код в репозитории перед формированием замечания.

## ФОРМАТ ОТЧЕТА:
- CONFIRMED SRE DEFECTS: Баги устойчивости, гонок и необработанных сбоев.
- RESILIENCE RISKS: Риски отказа сервисов при перезагрузках или сетевых пиках.
- RECOMMENDED C# FIXES: Минимальный готовый код для устранения проблем.
