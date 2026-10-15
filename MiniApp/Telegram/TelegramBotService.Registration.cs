using System;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public partial class TelegramBotService
{
    public class PocketRegistration
    {
        public long ChatId { get; set; }
        public string PocketId { get; set; } = "";
        public bool HasRegistered { get; set; }
        public bool HasDeposited { get; set; }
        public double DepositAmount { get; set; }
        public DateTime? TrialStartedAt { get; set; }
        public DateTime? TrialEndsAt { get; set; }
        public bool ReminderDay2Sent { get; set; }
        public bool ReminderDay3Sent { get; set; }
        public bool TrialExpiredSent { get; set; }
    }

    public static async Task ProcessPostback(long chatId, string pocketId, string status, double deposit)
    {
        if (string.IsNullOrEmpty(pocketId) || 
            pocketId.Contains("[") || pocketId.Contains("]") || 
            pocketId.Contains("{") || pocketId.Contains("}") || 
            pocketId.Equals("uid", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Postback] Ignored test postback with macro placeholder: pocketId={pocketId}");
            return;
        }

        var reg = await ValutaBot.App.MiniApp.Data.Repositories.RegistrationRepository.GetPocketRegistrationAsync(pocketId);
        if (reg == null)
        {
            reg = new PocketRegistration { PocketId = pocketId };
        }
        
        if (chatId > 0) reg.ChatId = chatId;
        
        bool isNewRegistration = !reg.HasRegistered && (status == "register" || status == "reg" || status == "lead" || status == "registration");

        if (status == "register" || status == "reg" || status == "lead" || status == "registration")
        {
            reg.HasRegistered = true;
        }
        
        if (status == "deposit" || deposit > 0)
        {
            reg.HasDeposited = true;
            reg.DepositAmount += deposit;
        }

        if ((status == "deposit" || deposit > 0) && reg.ChatId > 0)
        {
            await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAllowedUserAsync(reg.ChatId);
            string? token = TelegramNotifier.GetToken();
            if (!string.IsNullOrEmpty(token))
            {
                await SendMessage(token, reg.ChatId, "🎉 <b>Депозит подтвержден. Бессрочный доступ открыт!</b>");
                await SendUserWelcome(token, reg.ChatId, _webAppUrl);
            }
        }
        else if (isNewRegistration && reg.ChatId > 0 && !reg.HasDeposited && !reg.TrialStartedAt.HasValue)
        {
            reg.TrialStartedAt = DateTime.UtcNow;
            reg.TrialEndsAt = DateTime.UtcNow.AddDays(3);
            string? token = TelegramNotifier.GetToken();
            if (!string.IsNullOrEmpty(token))
            {
                string trialWelcomeText = 
                    $"🎉 <b>Добро пожаловать!</b> Вам открыт полный доступ к боту на 3 дня (ID: <code>{reg.PocketId}</code>).\n\n" +
                    "Рекомендуем протестировать первые сигналы на демо-счёте брокера, чтобы освоиться с таймфреймами и стратегией Smart Money. Удачных сделок!";

                await SendMessage(token, reg.ChatId, trialWelcomeText);
                await SendUserWelcome(token, reg.ChatId, _webAppUrl);
            }
        }
        
        await ValutaBot.App.MiniApp.Data.Repositories.RegistrationRepository.SaveRegistrationAsync(reg);
    }
}
