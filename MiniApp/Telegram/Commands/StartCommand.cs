using ValutaBot.MiniApp;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class StartCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return command == "/start";
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            bool isAllowedUser = await TelegramBotService.IsUserAllowed(chatId);

            if (isAdmin)
            {
                await TelegramBotService.SendAdminWelcome(token, chatId, webAppUrl);
            }
            else if (isAllowedUser)
            {
                await TelegramBotService.SendUserWelcome(token, chatId, webAppUrl);
            }
            else
            {
                await TelegramBotService.SendGatedWelcome(token, chatId);
            }
        }
    }
}

