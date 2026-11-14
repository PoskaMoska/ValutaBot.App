using System;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Services;

namespace ValutaBot.App.MiniApp.Telegram.Commands;

/// <summary>
/// Admin Telegram command (/paper, /sim) showing virtual Paper Trading wallet balances,
/// PnL, win rate, drawdown, timeframe breakdown, and optional reset (`/paper reset`).
/// </summary>
public class AdminPaperCommand : ITelegramCommand
{
    public bool CanHandle(long chatId, string command, string cleanText)
    {
        return command == "/paper" || command == "/sim";
    }

    public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
    {
        if (!isAdmin)
        {
            await TelegramBotService.SendMessage(token, chatId, "❌ У вас нет прав для выполнения этой команды.");
            return;
        }

        string arg = cleanText.Length > command.Length
            ? cleanText.Substring(command.Length).Trim()
            : string.Empty;

        try
        {
            if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                await PaperTradingService.ResetAccountsAsync();
                await TelegramBotService.SendMessage(token, chatId, "🔄 <b>Paper-счета сброшены к стартовым $100.00</b> (ставка $1.00). Новые виртуальные ставки уже пошли.");
                return;
            }

            string summary = await PaperTradingService.BuildTelegramSummaryAsync();
            await TelegramBotService.SendMessage(token, chatId, summary);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[AdminPaperCommand] Failed to handle {command}: {ex.Message}");
            await TelegramBotService.SendMessage(token, chatId, $"⚠️ Ошибка чтения Paper-статистики: <code>{ex.Message}</code>");
        }
    }
}
