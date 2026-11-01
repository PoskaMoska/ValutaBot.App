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
            // Вместо TryUpdate в UserState.None полностью вычищаем ключ из ConcurrentDictionary
            bool claimed = TelegramBotService.UserStates.TryRemove(chatId, out var state) && state == TelegramBotService.UserState.AwaitingDeleteId;
            if (!claimed) return;
            if (long.TryParse(cleanText.Trim(), out long targetChatId))
            {
                // 1. Remove from allowed_users
                await UserRepository.RemoveAllowedUserAsync(targetChatId);

                // 2. Revoke deposit/trial flags in registrations so IsUserAllowed cannot auto-resurrect access
                // Note: SaveRegistrationAsync uses COALESCE(EXCLUDED.trial_ends_at, registrations.trial_ends_at),
                // so set TrialEndsAt to a past timestamp rather than null when expiring an active trial.
                var reg = await RegistrationRepository.GetRegistrationByChatIdAsync(targetChatId);
                if (reg != null)
                {
                    reg.HasDeposited = false;
                    if (reg.TrialEndsAt.HasValue && reg.TrialEndsAt.Value > System.DateTime.UtcNow)
                    {
                        reg.TrialEndsAt = System.DateTime.UtcNow.AddSeconds(-1);
                    }
                    await RegistrationRepository.SaveRegistrationAsync(reg);
                }

                // 3. Immediately invalidate in-memory 60s access cache
                TelegramBotService.InvalidateUserCache(targetChatId);

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
