using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public static class CommandDispatcher
    {
        private static readonly List<ITelegramCommand> _commands = new()
        {
            new StartCommand(),
            new HelpCommand(),
            new SettingsCommand(),
            new AdminStatsCommand(),
            new AdminAccessCommand(),
            new AdminAddUserCommand(),
            new AdminAddUserStateCommand(),
            new AdminRemoveUserCommand(),
            new AdminRemoveUserStateCommand(),
            new SubmitIdStateCommand()
        };

        public static async Task DispatchAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            foreach (var cmd in _commands)
            {
                if (cmd.CanHandle(chatId, command, cleanText))
                {
                    await cmd.ExecuteAsync(chatId, command, cleanText, isAdmin, token, webAppUrl);
                    return; // Stop after first successful handling
                }
            }

            // Fallback: AwaitingId state if no command was handled and no other state is active
            if (TelegramBotService.UserStates.TryGetValue(chatId, out var state) && state == TelegramBotService.UserState.AwaitingId)
            {
                // This is already handled by SubmitIdStateCommand, but if it wasn't, we'd handle it here.
            }
            else
            {
                bool isAllowedUser = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsUserAllowedAsync(chatId);

                // FIX: If the user typed an unknown slash command, just say so instead of resending the full welcome menu
                if (cleanText.StartsWith("/"))
                {
                    await TelegramBotService.SendMessage(token, chatId, "❓ <b>Неизвестная команда.</b>\nВоспользуйтесь кнопками меню или введите /start.");
                    return;
                }

                // For unrecognized plain text, we resend the appropriate menu
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
}
