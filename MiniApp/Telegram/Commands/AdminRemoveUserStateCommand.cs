using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class AdminRemoveUserStateCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return TelegramBotService.UserStates.TryGetValue(chatId, out var state) && state == TelegramBotService.UserState.AwaitingDeleteId;
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            if (!isAdmin) return;
            // RACE CONDITION FIX: Atomically claim the state
            bool claimed = TelegramBotService.UserStates.TryUpdate(chatId,
                newValue: TelegramBotService.UserState.None,
                comparisonValue: TelegramBotService.UserState.AwaitingDeleteId);
            if (!claimed) return;
            if (long.TryParse(cleanText.Trim(), out long targetChatId))
            {
                await UserRepository.RemoveAllowedUserAsync(targetChatId);

                await TelegramBotService.SendMessage(token, chatId, $"✅ <b>Доступ для пользователя <code>{targetChatId}</code> успешно удален из базы данных!</b>");
                try
                {
                    await TelegramBotService.SendMessage(token, targetChatId, "🔄 <b>Ваш доступ к боту был аннулирован администратором.</b>");
                }
                catch { /* ignore if blocked */ }
            }
            else
            {
                await TelegramBotService.SendMessage(token, chatId, "❌ <b>Неверный формат Chat ID. Действие отменено.</b>\n\nChat ID должен состоять только из цифр.");
            }
        }
    }
}
