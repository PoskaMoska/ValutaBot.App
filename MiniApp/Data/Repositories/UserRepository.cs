using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace ValutaBot.App.MiniApp.Data.Repositories
{
    public static class UserRepository
    {
        private static readonly HashSet<long> SuperAdmins = GetSuperAdmins();

        private static HashSet<long> GetSuperAdmins()
        {
            var admins = new HashSet<long>();
            var envVar = Environment.GetEnvironmentVariable("ADMIN_CHAT_IDS");
            if (!string.IsNullOrWhiteSpace(envVar))
            {
                var parts = envVar.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (long.TryParse(p, out long id))
                    {
                        admins.Add(id);
                    }
                }
            }
            return admins;
        }
        public static async Task<bool> IsUserAllowedAsync(long chatId)
        {
            if (SuperAdmins.Contains(chatId)) return true;
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return false;
            using var conn = DbConnectionFactory.GetConnection();
            return await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM allowed_users WHERE chat_id = @chatId)", new { chatId });
        }

        public static async Task<bool> IsAdminAsync(long chatId)
        {
            if (SuperAdmins.Contains(chatId)) return true;
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return false;
            using var conn = DbConnectionFactory.GetConnection();
            return await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM admins WHERE chat_id = @chatId)", new { chatId });
        }

        public static async Task<List<long>> GetAdminChatIdsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<long>();
            using var conn = DbConnectionFactory.GetConnection();
            var result = await conn.QueryAsync<long>("SELECT chat_id FROM admins");
            return result.ToList();
        }

        public static async Task<int> GetTotalUsersCountAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return 0;
            using var conn = DbConnectionFactory.GetConnection();
            return (int)await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM all_users");
        }

        public static async Task<int> GetAllowedUsersCountAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return 0;
            using var conn = DbConnectionFactory.GetConnection();
            return (int)await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM allowed_users");
        }

        public static async Task AddAllowedUserAsync(long chatId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("INSERT INTO allowed_users (chat_id, created_at) VALUES (@chatId, @now) ON CONFLICT (chat_id) DO NOTHING", new { chatId, now = DateTime.UtcNow.ToString("o") });
        }

        public static async Task AddAdminAsync(long chatId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("INSERT INTO admins (chat_id) VALUES (@chatId) ON CONFLICT (chat_id) DO NOTHING", new { chatId });
            await conn.ExecuteAsync("INSERT INTO allowed_users (chat_id, created_at) VALUES (@chatId, @now) ON CONFLICT (chat_id) DO NOTHING", new { chatId, now = DateTime.UtcNow.ToString("o") });
        }

        public static async Task RemoveAdminAsync(long chatId)
        {
            if (SuperAdmins.Contains(chatId)) return; // Prevent removal of SuperAdmins
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("DELETE FROM admins WHERE chat_id = @chatId", new { chatId });
        }

        public static async Task RemoveAllowedUserAsync(long chatId)
        {
            if (SuperAdmins.Contains(chatId)) return; // Prevent removal of SuperAdmins
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("DELETE FROM allowed_users WHERE chat_id = @chatId", new { chatId });
        }

        public static async Task AddAllUserAsync(long chatId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("INSERT INTO all_users (chat_id, created_at) VALUES (@chatId, @now) ON CONFLICT (chat_id) DO NOTHING", new { chatId, now = DateTime.UtcNow.ToString("o") });
        }

        public static async Task<UserSettings> GetSettingsAsync(long chatId)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new UserSettings();
            using var conn = DbConnectionFactory.GetConnection();
            var settings = await conn.QueryFirstOrDefaultAsync<UserSettings>(
                "SELECT enable_ml as EnableMl, enable_smc as EnableSmc, enable_of as EnableOf FROM user_settings WHERE chat_id = @chatId", 
                new { chatId });
            
            if (settings == null)
            {
                settings = new UserSettings();
                await conn.ExecuteAsync(
                    "INSERT INTO user_settings (chat_id, enable_ml, enable_smc, enable_of) VALUES (@chatId, @EnableMl, @EnableSmc, @EnableOf)",
                    new { chatId, settings.EnableMl, settings.EnableSmc, settings.EnableOf });
            }
            return settings;
        }

        public static async Task ToggleSettingAsync(long chatId, string settingType)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            var settings = await GetSettingsAsync(chatId); // ensures row exists

            bool newVal = settingType switch
            {
                "ml" => !settings.EnableMl,
                "smc" => !settings.EnableSmc,
                "of" => !settings.EnableOf,
                _ => throw new ArgumentException("Unknown setting type")
            };

            string column = settingType switch
            {
                "ml" => "enable_ml",
                "smc" => "enable_smc",
                "of" => "enable_of",
                _ => throw new ArgumentException("Unknown setting type")
            };

            await conn.ExecuteAsync($"UPDATE user_settings SET {column} = @newVal WHERE chat_id = @chatId", new { newVal, chatId });
        }
    }

    public class UserSettings
    {
        public bool EnableMl { get; set; } = true;
        public bool EnableSmc { get; set; } = true;
        public bool EnableOf { get; set; } = true;
    }
}
