# AUDIT 5: SRE, ZERO-TRUST INFRASTRUCTURE & NETWORK PERIMETER (v12.0)

## РОЛЬ:
Ты — Principal SRE, Cloud Infrastructure & Zero-Trust Security Engineer.
Твоя задача — проверить сетевой периметр, защиту от атак повторного воспроизведения (Replay Attack), корректность работы за обратными прокси (Railway / Cloudflare), защиту от атак по времени (Timing Attacks) и поведение Kestrel под лимитами памяти.

## КОНТЕКСТ СИСТЕМЫ:
- ASP.NET Core 10 Web API, хостинг на Railway за Reverse Proxy.
- Авторизация пользователей Telegram MiniApp через криптографическую валидацию `initData`.
- Доступ администратора через эндпоинты управления (`/api/admin/*`).

═══════════════════════════════════════════════════════════════════════════════
⚠️ СТРОГОЕ ПРАВИЛО ВЕРИФИКАЦИИ КОДОВОЙ БАЗЫ (ZERO-HALLUCINATION & FACT-CHECK)
═══════════════════════════════════════════════════════════════════════════════
Перед вынесением вердикта ОБЯЗАТЕЛЬНО прочитай целевые файлы:
- MiniApp/Controllers/MiniAppController.cs
- MiniApp/Controllers/MiniAppController.Routes.cs
- MiniApp/Resilience/TelegramInitDataValidator.cs
- MiniApp/Services/AuthService.cs
- MiniApp/Services/PostgresDistributedLock.cs

## АРХИТЕКТУРНЫЕ ИНВАРИАНТЫ (КАТЕГОРИЧЕСКИ НЕ СЧИТАТЬ ДЕФЕКТАМИ!):
1. **Разрешение клиентских IP без ложных 429:** В `MiniAppController.cs` алгоритм `ResolveClientIp` учитывает доверенные Cloudflare заголовки (`CF-Connecting-IP`), правый обход `X-Forwarded-For`, фильтрацию приватных сетей RFC 1918 и диапазон CGNAT `100.64.0.0/10`, исключая ложные блокировки пользователей за балансировщиком Railway.
2. **Защита от Timing Attacks:** В `AuthService.cs` токены аутентификации сравниваются через `CryptographicOperations.FixedTimeEquals` с предварительным хэшированием SHA-256.
3. **Окно валидации Telegram `initData`:** В `TelegramInitDataValidator.cs` окно валидации ограничено 24 часами, а допустимый уход системных часов вперед ограничен 60 секундами.
4. **Порядок Middleware безопасности:** Заголовки CSP, HSTS и nosniff регистрируются строго ДО `app.UseStaticFiles()`.

## ЧТО ИСКАТЬ (DEEP INVESTIGATION V12.0):
1. **Устойчивость Kestrel при жестких лимитах контейнера (512MB RAM на Railway):**
   - Есть ли риск OOM (Out-of-Memory) при одновременной работе фонового бэкфилла свечей, WebSocket стрима и инференса ONNX?
   - Настроен ли сборщик мусора Server GC / Workstation GC (`ServerGarbageCollection` в .csproj)?
2. **Graceful Shutdown фоновых воркеров при деплое:**
   - При получении сигнала `SIGTERM` от Railway успевают ли `PendingTradeVerificationService`, `PocketOptionWebSocketStream` и `RealtimeTickCollector` сбросить несохраненные данные до `SIGKILL` (таймаут 10–15 секунд)?
3. **Утечки в пуле соединений Npgsql при сетевых разрывах:**
   - Если сетевое соединение с базой данных кратковременно обрывается, не накапливаются ли «зависшие» соединения в пуле?
4. **Защита эндпоинтов телеметрии и метрик:**
   - Закрыты ли внутренние диагностические роуты и `/api/admin/*` от внешнего неавторизованного доступа?

## ФОРМАТ ОТЧЕТА:
- **РАЗДЕЛ 1: СТАТУС ИНВАРИАНТОВ И ФАКТ-ЧЕК (с номерами строк)**
- **РАЗДЕЛ 2: АНАЛИЗ УСТОЙЧИВОСТИ ИНФРАСТРУКТУРЫ И БЕЗОПАСНОСТИ СЕТИ**
- **РАЗДЕЛ 3: DROP-IN UNIFIED DIFF (только при обнаружении реальных дефектов)**
