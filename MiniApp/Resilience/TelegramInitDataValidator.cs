using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ValutaBot.MiniApp;

public static class TelegramInitDataValidator
{
    public static bool Validate(string initData, string botToken, out long userId, out string? username)
    {
        userId = 0;
        username = null;

        if (string.IsNullOrEmpty(initData)) return false;

        try
        {
            // Handle cases where the entire query string was URL-encoded once (e.g. delimiters are %26 instead of &)
            if (!initData.Contains("&") && initData.Contains("%26"))
            {
                initData = HttpUtility.UrlDecode(initData);
            }

            // HttpUtility.ParseQueryString already URL-decodes each parameter value exactly once,
            // matching Telegram's WebApp data_check_string specification.
            var parsed = HttpUtility.ParseQueryString(initData);
            string? hash = parsed["hash"];
            if (string.IsNullOrEmpty(hash)) return false;

            var sortedParams = parsed.AllKeys
                .Where(k => k != "hash" && k != null)
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => $"{k}={parsed[k] ?? ""}")
                .ToList();

            string dataCheckString = string.Join("\n", sortedParams);

            // Compute secret key: HMAC_SHA256(key: "WebAppData", data: botToken)
            byte[] secretKey = HMAC_SHA256(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes("WebAppData"));

            // Compute expected hash: HMAC_SHA256(key: secretKey, data: dataCheckString)
            byte[] expectedHashBytes = HMAC_SHA256(Encoding.UTF8.GetBytes(dataCheckString), secretKey);

            byte[] providedHashBytes;
            try
            {
                providedHashBytes = Convert.FromHexString(hash);
            }
            catch
            {
                Console.WriteLine("[InitData] Hash is not valid hex.");
                return false;
            }

            if (providedHashBytes.Length != expectedHashBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(providedHashBytes, expectedHashBytes))
            {
                Console.WriteLine("[InitData] Hash mismatch detected for initData authorization.");
                return false;
            }

            // Extract user information (mandatory)
            string userJson = parsed["user"] ?? "";
            if (string.IsNullOrEmpty(userJson))
            {
                Console.WriteLine("[InitData] Missing 'user' payload.");
                return false;
            }

            using (var doc = JsonDocument.Parse(userJson))
            {
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    userId = idProp.GetInt64();
                }
                if (doc.RootElement.TryGetProperty("username", out var uProp))
                {
                    username = uProp.GetString();
                }
            }

            if (userId <= 0)
            {
                Console.WriteLine("[InitData] User ID is missing or invalid (non-positive).");
                return false;
            }

            // Check auth_date expiration
            string? authDateStr = parsed["auth_date"];
            if (!long.TryParse(authDateStr, out long authDate))
            {
                Console.WriteLine("[InitData] Missing or invalid auth_date.");
                return false;
            }

            var authTime = DateTimeOffset.FromUnixTimeSeconds(authDate).UtcDateTime;
            var now = DateTime.UtcNow;
            // Allow up to 5 minutes of clock drift (client ahead of server) and max 24 hours in the past
            if (authTime > now.AddMinutes(5) || (now - authTime).TotalHours > 24)
            {
                Console.WriteLine($"[InitData] Session expired or invalid time: authTime={authTime:O}, current={now:O}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InitData] Validation exception: {ex.Message}");
            return false;
        }
    }

    private static byte[] HMAC_SHA256(byte[] data, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }
}
