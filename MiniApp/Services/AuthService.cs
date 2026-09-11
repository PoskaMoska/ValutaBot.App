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

        // ─── Standard Telegram InitData Validation ───
        if (initData.Contains("custom_user_id=") && initData.Contains("custom_user_sign="))
        {
            var parsed = HttpUtility.ParseQueryString(initData);
            if (long.TryParse(parsed["custom_user_id"], out tgUserId))
            {
                string providedSign = parsed["custom_user_sign"] ?? "";
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(botToken));
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(tgUserId.ToString()));
                string expectedSign = Convert.ToHexString(hash).ToLowerInvariant();

                if (providedSign.Length != expectedSign.Length ||
                    !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(providedSign.ToLowerInvariant()),
                        Encoding.UTF8.GetBytes(expectedSign)))
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
