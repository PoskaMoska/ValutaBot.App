using System;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public static class AuthService
{
    public static async Task<(bool isAuthorized, string? errorMessage)> IsRequestAuthorized(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        bool isProxiedExternal = context.Request.Headers.ContainsKey("X-Forwarded-For");

        bool isProductionCloud = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID"))
            || string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase);

        // SRE-FIX: In production cloud environments, bypass is strictly prohibited.
        // Test/local bypass is ONLY permitted in local development or unit test runners.
        bool isLocalOrTestRunner = !isProductionCloud && !isProxiedExternal && (
            (remoteIp == null || System.Net.IPAddress.IsLoopback(remoteIp)) &&
            (context.Request.Headers.TryGetValue("X-Test-Bypass", out var bypassVal) && bypassVal == "true"
             || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
             || (remoteIp != null && System.Net.IPAddress.IsLoopback(remoteIp)))
        );

        if (isLocalOrTestRunner)
        {
            context.Items["userId"] = 123456789L; // Mock user ID for DB settings
            return (true, null);
        }

        string? botToken = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(botToken))
        {
            BotLogger.Warn("[Security] Auth bypassed? NO. Bot token is missing in configuration. Failing securely.");
            return (false, "Internal Server Error: Missing bot token configuration."); 
        }

        if (!context.Request.Headers.TryGetValue("X-Telegram-Init-Data", out var initDataValues))
        {
            return (false, "Missing authorization header");
        }

        string initData = initDataValues.ToString();
        if (string.IsNullOrEmpty(initData))
        {
            return (false, "Empty authorization token");
        }

        var (ok, tgUserId, sigError) = ValidateInitData(initData, botToken);
        if (!ok)
        {
            return (false, sigError);
        }

        if (!await TelegramBotService.IsUserAllowed(tgUserId))
        {
            return (false, "Access Denied: Pocket Option registration and deposit required");
        }

        context.Items["userId"] = tgUserId;
        return (true, null);
    }

    /// <summary>
    /// Validates raw Telegram init-data (header value or query parameter) against
    /// the bot token. Shared by HTTP auth and any future query-param auth flows —
    /// single HMAC-check implementation, no duplicated logic.
    /// Returns (ok, userId, error).
    /// </summary>
    public static (bool ok, long userId, string? error) ValidateInitData(string initData, string botToken)
    {
        long tgUserId = 0;

        if (string.IsNullOrEmpty(initData))
        {
            return (false, 0, "Empty authorization token");
        }

        // ─── Custom WebApp Signature Validation (with TTL and Clock Skew tolerance) ───
        if (initData.Contains("custom_user_id=") && initData.Contains("custom_user_sign="))
        {
            var parsed = HttpUtility.ParseQueryString(initData);
            if (long.TryParse(parsed["custom_user_id"], out tgUserId))
            {
                string providedSign = parsed["custom_user_sign"] ?? "";
                string? authDateStr = parsed["auth_date"];

                // SRE-SECURITY FIX: Require auth_date strictly; timeless tokens without auth_date are rejected.
                if (string.IsNullOrEmpty(authDateStr) || !long.TryParse(authDateStr, out long authDateEpoch))
                {
                    return (false, 0, "Missing or invalid auth_date: Timeless tokens are strictly rejected.");
                }

                var authTime = DateTimeOffset.FromUnixTimeSeconds(authDateEpoch).UtcDateTime;
                var now = DateTime.UtcNow;
                // SRE-FIX: Verify TTL and clock-skew tolerance (5 min future, 24h past)
                if (authTime > now.AddMinutes(5) || (now - authTime).TotalHours > 24)
                {
                    return (false, 0, "Custom authorization token expired or invalid timestamp");
                }

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(botToken));
                byte[] expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{tgUserId}:{authDateEpoch}"));

                string expectedSign = Convert.ToHexString(expectedHash).ToLowerInvariant();
                byte[] provBytes = Encoding.UTF8.GetBytes(providedSign.ToLowerInvariant());
                byte[] expBytes = Encoding.UTF8.GetBytes(expectedSign);

                if (provBytes.Length != expBytes.Length || !CryptographicOperations.FixedTimeEquals(provBytes, expBytes))
                {
                    return (false, 0, "Invalid custom authorization signature");
                }
            }
            else
            {
                return (false, 0, "Invalid custom user ID");
            }
        }
        else if (!TelegramInitDataValidator.Validate(initData, botToken, out tgUserId, out _))
        {
            return (false, 0, "Invalid Telegram authorization signature");
        }

        return (true, tgUserId, null);
    }

    public static string SanitizeAsset(string asset)
    {
        return AssetSanitizer.Sanitize(asset);
    }
}
