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

                if (foundReg)
                {
                    if (hasDeposited)
                    {
                        await UserRepository.AddAllowedUserAsync(chatId);
                        await TelegramBotService.SendMessage(token, chatId, "✅ <b>Депозит подтвержден. Доступ открыт.</b>");
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
                                    new { text = "💵 Проверить депозит", callback_data = $"check_dep_{pocketId}" }
                                }
                            }
                        };
                        var payload = new 
                        { 
                            chat_id = chatId, 
                            text = "✅ <b>ID сохранен. Регистрация найдена. Теперь внесите депозит на аккаунт Pocket Option (от $10) и нажмите кнопку проверки.</b>", 
                            parse_mode = "HTML", 
                            reply_markup = depositKeyboard 
                        };
                        var json = JsonSerializer.Serialize(payload);
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        await TelegramBotService._httpClient.PostAsync(new Uri($"https://api.telegram.org/bot{token}/sendMessage"), content);
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
