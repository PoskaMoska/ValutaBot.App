using System;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Services;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Telegram.Commands
{
    public class AdminPoAuthCommand : ITelegramCommand
    {
        public bool CanHandle(long chatId, string command, string cleanText)
        {
            return command == "/po_auth" || command == "/po_status";
        }

        public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
        {
            if (!isAdmin)
            {
                await TelegramBotService.SendMessage(token, chatId, "❌ У вас нет прав для выполнения этой команды.");
                return;
            }

            if (command == "/po_status")
            {
                await SendStatusAsync(token, chatId);
                return;
            }

            // /po_auth [payload or session token]
            string arg = cleanText.Length > command.Length
                ? cleanText.Substring(command.Length).Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(arg))
            {
                var sb = new StringBuilder();
                sb.AppendLine("🔑 <b>Управление сессией Pocket Option WebSocket</b>\n");
                sb.AppendLine($"• Текущий статус: <code>{PocketOptionWebSocketStream.Status}</code> (Connected: <b>{PocketOptionWebSocketStream.IsConnected}</b>)");
                sb.AppendLine($"• Всего тиков за сессию: <b>{PocketOptionWebSocketStream.TotalTicksIngested:N0}</b>");
                sb.AppendLine($"• Последний тик (UTC): <code>{(PocketOptionWebSocketStream.LastTickUtc == DateTime.MinValue ? "нет" : PocketOptionWebSocketStream.LastTickUtc.ToString("yyyy-MM-dd HH:mm:ss"))}</code>\n");
                sb.AppendLine("<b>Как обновить сессию без перезапуска:</b>");
                sb.AppendLine("1️⃣ Отправьте фрейм авторизации из браузера (DevTools → Network → WS → <code>42[\"auth\",...]</code>):");
                sb.AppendLine("<code>/po_auth 42[\"auth\",{\"session\":\"...\",\"isDemo\":1,\"uid\":129671966,\"platform\":2}]</code>\n");
                sb.AppendLine("2️⃣ Или отправьте только новую строку <code>session</code> (26 символов):");
                sb.AppendLine("<code>/po_auth d725c16a468b2fe5b5f8dc105e</code>\n");
                sb.AppendLine("3️⃣ Проверить статус потока: <code>/po_status</code>");
                await TelegramBotService.SendMessage(token, chatId, sb.ToString());
                return;
            }

            var (ok, message) = await PocketOptionWebSocketStream.UpdateSessionAsync(arg);
            if (!ok)
            {
                await TelegramBotService.SendMessage(token, chatId, $"❌ <b>Ошибка обновления сессии:</b>\n{message}");
                return;
            }

            await TelegramBotService.SendMessage(token, chatId, $"⏳ <b>{message}</b>\nПроверяю переподключение к Pocket Option (3 сек)...");
            await Task.Delay(3000);
            await SendStatusAsync(token, chatId);
        }

        private readonly record struct PoStateRow(DateTime? updated_at, DateTime? last_tick_at, string? status);

        private static async Task SendStatusAsync(string token, long chatId)
        {
            string dbStatus = "N/A";
            string dbUpdated = "N/A";
            string dbLastTick = "N/A";

            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                conn.Open();
                var row = await conn.QueryFirstOrDefaultAsync<PoStateRow>(
                    "SELECT updated_at, last_tick_at, status FROM po_collector_state WHERE id = 1");
                dbStatus = row.status ?? "NULL";
                dbUpdated = row.updated_at?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "NULL";
                dbLastTick = row.last_tick_at?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "NULL";
            }
            catch (Exception ex)
            {
                dbStatus = $"Error: {ex.Message}";
            }

            var sb = new StringBuilder();
            string icon = PocketOptionWebSocketStream.IsConnected && PocketOptionWebSocketStream.Status == "ONLINE" ? "🟢" : "🟡";
            if (PocketOptionWebSocketStream.Status == "AUTH_EXPIRED") icon = "🔴";

            sb.AppendLine($"{icon} <b>Статус потока Pocket Option:</b>\n");
            sb.AppendLine($"• Состояние в памяти: <code>{PocketOptionWebSocketStream.Status}</code>");
            sb.AppendLine($"• WebSocket активен: <b>{(PocketOptionWebSocketStream.IsConnected ? "Да" : "Нет")}</b>");
            sb.AppendLine($"• Сбоев авторизации подряд: <b>{PocketOptionWebSocketStream.ConsecutiveAuthFailures}</b>");
            sb.AppendLine($"• Принято тиков: <b>{PocketOptionWebSocketStream.TotalTicksIngested:N0}</b>");
            sb.AppendLine($"• Последний тик (RAM): <code>{(PocketOptionWebSocketStream.LastTickUtc == DateTime.MinValue ? "нет" : PocketOptionWebSocketStream.LastTickUtc.ToString("yyyy-MM-dd HH:mm:ss"))} UTC</code>");
            sb.AppendLine($"• Статус в БД: <code>{dbStatus}</code>");
            sb.AppendLine($"• Последний тик (БД): <code>{dbLastTick} UTC</code>");
            sb.AppendLine($"• Сессия обновлена: <code>{dbUpdated} UTC</code>");

            await TelegramBotService.SendMessage(token, chatId, sb.ToString());
        }
    }
}
