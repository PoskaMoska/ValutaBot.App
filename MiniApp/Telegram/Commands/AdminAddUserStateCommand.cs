using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class AdminAddUserStateCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return TelegramBotService.UserStates.TryGetValue(chatId, out var state) && state == TelegramBotService.UserState.AwaitingAddAdminId;
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            // FSM Memory Safety: Атомарно забираем состояние на входе, исключая утечки даже при сбросе прав админа
            bool claimed = TelegramBotService.UserStates.TryRemove(chatId, out var state) && state == TelegramBotService.UserState.AwaitingAddAdminId;
            if (!claimed || !isAdmin) return;
            if (long.TryParse(cleanText.Trim(), out long targetChatId) && targetChatId > 0)
            {
                await UserRepository.AddAdminAsync(targetChatId);
                await UserRepository.AddAllowedUserAsync(targetChatId);

                await TelegramBotService.SendMessage(token, chatId, $"👑 <b>Пользователь <code>{targetChatId}</code> успешно назначен администратором!</b>");
                try
                {
                    await TelegramBotService.SendMessage(token, targetChatId, "👑 <b>Вам предоставили права администратора и полный доступ к боту!</b>");
                }
                catch { /* ignore if blocked */ }
            }
            else
            {
                string safeInput = System.Net.WebUtility.HtmlEncode(cleanText.Trim().Length > 50 ? cleanText.Trim()[..50] : cleanText.Trim());
                await TelegramBotService.SendMessage(token, chatId, $"❌ <b>Неверный формат Chat ID: <code>{safeInput}</code>. Действие отменено.</b>\n\nChat ID должен состоять только из цифр.");
            }
        }
    }
}
