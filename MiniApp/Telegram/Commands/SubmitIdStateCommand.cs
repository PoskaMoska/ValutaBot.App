using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class SubmitIdStateCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return TelegramBotService.UserStates.TryGetValue(chatId, out var state) && state == TelegramBotService.UserState.AwaitingId;
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            // RACE CONDITION FIX: Atomically claim the state. If two Tasks run concurrently
            // (e.g. user double-taps), only the first one succeeds. The second sees None and exits.
            bool claimed = TelegramBotService.UserStates.TryUpdate(chatId,
                newValue: TelegramBotService.UserState.None,
                comparisonValue: TelegramBotService.UserState.AwaitingId);
            if (!claimed) return;
            var match = Regex.Match(cleanText, @"\d{7,10}");
            if (match.Success)
            {
                string pocketId = match.Value;
                TelegramBotService.UserSubmittedIds[chatId] = pocketId;
                // State already reset atomically at entry — no need to reset again

                var reg = await RegistrationRepository.GetPocketRegistrationAsync(pocketId);
                bool foundReg = reg != null && reg.HasRegistered;
                bool hasDeposited = reg != null && reg.HasDeposited;

                if (reg != null)
                {
                    reg.ChatId = chatId;
                    await RegistrationRepository.SaveRegistrationAsync(reg);
                }

                if (foundReg && reg != null)
                {
                    if (hasDeposited)
                    {
                        await UserRepository.AddAllowedUserAsync(chatId);
                        await TelegramBotService.SendMessage(token, chatId, "🎉 <b>Поздравляем! Ваш депозит подтвержден, открыт бессрочный доступ к TradeAI!</b>");
                        await TelegramBotService.SendUserWelcome(token, chatId, webAppUrl);
                    }
                    else
                    {
                        // Check if 3-day trial was already started for this pocketId (anti-abuse)
                        if (reg.TrialStartedAt.HasValue)
                        {
                            if (reg.TrialEndsAt.HasValue && reg.TrialEndsAt.Value > DateTime.UtcNow)
                            {
                                int remainingHours = Math.Max(1, (int)Math.Ceiling((reg.TrialEndsAt.Value - DateTime.UtcNow).TotalHours));
                                await TelegramBotService.SendMessage(token, chatId, 
                                    $"ℹ️ <b>Для вашего аккаунта Pocket Option (ID: {pocketId}) уже активен пробный период.</b>\n\n" +
                                    $"Осталось времени: <b>{remainingHours} ч.</b>\n\n" +
                                    $"Используйте кнопку <b>📊 Открыть TradeAI</b> в меню внизу чата для запуска.");
                                await TelegramBotService.SendUserWelcome(token, chatId, webAppUrl);
                            }
                            else
                            {
                                var depositKeyboard = new
                                {
                                    inline_keyboard = new object[]
                                    {
                                        new object[]
                                        {
                                            new { text = "💳 Я внёс депозит — Активировать бота", callback_data = $"check_dep_{pocketId}" }
                                        }
                                    }
                                };
                                await TelegramBotService.SendMessageWithKeyboard(token, chatId,
                                    $"🔒 <b>Пробный 3-дневный период для данного аккаунта Pocket Option (ID: {pocketId}) уже завершён.</b>\n\n" +
                                    "Чтобы продолжить пользоваться ботом и закрепить бессрочный доступ, пожалуйста, внесите депозит на ваш брокерский счёт и нажмите кнопку проверки ниже.",
                                    depositKeyboard);
                            }
                        }
                        else
                        {
                            // First time activating trial!
                            reg.TrialStartedAt = DateTime.UtcNow;
                            reg.TrialEndsAt = DateTime.UtcNow.AddDays(3);
                            reg.ChatId = chatId;
                            await RegistrationRepository.SaveRegistrationAsync(reg);

                            string trialWelcomeText = 
                                $"🎉 <b>Добро пожаловать!</b> Вам открыт полный доступ к боту на 3 дня (ID: <code>{pocketId}</code>).\n\n" +
                                "Рекомендуем протестировать первые сигналы на демо-счёте брокера, чтобы освоиться с таймфреймами и стратегией Smart Money. Удачных сделок!";

                            await TelegramBotService.SendMessage(token, chatId, trialWelcomeText);
                            await TelegramBotService.SendUserWelcome(token, chatId, webAppUrl);
                        }
                    }
                }
                else
                {
                    await TelegramBotService.SendMessage(token, chatId, "❌ <b>Ваш ID не найден в автоматической базе регистраций.</b>\n\n" +
                                                   "Пожалуйста, убедитесь, что вы зарегистрировались по нашей ссылке.\n\n" +
                                                   "Если вы только что прошли регистрацию, брокеру может потребоваться 1-2 минуты для синхронизации данных. Пожалуйста, подождите немного и введите ваш ID еще раз.");
                }
            }
            else
            {
                await TelegramBotService.SendMessage(token, chatId, "❌ <b>Неверный формат ID.</b>\n\nПожалуйста, введите корректный ID аккаунта Pocket Option (это число из 7-10 цифр).");
            }
        }
    }
}
