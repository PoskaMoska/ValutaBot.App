using ValutaBot.App.MiniApp.Data.Repositories;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public partial class TelegramBotService
{
    private static async Task HandleCallback(string token, string queryId, long chatId, string data, int messageId, string username, string webAppUrl)
    {

        if (data == "check_reg")
        {
            bool isAllowed = await IsUserAllowed(chatId);

            if (isAllowed)
            {
                await AnswerCallbackQuery(token, queryId, "Доступ уже открыт!");
                await SendUserWelcome(token, chatId, webAppUrl);
                return;
            }

            UserStates[chatId] = UserState.AwaitingId;
            await AnswerCallbackQuery(token, queryId, "Введите ваш ID");
            await SendMessage(token, chatId, "✍️ <b>Пожалуйста, введите ваш ID аккаунта Pocket Option.</b>\n\n" +
                                           "Вы можете найти его в личном кабинете Pocket Option (это число из 7-10 цифр в вашем профиле).");
        }
        else if (data.StartsWith("check_dep_"))
        {
            string pocketId = data.Replace("check_dep_", "");
            
            // Answer callback query quickly
            await AnswerCallbackQuery(token, queryId, "Проверка депозита...");
            
            bool hasDeposited = false;
            var reg = await ValutaBot.App.MiniApp.Data.Repositories.RegistrationRepository.GetPocketRegistrationAsync(pocketId);
            if (reg != null)
            {
                hasDeposited = reg.HasDeposited;
                reg.ChatId = chatId;
                await ValutaBot.App.MiniApp.Data.Repositories.RegistrationRepository.SaveRegistrationAsync(reg);
            }
            
            if (hasDeposited)
            {
                await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAllowedUserAsync(chatId);
                await SendMessage(token, chatId, "✅ <b>Депозит подтвержден. Доступ открыт.</b>");
                await SendUserWelcome(token, chatId, webAppUrl);
            }
            else
            {
                var retryKeyboard = new
                {
                    inline_keyboard = new object[]
                    {
                        new object[]
                        {
                            new { text = "💵 Проверить депозит снова", callback_data = $"check_dep_{pocketId}" }
                        }
                    }
                };
                var payload = new 
                { 
                    chat_id = chatId, 
                    text = "❌ <b>Депозит не обнаружен.</b>\n\nПожалуйста, убедитесь, что вы пополнили баланс на брокерском счете Pocket Option. Если вы уже внесли депозит, подождите 1-2 минуты и попробуйте снова.", 
                    parse_mode = "HTML", 
                    reply_markup = retryKeyboard 
                };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync(new Uri($"https://api.telegram.org/bot{token}/sendMessage"), content);
            }
        }
        else if (data.StartsWith("approve_"))
        {
            bool isSenderAdmin;
            isSenderAdmin = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsAdminAsync(chatId);

            if (!isSenderAdmin)
            {
                await AnswerCallbackQuery(token, queryId, "❌ У вас нет прав для одобрения заявок.");
                return;
            }

            if (!long.TryParse(data.Replace("approve_", ""), out long userChatId))
            {
                await AnswerCallbackQuery(token, queryId, "❌ Неверный формат ID.");
                return;
            }
            await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAllowedUserAsync(userChatId);

            UserSubmittedIds.TryRemove(userChatId, out var pocketId);
            await AnswerCallbackQuery(token, queryId, "Заявка одобрена!");

            // Notify user
            await SendMessage(token, userChatId, "🎉 <b>Поздравляем! Доступ к ИИ-анализатору открыт.</b>");
            await SendUserWelcome(token, userChatId, webAppUrl);

            // Edit admin message
            string userDisplay = string.IsNullOrEmpty(username) ? $"ID {userChatId}" : $"@{username}";
            string updatedText = $"✅ <b>Заявка ОДОБРЕНА</b>\n\n" +
                                 $"👤 Пользователь: {userDisplay} (Chat ID: <code>{userChatId}</code>)\n" +
                                 $"🆔 Pocket Option ID: <code>{pocketId ?? "Не указан"}</code>";
            
            await EditMessageText(token, chatId, messageId, updatedText);
        }
        else if (data.StartsWith("decline_"))
        {
            bool isSenderAdmin;
            isSenderAdmin = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsAdminAsync(chatId);

            if (!isSenderAdmin)
            {
                await AnswerCallbackQuery(token, queryId, "❌ У вас нет прав для отклонения заявок.");
                return;
            }

            if (!long.TryParse(data.Replace("decline_", ""), out long userChatId))
            {
                await AnswerCallbackQuery(token, queryId, "❌ Неверный формат ID.");
                return;
            }

            UserStates.TryRemove(userChatId, out _);
            UserSubmittedIds.TryRemove(userChatId, out var pocketId);
            await AnswerCallbackQuery(token, queryId, "Заявка отклонена!");

            // Notify user
            await SendMessage(token, userChatId, "❌ <b>Доступ отклонен.</b>\n\n" +
                                               "Регистрация с указанным ID не найдена под нашей реферальной ссылкой.\n\n" +
                                               "Пожалуйста, убедитесь, что вы зарегистрировали новый аккаунт строго по нашей ссылке и ввели корректный ID.");
            await SendGatedWelcome(token, userChatId);

            // Edit admin message
            string userDisplay = string.IsNullOrEmpty(username) ? $"ID {userChatId}" : $"@{username}";
            string updatedText = $"❌ <b>Заявка ОТКЛОНЕНА</b>\n\n" +
                                 $"👤 Пользователь: {userDisplay} (Chat ID: <code>{userChatId}</code>)\n" +
                                 $"🆔 Pocket Option ID: <code>{pocketId ?? "Не указан"}</code>";

            await EditMessageText(token, chatId, messageId, updatedText);
        }

    }
}
