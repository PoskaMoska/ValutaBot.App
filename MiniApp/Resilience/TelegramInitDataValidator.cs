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
    private static string FullyUrlDecode(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string decoded = value;
        while (true)
        {
            string next = HttpUtility.UrlDecode(decoded);
            if (next == decoded) break;
            decoded = next;
        }
        return decoded;
    }

    public static bool Validate(string initData, string botToken, out long userId, out string? username)
    {
        userId = 0;
        username = null;

        if (string.IsNullOrEmpty(initData)) return false;

        try
        {
            // Handle cases where the entire query string was URL-encoded (e.g. delimiters are %26 instead of &)
            if (!initData.Contains("&") && initData.Contains("%26"))
            {
                initData = HttpUtility.UrlDecode(initData);
            }

            var parsed = HttpUtility.ParseQueryString(initData);
            string? hash = parsed["hash"];
            if (string.IsNullOrEmpty(hash)) return false;

            // Sort all query parameters alphabetically except hash.
            // Telegram calculates the signature based on fully URL-decoded values.
            var sortedParams = parsed.AllKeys
                .Where(k => k != "hash" && k != null)
                .OrderBy(k => k)
                .Select(k => $"{k}={FullyUrlDecode(parsed[k] ?? "")}")
                .ToList();

            string dataCheckString = string.Join("\n", sortedParams);

            // Compute secret key: HMAC_SHA256(botToken, "WebAppData")
            // Note: The key is "WebAppData" (second arg in helper), and the message is botToken (first arg in helper)
            byte[] secretKey = HMAC_SHA256(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes("WebAppData"));

            // Compute expected hash: HMAC_SHA256(dataCheckString, secretKey)
            byte[] expectedHashBytes = HMAC_SHA256(Encoding.UTF8.GetBytes(dataCheckString), secretKey);
            string expectedHash = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

            byte[] providedHashBytes;
            try 
            {
                providedHashBytes = Convert.FromHexString(hash);
            }
            catch 
            {
                Console.WriteLine($"[InitData] Hash is not valid hex.");
                return false;
            }

            if (providedHashBytes.Length != expectedHashBytes.Length || 
                !CryptographicOperations.FixedTimeEquals(providedHashBytes, expectedHashBytes))
            {
                Console.WriteLine($"[InitData] Hash mismatch detected for initData authorization.");
                return false;
            }

            // Extract user information
            string userJson = FullyUrlDecode(parsed["user"] ?? "");
            if (!string.IsNullOrEmpty(userJson))
            {
                using var doc = JsonDocument.Parse(userJson);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    userId = idProp.GetInt64();
                }
                if (doc.RootElement.TryGetProperty("username", out var uProp))
                {
                    username = uProp.GetString();
                }
            }

            // Check auth_date expiration (e.g. 24 hours)
            string? authDateStr = parsed["auth_date"];
            if (!long.TryParse(authDateStr, out long authDate))
            {
                Console.WriteLine($"[InitData] Missing or invalid auth_date.");
                return false;
            }

            var authTime = DateTimeOffset.FromUnixTimeSeconds(authDate).UtcDateTime;
            var elapsedHours = (DateTime.UtcNow - authTime).TotalHours;
            if (elapsedHours < 0 || elapsedHours > 24)
            {
                Console.WriteLine($"[InitData] Session expired or invalid time: authTime={authTime}, current={DateTime.UtcNow}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InitData] Exception during validation: {ex.Message}");
            return false;
        }
    }

    private static byte[] HMAC_SHA256(byte[] data, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }
}
