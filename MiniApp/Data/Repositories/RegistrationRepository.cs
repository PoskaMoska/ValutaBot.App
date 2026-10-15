using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Data.Repositories
{
    public static class RegistrationRepository
    {
        public static async Task<long> GetRegistrationsCountAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return 0;
            using var conn = DbConnectionFactory.GetConnection();
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM registrations");
        }

        public static async Task<List<TelegramBotService.PocketRegistration>> GetLatestRegistrationsAsync(int limit = 15)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<TelegramBotService.PocketRegistration>();
            using var conn = DbConnectionFactory.GetConnection();
            var result = await conn.QueryAsync<TelegramBotService.PocketRegistration>(
                @"SELECT chat_id as ChatId, pocket_id as PocketId, has_registered as HasRegistered, has_deposited as HasDeposited, deposit_amount as DepositAmount,
                         trial_started_at as TrialStartedAt, trial_ends_at as TrialEndsAt,
                         reminder_day2_sent as ReminderDay2Sent, reminder_day3_sent as ReminderDay3Sent, trial_expired_sent as TrialExpiredSent
                  FROM registrations ORDER BY pocket_id DESC LIMIT @limit",
                new { limit });
            return result.ToList();
        }

        public static async Task<TelegramBotService.PocketRegistration?> GetPocketRegistrationAsync(string pocketId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return null;
            using var conn = DbConnectionFactory.GetConnection();
            return await conn.QueryFirstOrDefaultAsync<TelegramBotService.PocketRegistration>(
                @"SELECT chat_id as ChatId, pocket_id as PocketId, has_registered as HasRegistered, has_deposited as HasDeposited, deposit_amount as DepositAmount,
                         trial_started_at as TrialStartedAt, trial_ends_at as TrialEndsAt,
                         reminder_day2_sent as ReminderDay2Sent, reminder_day3_sent as ReminderDay3Sent, trial_expired_sent as TrialExpiredSent
                  FROM registrations WHERE pocket_id = @pocketId",
                new { pocketId });
        }

        public static async Task<TelegramBotService.PocketRegistration?> GetRegistrationByChatIdAsync(long chatId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return null;
            using var conn = DbConnectionFactory.GetConnection();
            return await conn.QueryFirstOrDefaultAsync<TelegramBotService.PocketRegistration>(
                @"SELECT chat_id as ChatId, pocket_id as PocketId, has_registered as HasRegistered, has_deposited as HasDeposited, deposit_amount as DepositAmount,
                         trial_started_at as TrialStartedAt, trial_ends_at as TrialEndsAt,
                         reminder_day2_sent as ReminderDay2Sent, reminder_day3_sent as ReminderDay3Sent, trial_expired_sent as TrialExpiredSent
                  FROM registrations WHERE chat_id = @chatId ORDER BY trial_started_at DESC NULLS LAST LIMIT 1",
                new { chatId });
        }

        public static async Task<List<TelegramBotService.PocketRegistration>> GetActiveTrialsForRemindersAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<TelegramBotService.PocketRegistration>();
            using var conn = DbConnectionFactory.GetConnection();
            var result = await conn.QueryAsync<TelegramBotService.PocketRegistration>(
                @"SELECT chat_id as ChatId, pocket_id as PocketId, has_registered as HasRegistered, has_deposited as HasDeposited, deposit_amount as DepositAmount,
                         trial_started_at as TrialStartedAt, trial_ends_at as TrialEndsAt,
                         reminder_day2_sent as ReminderDay2Sent, reminder_day3_sent as ReminderDay3Sent, trial_expired_sent as TrialExpiredSent
                  FROM registrations 
                  WHERE has_deposited = FALSE 
                    AND chat_id > 0 
                    AND trial_started_at IS NOT NULL 
                    AND trial_ends_at IS NOT NULL");
            return result.ToList();
        }

        public static async Task SaveRegistrationAsync(TelegramBotService.PocketRegistration reg)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO registrations (chat_id, pocket_id, has_registered, has_deposited, deposit_amount, trial_started_at, trial_ends_at, reminder_day2_sent, reminder_day3_sent, trial_expired_sent)
                VALUES (@ChatId, @PocketId, @HasRegistered, @HasDeposited, @DepositAmount, @TrialStartedAt, @TrialEndsAt, @ReminderDay2Sent, @ReminderDay3Sent, @TrialExpiredSent)
                ON CONFLICT (pocket_id) DO UPDATE SET
                    chat_id = EXCLUDED.chat_id,
                    has_registered = EXCLUDED.has_registered,
                    has_deposited = EXCLUDED.has_deposited,
                    deposit_amount = EXCLUDED.deposit_amount,
                    trial_started_at = COALESCE(EXCLUDED.trial_started_at, registrations.trial_started_at),
                    trial_ends_at = COALESCE(EXCLUDED.trial_ends_at, registrations.trial_ends_at),
                    reminder_day2_sent = EXCLUDED.reminder_day2_sent,
                    reminder_day3_sent = EXCLUDED.reminder_day3_sent,
                    trial_expired_sent = EXCLUDED.trial_expired_sent
            ", new { 
                reg.ChatId, 
                reg.PocketId, 
                reg.HasRegistered, 
                reg.HasDeposited, 
                reg.DepositAmount,
                reg.TrialStartedAt,
                reg.TrialEndsAt,
                reg.ReminderDay2Sent,
                reg.ReminderDay3Sent,
                reg.TrialExpiredSent 
            });
        }
    }
}
