using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class SettingsCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return command == "/settings" || cleanText.Contains("Настройки");
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            if (!isAdmin)
            {
                // Settings button only available for admins.
                return;
            }

            var settings = await UserRepository.GetSettingsAsync(chatId);
            
            var inlineKeyboard = new
            {
                inline_keyboard = new object[]
                {
                    // Toggles removed as per request to prevent disabling core features.
                }
            };

            string text = "⚙️ <b>Настройки</b>\n\nВсе аналитические модули (ИИ, SMC, Order Flow) работают в принудительном режиме и не могут быть отключены.";

            await ValutaBot.MiniApp.TelegramBotService.SendMessageWithKeyboard(token, chatId, text, inlineKeyboard);
        }
    }
}
