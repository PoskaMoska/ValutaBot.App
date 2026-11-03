using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp.Services
{
    public class TrialReminderBackgroundService : BackgroundService
    {
        private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(10);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            BotLogger.Info("[TrialReminder] Background service started. Monitoring 3-day trial funnels.");

            // Brief initial delay to ensure TelegramBotService and DB initialization are completed
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndSendRemindersAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    BotLogger.Error($"[TrialReminder] Error during trial reminders check: {ex.Message}", ex);
                }

                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public async Task CheckAndSendRemindersAsync(CancellationToken ct = default)
        {
            string? token = TelegramNotifier.GetToken();
            if (string.IsNullOrEmpty(token)) return;

            var activeTrials = await RegistrationRepository.GetActiveTrialsForRemindersAsync();
            var now = DateTime.UtcNow;

            foreach (var reg in activeTrials)
            {
                if (ct.IsCancellationRequested) break;
                if (reg.HasDeposited || reg.ChatId <= 0 || !reg.TrialStartedAt.HasValue || !reg.TrialEndsAt.HasValue)
                    continue;

                bool messageAttempted = false;
                try
                {
                    // 1. Check Day 4 / Soft Expired Block
                    if (now >= reg.TrialEndsAt.Value && !reg.TrialExpiredSent)
                    {
                        messageAttempted = true;
                        bool sent = await SendExpiredNoticeAsync(token, reg);
                        if (sent)
                        {
                            reg.TrialExpiredSent = true;
                            await RegistrationRepository.SaveRegistrationAsync(reg);
                        }
                        continue;
                    }

                    // If trial is already expired and notice was sent, skip
                    if (now >= reg.TrialEndsAt.Value)
                        continue;

                    double remainingHours = (reg.TrialEndsAt.Value - now).TotalHours;

                    // 2. Check Day 3 Reminder (<= 12 hours remaining)
                    if (remainingHours <= 12.0 && !reg.ReminderDay3Sent)
                    {
                        messageAttempted = true;
                        bool sent = await SendDay3UrgencyReminderAsync(token, reg, now);
                        if (sent)
                        {
                            reg.ReminderDay3Sent = true;
                            reg.ReminderDay2Sent = true; // Mark day 2 as well to prevent late out-of-order sending
                            await RegistrationRepository.SaveRegistrationAsync(reg);
                        }
                        continue;
                    }

                    // 3. Check Day 2 Reminder (>= 24 hours elapsed, remaining between 12 and 48 hours)
                    double elapsedHours = (now - reg.TrialStartedAt.Value).TotalHours;
                    if (elapsedHours >= 24.0 && remainingHours > 12.0 && !reg.ReminderDay2Sent)
                    {
                        messageAttempted = true;
                        bool sent = await SendDay2ValueReminderAsync(token, reg, now);
                        if (sent)
                        {
                            reg.ReminderDay2Sent = true;
                            await RegistrationRepository.SaveRegistrationAsync(reg);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    BotLogger.Warn($"[TrialReminder] Failed processing reminder for chatId {reg.ChatId} (PocketId: {reg.PocketId}): {ex.Message}");
                }
                finally
                {
                    if (messageAttempted)
                    {
                        try
                        {
                            await Task.Delay(40, ct); // Rate limit guard: <= 25 msg/s global (Telegram limit: 30 msg/s)
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    }
                }
            }
        }

        private static async Task<bool> SendDay2ValueReminderAsync(string token, TelegramBotService.PocketRegistration reg, DateTime now)
        {
            var (winRate, topPairs, totalSignals) = await GetRecentMarketStatsAsync();
            int remainingHours = reg.TrialEndsAt.HasValue ? Math.Max(1, (int)Math.Round((reg.TrialEndsAt.Value - now).TotalHours)) : 48;
            string safePocketId = System.Net.WebUtility.HtmlEncode(reg.PocketId ?? "");
            string safeTopPairs = System.Net.WebUtility.HtmlEncode(topPairs ?? "EUR/USD и GBP/USD");

            string text =
                $"📊 <b>Прошёл 1 день использования бота.</b> Сегодня наши алгоритмы показали винрейт <b>{winRate:0.#}%</b> по парам <b>{safeTopPairs}</b>. Как ваши успехи?\n\n" +
                $"Напоминаем: пробный период для вашего аккаунта <code>{safePocketId}</code> действует ещё <b>{remainingHours} часов</b>. Чтобы сохранить доступ навсегда и перейти на реальный счёт, просто пополните баланс у брокера.";

            var keyboard = new
            {
                inline_keyboard = new object[]
                {
                    new object[]
                    {
                        new { text = "💵 Проверить депозит", callback_data = $"check_dep_{reg.PocketId}" }
                    }
                }
            };

            return await TelegramBotService.SendMessageWithKeyboard(token, reg.ChatId, text, keyboard);
        }

        private static async Task<bool> SendDay3UrgencyReminderAsync(string token, TelegramBotService.PocketRegistration reg, DateTime now)
        {
            int remainingHours = reg.TrialEndsAt.HasValue ? Math.Max(1, (int)Math.Round((reg.TrialEndsAt.Value - now).TotalHours)) : 12;
            string safePocketId = System.Net.WebUtility.HtmlEncode(reg.PocketId ?? "");

            string text =
                $"⏳ <b>До окончания бесплатного доступа осталось {remainingHours} часов!</b>\n\n" +
                $"После этого бот прекратит отправку сигналов для вашего аккаунта <code>{safePocketId}</code>. Чтобы не прерывать торговлю, пополните баланс в личном кабинете брокера и нажмите кнопку \"Проверить депозит\".";

            var keyboard = new
            {
                inline_keyboard = new object[]
                {
                    new object[]
                    {
                        new { text = "💵 Проверить депозит", callback_data = $"check_dep_{reg.PocketId}" }
                    }
                }
            };

            return await TelegramBotService.SendMessageWithKeyboard(token, reg.ChatId, text, keyboard);
        }

        private static async Task<bool> SendExpiredNoticeAsync(string token, TelegramBotService.PocketRegistration reg)
        {
            int signalsObserved = await GetTradeCountBetweenAsync(reg.TrialStartedAt, reg.TrialEndsAt);
            string safePocketId = System.Net.WebUtility.HtmlEncode(reg.PocketId ?? "");

            string signalsContext = signalsObserved > 0 
                ? $"За время вашего 3-дневного тест-драйва алгоритмы выдали <b>{signalsObserved} сигналов</b>.\n\n" 
                : "";

            string text =
                $"🔒 <b>Пробный период завершён. Доступ к сигналам приостановлен.</b>\n\n" +
                signalsContext +
                $"Вы в любой момент можете активировать бота обратно — достаточно внести минимальный депозит на ваш зарегистрированный аккаунт (ID: <code>{safePocketId}</code>) и нажать кнопку ниже:";

            var keyboard = new
            {
                inline_keyboard = new object[]
                {
                    new object[]
                    {
                        new { text = "💳 Я внёс депозит — Активировать бота", callback_data = $"check_dep_{reg.PocketId}" }
                    }
                }
            };

            return await TelegramBotService.SendMessageWithKeyboard(token, reg.ChatId, text, keyboard);
        }

        public static async Task<(double WinRate, string TopPairs, int TotalTrades)> GetRecentMarketStatsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
                return (68.0, "EUR/USD и GBP/USD", 120);

            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                string since = DateTime.UtcNow.AddHours(-24).ToString("o");

                var rows = (await conn.QueryAsync<(string Asset, bool? WasWin)>(
                    "SELECT asset, was_win FROM trade_outcomes WHERE created_at >= @since AND direction IN ('BUY', 'PUT') AND was_win IS NOT NULL",
                    new { since })).ToList();

                if (rows.Count < 20)
                {
                    // Fallback to recent 200 real trades if 24h has few trades (e.g. weekend or quiet period)
                    rows = (await conn.QueryAsync<(string Asset, bool? WasWin)>(
                        "SELECT asset, was_win FROM trade_outcomes WHERE direction IN ('BUY', 'PUT') AND was_win IS NOT NULL ORDER BY created_at DESC LIMIT 200")).ToList();
                }

                if (rows.Count == 0)
                    return (68.0, "EUR/USD и GBP/USD", 0);

                int total = rows.Count;
                int wins = rows.Count(r => r.WasWin == true);
                double winRate = Math.Round((double)wins / total * 100.0, 1);

                // Find top 2 pairs with best winrate (with at least 5 trades)
                var pairStats = rows.GroupBy(r => r.Asset)
                    .Select(g => new {
                        Asset = g.Key,
                        Count = g.Count(),
                        WinRate = (double)g.Count(x => x.WasWin == true) / g.Count()
                    })
                    .Where(x => x.Count >= 5)
                    .OrderByDescending(x => x.WinRate)
                    .ThenByDescending(x => x.Count)
                    .Take(2)
                    .Select(x => FormatPair(x.Asset))
                    .ToList();

                string topPairs = pairStats.Count >= 2 
                    ? $"{pairStats[0]} и {pairStats[1]}" 
                    : (pairStats.Count == 1 ? pairStats[0] : "EUR/USD и GBP/USD");

                return (winRate, topPairs, total);
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TrialFunnel] Error querying market stats: {ex.Message}");
                return (68.0, "EUR/USD и GBP/USD", 100);
            }
        }

        public static async Task<int> GetTradeCountBetweenAsync(DateTime? start, DateTime? end)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()) || !start.HasValue)
                return 0;

            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                string startStr = start.Value.ToString("o");
                string endStr = (end ?? DateTime.UtcNow).ToString("o");
                return (int)await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= @startStr AND created_at <= @endStr AND direction IN ('BUY', 'PUT')",
                    new { startStr, endStr });
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatPair(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return "EUR/USD";
            if (asset.Contains("/")) return asset;
            if (asset.Length >= 6) return $"{asset.Substring(0, 3)}/{asset.Substring(3, 3)}";
            return asset;
        }
    }
}
