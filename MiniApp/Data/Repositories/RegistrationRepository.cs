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
                "SELECT chat_id as ChatId, pocket_id as PocketId, has_registered as HasRegistered, has_deposited as HasDeposited, deposit_amount as DepositAmount FROM registrations ORDER BY pocket_id DESC LIMIT @limit",
                new { limit });
            return result.ToList();
        }

        public static async Task<TelegramBotService.PocketRegistration?> GetPocketRegistrationAsync(string pocketId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return null;
            using var conn = DbConnectionFactory.GetConnection();
            return await conn.QueryFirstOrDefaultAsync<TelegramBotService.PocketRegistration>(
                "SELECT chat_id as ChatId, pocket_id as PocketId, has_registered as HasRegistered, has_deposited as HasDeposited, deposit_amount as DepositAmount FROM registrations WHERE pocket_id = @pocketId",
                new { pocketId });
        }

        public static async Task SaveRegistrationAsync(TelegramBotService.PocketRegistration reg)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO registrations (chat_id, pocket_id, has_registered, has_deposited, deposit_amount)
                VALUES (@ChatId, @PocketId, @HasRegistered, @HasDeposited, @DepositAmount)
                ON CONFLICT (pocket_id) DO UPDATE SET
                    chat_id = EXCLUDED.chat_id,
                    has_registered = EXCLUDED.has_registered,
                    has_deposited = EXCLUDED.has_deposited,
                    deposit_amount = EXCLUDED.deposit_amount
            ", new { reg.ChatId, reg.PocketId, reg.HasRegistered, reg.HasDeposited, reg.DepositAmount });
        }
    }
}
