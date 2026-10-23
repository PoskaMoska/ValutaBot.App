using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit;

namespace ValutaBot.App.MiniApp.Telegram.Commands;

/// <summary>
/// Admin command to interact with the continuous in-process saboteur:
///  - /sabotage or /break: launches an immediate on-demand full audit assault
///  - /sabotage_stats: displays real-time telemetry (attacks repelled vs breached)
/// </summary>
public class AdminSabotageCommand : ITelegramCommand
{
    public bool CanHandle(long chatId, string command, string cleanText)
    {
        return command is "/sabotage" or "/break" or "/sabotage_stats" or "/chaos" or "/chaos_stats";
    }

    public async Task ExecuteAsync(long chatId, string command, string cleanText, bool isAdmin, string token, string webAppUrl)
    {
        if (!isAdmin)
        {
            await TelegramBotService.SendMessage(token, chatId, "❌ У вас нет прав для выполнения этой команды.");
            return;
        }

        var chaosService = MiniAppController.Services?.GetService<IChaosAuditService>();
        if (chaosService == null)
        {
            await TelegramBotService.SendMessage(token, chatId, "⚠️ Сервис внутреннего диверсанта недоступен в контейнере.");
            return;
        }

        if (command is "/sabotage_stats" or "/chaos_stats")
        {
            var stats = chaosService.GetSaboteurStats();
            var sb = new StringBuilder();
            sb.AppendLine("👾 <b>Статистика Внутреннего Диверсанта (Chaos Monkey)</b>");
            sb.AppendLine();
            sb.AppendLine("⏱ <b>Режим:</b> Непрерывный фоновый (атака каждые 3 мин)");
            sb.AppendLine($"🚀 <b>Всего внутренних атак:</b> {stats.TotalAttacksLaunched}");
            sb.AppendLine($"🛡 <b>Отражено защитой:</b> {stats.AttacksRepelled} ({stats.RepelRatePct:F1}%)");
            sb.AppendLine($"💥 <b>Пробито брешей:</b> {stats.AttacksBreached}");
            sb.AppendLine($"🎯 <b>Последняя атака:</b> {stats.LastAttackScenario ?? "еще не запускалась"}");
            sb.AppendLine($"⏳ <b>Время последней атаки:</b> {(stats.LastAttackUtc.HasValue ? stats.LastAttackUtc.Value.ToString("HH:mm:ss UTC") : "нет")}");
            sb.AppendLine();
            if (stats.LastBreach != null)
            {
                sb.AppendLine($"⚠️ <b>Последняя брешь:</b> <code>{stats.LastBreach.ScenarioName}</code>");
                sb.AppendLine($"• {stats.LastBreach.Description}");
            }
            else
            {
                sb.AppendLine("🟢 <b>Активных брешей нет</b> — бот полностью устойчив к атакам.");
            }

            await TelegramBotService.SendMessage(token, chatId, sb.ToString());
            return;
        }

        // /sabotage or /break: on-demand full assault
        await TelegramBotService.SendMessage(token, chatId, "👾 <i>Внутренний диверсант начал полномасштабный штурм всех систем бота (C# пайплайн, инварианты, нейросеть)...</i>");

        var report = await chaosService.RunFullAuditAsync();

        if (report.FailedCount == 0 && report.WarningCount == 0)
        {
            await TelegramBotService.SendMessage(token, chatId,
                $"🛡️ <b>Штурм завершён: Защита бота выстояла!</b>\n\n" +
                $"Все <b>{report.TotalScenarios}</b> сценариев успешно отражены защитными механизмами бота.\n" +
                $"⏱ Время выполнения: <b>{report.TotalDurationMs}ms</b>\n" +
                $"Статус: <b>PASS</b>. Искажений и утечек не обнаружено.");
        }
        else
        {
            await TelegramBotService.SendMessage(token, chatId,
                $"🚨 <b>Внутренний диверсант пробил брешь!</b>\n\n" +
                $"Статус: <b>{report.Status}</b> (Пройдено: {report.PassedCount}/{report.TotalScenarios})\n" +
                $"❌ Критических ошибок: <b>{report.FailedCount}</b>\n" +
                $"⚠️ Замечаний: <b>{report.WarningCount}</b>\n\n" +
                $"<i>AI-диагноз и детали уязвимостей отправлены в сообщениях выше.</i>");
        }
    }
}
