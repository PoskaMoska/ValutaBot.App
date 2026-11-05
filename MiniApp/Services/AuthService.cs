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
        bool isProxiedExternal = context.Request.Headers.ContainsKey("X-Forwarded-For")
                              || context.Request.Headers.ContainsKey("Forwarded");

        // SRE-FIX (F1): In production cloud or proxied environments, bypass is strictly prohibited.
        // Local/test bypass requires loopback/in-memory connection AND explicit Development/Testing host environment.
        bool isVerifiedLoopback = remoteIp != null && System.Net.IPAddress.IsLoopback(remoteIp);
        string? hostEnv = (context.RequestServices?.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) as Microsoft.AspNetCore.Hosting.IWebHostEnvironment)?.EnvironmentName
                       ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        bool isExplicitDev = string.Equals(hostEnv, "Development", StringComparison.OrdinalIgnoreCase);
        bool isTestHost = string.Equals(hostEnv, "Testing", StringComparison.OrdinalIgnoreCase);
        bool hasDevBypassHeader = context.Request.Headers.TryGetValue("X-Test-Bypass", out var bypassVal) && bypassVal == "true";

        bool isProductionCloud = !isTestHost && (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID"))
            || string.Equals(hostEnv, "Production", StringComparison.OrdinalIgnoreCase));

        if (!isProductionCloud && !isProxiedExternal && (isVerifiedLoopback || (isTestHost && remoteIp == null)) && (isExplicitDev || (isTestHost && hasDevBypassHeader)))
        {
            context.Items["userId"] = 123456789L; // Mock user ID for local dev / test runner
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
            if (long.TryParse(parsed["custom_user_id"], out tgUserId) && tgUserId > 0)
            {
                string providedSign = parsed["custom_user_sign"] ?? "";
                string? authDateStr = parsed["auth_date"];

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(botToken));
                byte[] expectedHash;

                if (!string.IsNullOrEmpty(authDateStr) && long.TryParse(authDateStr, out long authDateEpoch))
                {
                    var authTime = DateTimeOffset.FromUnixTimeSeconds(authDateEpoch).UtcDateTime;
                    var now = DateTime.UtcNow;
                    // Verify TTL and clock-skew tolerance (60s future, 24h past)
                    if (authTime > now.AddSeconds(60) || (now - authTime).TotalHours > 24)
                    {
                        return (false, 0, "Custom authorization token expired or invalid timestamp");
                    }
                    expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{tgUserId}:{authDateEpoch}"));
                }
                else
                {
                    // SECURITY: Reject legacy tokens lacking auth_date to prevent permanent, non-expiring credentials
                    return (false, 0, "Legacy token without auth_date is deprecated and no longer accepted");
                }

                byte[] providedBytes;
                try
                {
                    providedBytes = Convert.FromHexString(providedSign);
                }
                catch
                {
                    return (false, 0, "Invalid custom authorization signature");
                }

                if (providedBytes.Length != expectedHash.Length ||
                    !CryptographicOperations.FixedTimeEquals(providedBytes, expectedHash))
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
