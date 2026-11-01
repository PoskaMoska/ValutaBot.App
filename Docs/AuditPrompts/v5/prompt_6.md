# AUDIT 6: TELEGRAM MINIAPP FRONTEND, UI STATE & API CLIENT (v5)

## РОЛЬ:
Ты — Senior Frontend Architect & Performance Engineer (Telegram MiniApps / Vanilla JS / Mobile WebViews).
Твоя задача — провести глубокий аудит клиентской части Telegram MiniApp бота ValutaBot.App (каталог MiniApp/wwwroot/) и мобильного взаимодействия с C# бэкендом.

## КОНТЕКСТ СИСТЕМЫ И ИНВАРИАНТЫ (НЕ СЧИТАТЬ ДЕФЕКТАМИ):
- Стек: Чистый Vanilla JS (ES Modules), HTML5 Canvas/SVG, CSS3 без тяжелых фреймворков.
- Поддержка iOS Safari BFCache и блокировки экрана:
  * В api.js реализован флаг _pendingReconnectOnWakeup и функция handleWakeupReconnect(), привязанная к pageshow и visibilitychange.
  * В ui.js обработчик видимости графика вынесен в _onChartVisibilityChange() и слушает как visibilitychange, так и pageshow (persisted: true).
- Счетчик reconnectAttempts в api.js не инкрементируется при штатном 2-секундном offline-polling.
- Подсветка цены сбрасывается функцией resetLivePriceFlash() при смене актива, предотвращая зависание предыдущего цвета.
- В hideAiChart() отменяется незавершенный кадр анимации cancelAnimationFrame(resizeRafId).
- Вспомогательная функция escapeHtml вынесена в область видимости модуля.
- Таймер startStatusBar() пропускает итерации при скрытой вкладке (document.hidden).
- Метатег viewport поддерживает viewport-fit=cover, а сетка карточек погоды рынка (#marketWeatherCard) перестраивается в 2x2 на экранах <= 390px (iPhone SE/mini).

## ФОКУСИРУЙСЯ НА СЛЕДУЮЩИХ ФАЙЛАХ:
- `MiniApp/wwwroot/js/api.js`
- `MiniApp/wwwroot/js/ui.js`
- `MiniApp/wwwroot/js/state.js`
- `MiniApp/wwwroot/js/main.js`
- `MiniApp/wwwroot/index.html`
- `MiniApp/wwwroot/css/style.css`

## ЧТО ИСКАТЬ:
1. CANVAS HIGH-DPI / RETINA SCALING:
   - Как Canvas в ui.js обрабатывает window.devicePixelRatio на современных смартфонах (Retina 2x/3x)? Не становится ли график размытым при изменении масштаба или ориентации экрана?
   - Корректно ли очищается контекст Canvas (ctx.clearRect) при ресайзе окна, чтобы не оставалось артефактов наложения?
2. RAPID USER SWITCHING & SOCKET RACE CONDITIONS:
   - Если пользователь быстро кликает по разным валютным парам в меню (EUR/USD -> GBP/USD -> USD/JPY за 1 секунду), гарантирует ли throttledWsSwitch в api.js, что предыдущий сокет действительно закрыт, а не продолжает слать тики старой пары в параллельный поток?
   - Не возникает ли утечки событий onmessage старых сокетов?
3. TELEGRAM WEBAPP VIEWPORT & KEYBOARD COLLISIONS:
   - Обрабатывает ли MiniApp события Telegram.WebApp.onEvent('viewportChanged') при открытии/закрытии виртуальной клавиатуры? Не перекрывают ли плавающие кнопки нижнюю панель навигации?
   - Корректно ли подхватывается системная тема Telegram (светлая/темная) при динамическом переключении темы Telegram клиентом?
4. TOUCH GESTURE COLLISIONS:
   - При скролле страницы на мобильном устройстве не перехватывает ли Canvas графика вертикальный свайп пользователя, вызывая дергание страницы?

## ПРАВИЛО:
Проверяй актуальный код в репозитории перед формированием замечания. Запрещено предлагать повторно то, что уже перечислено в инвариантах.

## ФОРМАТ ОТЧЕТА:
- CONFIRMED FRONTEND DEFECTS: Баги логики, памяти и отрисовки с указанием строк.
- MOBILE UX BOTTLENECKS: Проблемы на реальных смартфонах (iOS/Android).
- DROP-IN JAVASCRIPT/CSS FIXES: Готовый чистый код.
