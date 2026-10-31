using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Telegram.Commands;

namespace ValutaBot.MiniApp;

public partial class TelegramBotService
{
    private static async Task HandleMessage(string token, long chatId, string text, string username, string webAppUrl)
    {
        try
        {
            await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAllUserAsync(chatId);

            string cleanText = text.Trim();
            string command = cleanText.Split(' ')[0].Replace("@valutaPocket_bot", "").ToLower();

            bool isAdmin = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsAdminAsync(chatId);

            await CommandDispatcher.DispatchAsync(chatId, command, cleanText, isAdmin, token, webAppUrl);
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[TG Bot] Error handling message for chatId {chatId}", ex);
        }
    }

    internal static async Task SendGatedWelcome(string token, long chatId)
    {
        string text = "🤖 <b>TradeAI — AI анализ графиков</b>\n\n" +
                      "Для доступа к анализатору нужно:\n" +
                      "1. Зарегистрироваться на Pocket Option\n" +
                      "2. Нажать «Я зарегистрировался»\n\n" +
                      "🎁 <b>Вам будет начислен бесплатный тест-драйв на 3 дня</b> для проверки сигналов на демо-счёте без риска!\n\n" +
                      "Это занимает 1 минуту.";

        var inlineKeyboard = new
        {
            inline_keyboard = new object[]
            {
                new object[]
                {
                    new { text = "1️⃣ Зарегистрироваться на Pocket Option", url = $"https://po-ru4.click/cabinet/demo-quick-high-low?utm_campaign=852286&utm_source=affiliate&utm_medium=sr&a=Tlu0RchTyPcFYj&al=1775096&ac=smart-link&cid=963405&code=WELCOME50&subid={chatId}&subid1={chatId}&sub_id1={chatId}" }
                },
                new object[]
                {
                    new { text = "✅ Я зарегистрировался, открыть доступ", callback_data = "check_reg" }
                }
            }
        };

        var replyKeyboard = new
        {
            keyboard = new object[]
            {
                new object[] { new { text = "❓ Инструкция как пользоваться ботом" } }
            },
            resize_keyboard = true
        };

        await SendMessageWithKeyboard(token, chatId, text, inlineKeyboard);
    }

    private static string GetCustomWebAppUrl(string baseUrl, long chatId, string botToken)
    {
        long authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string dataToSign = $"{chatId}:{authDate}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(botToken));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        string sign = Convert.ToHexString(hash).ToLowerInvariant();
        string separator = baseUrl.Contains("?") ? "&" : "?";
        return $"{baseUrl}{separator}custom_user_id={chatId}&custom_user_sign={sign}&auth_date={authDate}";
    }

    internal static async Task SendUserWelcome(string token, long chatId, string webAppUrl)
    {
        _ = ResetChatMenuButton(token, chatId);
        string customUrl = GetCustomWebAppUrl(webAppUrl, chatId, token);

        string text = "✅ <b>Доступ открыт!</b>\n\nИспользуйте кнопку <b>📊 Открыть TradeAI</b> в меню внизу чата, чтобы запустить анализатор.";

        var keyboard = new
        {
            keyboard = new object[]
            {
                new object[]
                {
                    new { text = "📊 Открыть TradeAI", web_app = new { url = customUrl } }
                },
                new object[]
                {
                    new { text = "❓ Инструкция как пользоваться ботом" }
                }
            },
            resize_keyboard = true,
            is_persistent = true
        };

        await SendMessageWithKeyboard(token, chatId, text, keyboard);
    }

    internal static async Task SendAdminWelcome(string token, long chatId, string webAppUrl)
    {
        _ = ResetChatMenuButton(token, chatId);
        string customUrl = GetCustomWebAppUrl(webAppUrl, chatId, token);

        string text = "👑 <b>Панель администратора TradeAI</b>";

        var keyboard = new
        {
            keyboard = new object[]
            {
                new object[]
                {
                    new { text = "📊 Открыть TradeAI", web_app = new { url = customUrl } }
                },
                new object[]
                {
                    new { text = "👥 Всего юзеров" },
                    new { text = "👑 Добавить админа" },
                    new { text = "🚫 Удалить доступ" }
                },

            },
            resize_keyboard = true
        };

        await SendMessageWithKeyboard(token, chatId, text, keyboard);
    }

    public static async Task AutoApproveUser(long chatId)
    {
        string? token = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(token)) return;

        await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAllowedUserAsync(chatId);

        // Notify user
        await SendMessage(token, chatId, "🎉 <b>Поздравляем! Ваш аккаунт Pocket Option успешно подтвержден автоматически.</b>");
        await SendUserWelcome(token, chatId, _webAppUrl);

        // Notify admins
        List<long> adminsToNotify = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.GetAdminChatIdsAsync();
        
        foreach (long adminId in adminsToNotify)
        {
            await SendMessage(token, adminId, $"🔔 <b>Автоматическое открытие доступа</b>\n\nПользователь с Chat ID: <code>{chatId}</code> успешно прошел регистрацию и пополнил депозит!");
        }
    }
}
