using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class AdminStatsCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return command == "/stats" || command == "/regs" || cleanText == "👥 Всего юзеров" || command == "/db" || command == "/getdb" || command == "/downloaddb";
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            if (!isAdmin)
            {
                await TelegramBotService.SendMessage(token, chatId, "❌ У вас нет прав для выполнения этой команды.");
                return;
            }

            if (command == "/db" || command == "/getdb" || command == "/downloaddb")
            {
                await TelegramBotService.SendMessage(token, chatId, "⏳ База данных теперь в PostgreSQL. Для выгрузки используйте SQL-дампы.");
                return;
            }

            int totalUsers = await UserRepository.GetTotalUsersCountAsync();
            int allowedUsersCount = await UserRepository.GetAllowedUsersCountAsync();
            long regsCount = await RegistrationRepository.GetRegistrationsCountAsync();
            var latestRegs = (await RegistrationRepository.GetLatestRegistrationsAsync(15)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("📊 <b>Статистика бота:</b>");
            sb.AppendLine($"• Всего пользователей в боте: <b>{totalUsers}</b>");
            sb.AppendLine($"• Пользователей с доступом: <b>{allowedUsersCount}</b>");
            sb.AppendLine($"• Регистраций в базе: <b>{regsCount}</b>\n");
            
            sb.AppendLine("📝 <b>Последние 15 записей в базе:</b>");
            if (latestRegs.Count == 0)
            {
                sb.AppendLine("<i>(База регистраций пуста)</i>");
            }
            else
            {
                foreach (var r in latestRegs)
                {
                    string regIcon = r.HasRegistered ? "✅" : "❌";
                    string depIcon = r.HasDeposited ? "💰" : "❌";
                    sb.AppendLine($"• Pocket ID: <code>{r.PocketId}</code> | TG Chat: <code>{r.ChatId}</code> | Рег: {regIcon} | Деп: {depIcon}");
                }
            }

            await TelegramBotService.SendMessage(token, chatId, sb.ToString());
        }
    }
}
