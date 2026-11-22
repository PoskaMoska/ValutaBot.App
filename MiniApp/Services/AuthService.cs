using System;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public enum AuthFailureCode
{
    None,
    MissingHeader,
    EmptyToken,
    TokenExpired,
    InvalidSignature,
    LegacyToken,
    AccessDenied,
    ServerMisconfigured
}

public sealed record AuthResult(
    bool IsAuthorized,
    long UserId,
    string? ErrorMessage,
    AuthFailureCode Code,
    bool AuthenticatedByCookie = false,
    bool ShouldRenewCookie = false);

public static class AuthService
{
    public const string SessionCookieName = "valutabot_session";
    public static readonly TimeSpan SessionCookieLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan SessionRenewThreshold = TimeSpan.FromDays(7);

    public static string FailureCodeToApiString(AuthFailureCode code) => code switch
    {
        AuthFailureCode.None => "OK",
        AuthFailureCode.MissingHeader => "TOKEN_MISSING",
        AuthFailureCode.EmptyToken => "TOKEN_MISSING",
        AuthFailureCode.TokenExpired => "TOKEN_EXPIRED",
        AuthFailureCode.InvalidSignature => "INVALID_SIGNATURE",
        AuthFailureCode.LegacyToken => "TOKEN_EXPIRED",
        AuthFailureCode.AccessDenied => "ACCESS_DENIED",
        AuthFailureCode.ServerMisconfigured => "SERVER_MISCONFIGURED",
        _ => "UNAUTHORIZED"
    };

    public static async Task<(bool isAuthorized, string? errorMessage)> IsRequestAuthorized(HttpContext context)
    {
        var result = await IsRequestAuthorizedDetailed(context);
        return (result.IsAuthorized, result.ErrorMessage);
    }

    public static async Task<AuthResult> IsRequestAuthorizedDetailed(HttpContext context)
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
            return new AuthResult(true, 123456789L, null, AuthFailureCode.None);
        }

        string? botToken = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(botToken))
        {
            BotLogger.Warn("[Security] Auth bypassed? NO. Bot token is missing in configuration. Failing securely.");
            return new AuthResult(false, 0, "Internal Server Error: Missing bot token configuration.", AuthFailureCode.ServerMisconfigured);
        }

        bool hasHeader = context.Request.Headers.TryGetValue("X-Telegram-Init-Data", out var initDataValues);
        string initData = hasHeader ? initDataValues.ToString() : string.Empty;

        if (!hasHeader || string.IsNullOrEmpty(initData))
        {
            if (TryValidateSessionCookieFromContext(context, botToken, out long cookieUserId, out bool shouldRenew))
            {
                if (!await TelegramBotService.IsUserAllowed(cookieUserId))
                {
                    TryClearSessionCookie(context);
                    return new AuthResult(false, cookieUserId, "Access Denied: Pocket Option registration and deposit required", AuthFailureCode.AccessDenied);
                }

                context.Items["userId"] = cookieUserId;
                return new AuthResult(true, cookieUserId, null, AuthFailureCode.None, AuthenticatedByCookie: true, ShouldRenewCookie: shouldRenew);
            }

            return !hasHeader
                ? new AuthResult(false, 0, "Missing authorization header", AuthFailureCode.MissingHeader)
                : new AuthResult(false, 0, "Empty authorization token", AuthFailureCode.EmptyToken);
        }

        var (ok, tgUserId, sigError, failureCode) = ValidateInitDataDetailed(initData, botToken);
        if (ok)
        {
            if (!await TelegramBotService.IsUserAllowed(tgUserId))
            {
                TryClearSessionCookie(context);
                return new AuthResult(false, tgUserId, "Access Denied: Pocket Option registration and deposit required", AuthFailureCode.AccessDenied);
            }

            context.Items["userId"] = tgUserId;
            return new AuthResult(true, tgUserId, null, AuthFailureCode.None, AuthenticatedByCookie: false, ShouldRenewCookie: true);
        }

        // Only allow HttpOnly session cookie fallback when the submitted token's cryptographic HMAC
        // matched a positive userId and failed strictly due to timestamp expiry (never on forged HMAC).
        if (failureCode == AuthFailureCode.TokenExpired &&
            tgUserId > 0 &&
            TryValidateSessionCookieFromContext(context, botToken, out long fallbackUserId, out bool renewFallback) &&
            fallbackUserId == tgUserId)
        {
            if (!await TelegramBotService.IsUserAllowed(fallbackUserId))
            {
                TryClearSessionCookie(context);
                return new AuthResult(false, fallbackUserId, "Access Denied: Pocket Option registration and deposit required", AuthFailureCode.AccessDenied);
            }

            context.Items["userId"] = fallbackUserId;
            return new AuthResult(true, fallbackUserId, null, AuthFailureCode.None, AuthenticatedByCookie: true, ShouldRenewCookie: renewFallback);
        }

        return new AuthResult(false, tgUserId, sigError, failureCode);
    }

    public static async Task<bool> TryIssueSessionCookieFromQueryAsync(HttpContext context)
    {
        if (context.Response.HasStarted) return false;
        string? botToken = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(botToken)) return false;

        string? userIdParam = context.Request.Query["custom_user_id"].FirstOrDefault()
                           ?? context.Request.Query["userId"].FirstOrDefault();
        string? userSign = context.Request.Query["custom_user_sign"].FirstOrDefault()
                        ?? context.Request.Query["userSign"].FirstOrDefault();
        string? authDate = context.Request.Query["auth_date"].FirstOrDefault();

        if (string.IsNullOrEmpty(userIdParam) || string.IsNullOrEmpty(userSign) || string.IsNullOrEmpty(authDate))
            return false;

        string synthesized = $"custom_user_id={Uri.EscapeDataString(userIdParam)}&custom_user_sign={Uri.EscapeDataString(userSign)}&auth_date={Uri.EscapeDataString(authDate)}";
        var (ok, tgUserId, _) = ValidateInitData(synthesized, botToken);
        if (!ok || tgUserId <= 0) return false;

        if (!await TelegramBotService.IsUserAllowed(tgUserId))
            return false;

        IssueSessionCookie(context, tgUserId, botToken);
        return true;
    }

    public static void EnsureSessionCookie(HttpContext context, long userId)
    {
        if (context.Response.HasStarted || userId <= 0) return;
        string? botToken = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(botToken)) return;
        IssueSessionCookie(context, userId, botToken);
    }

    public static void EnsureSessionCookieIfNeeded(HttpContext context, AuthResult auth)
    {
        if (!auth.IsAuthorized || auth.UserId <= 0 || context.Response.HasStarted) return;
        string? botToken = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(botToken)) return;

        if (!auth.AuthenticatedByCookie)
        {
            if (TryValidateSessionCookieFromContext(context, botToken, out long existingId, out bool shouldRenew) &&
                existingId == auth.UserId && !shouldRenew)
            {
                return;
            }
            IssueSessionCookie(context, auth.UserId, botToken);
        }
        else if (auth.ShouldRenewCookie)
        {
            IssueSessionCookie(context, auth.UserId, botToken);
        }
    }

    public static string CreateSessionToken(long userId, string botToken, DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime;
        long issuedAt = new DateTimeOffset(now).ToUnixTimeSeconds();
        long expiresAt = new DateTimeOffset(now.Add(SessionCookieLifetime)).ToUnixTimeSeconds();
        string payload = $"v1:{userId}:{issuedAt}:{expiresAt}";

        byte[] derivedKey = DeriveSessionKey(botToken);
        using var hmac = new HMACSHA256(derivedKey);
        string sigHex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"v1.{userId}.{issuedAt}.{expiresAt}.{sigHex}";
    }

    public static bool TryValidateSessionToken(string? token, string botToken, out long userId, out bool shouldRenew, DateTimeOffset? nowUtc = null)
    {
        userId = 0;
        shouldRenew = false;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(botToken)) return false;

        string[] parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1") return false;

        if (!long.TryParse(parts[1], out long parsedUserId) || parsedUserId <= 0) return false;
        if (!long.TryParse(parts[2], out long issuedAtUnix) || !long.TryParse(parts[3], out long expiresAtUnix)) return false;

        byte[] providedSig;
        try
        {
            providedSig = Convert.FromHexString(parts[4]);
        }
        catch
        {
            return false;
        }

        string payload = $"v1:{parsedUserId}:{issuedAtUnix}:{expiresAtUnix}";
        byte[] derivedKey = DeriveSessionKey(botToken);
        using var hmac = new HMACSHA256(derivedKey);
        byte[] expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        if (providedSig.Length != expectedSig.Length || !CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
            return false;

        long nowUnix = (nowUtc ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (issuedAtUnix > nowUnix + 60 || expiresAtUnix <= nowUnix)
            return false;

        userId = parsedUserId;
        shouldRenew = (expiresAtUnix - nowUnix) < (long)SessionRenewThreshold.TotalSeconds;
        return true;
    }

    public static void IssueSessionCookie(HttpContext context, long userId, string botToken)
    {
        if (context.Response.HasStarted || userId <= 0 || string.IsNullOrEmpty(botToken)) return;
        string token = CreateSessionToken(userId, botToken);
        context.Response.Cookies.Append(SessionCookieName, token, BuildSessionCookieOptions(context, SessionCookieLifetime));
    }

    public static void TryClearSessionCookie(HttpContext context)
    {
        if (context.Response.HasStarted) return;
        if (context.Request.Cookies.ContainsKey(SessionCookieName))
        {
            context.Response.Cookies.Delete(SessionCookieName, BuildSessionCookieOptions(context, TimeSpan.Zero));
        }
    }

    private static bool TryValidateSessionCookieFromContext(HttpContext context, string botToken, out long userId, out bool shouldRenew)
    {
        userId = 0;
        shouldRenew = false;
        if (!context.Request.Cookies.TryGetValue(SessionCookieName, out string? cookieVal) || string.IsNullOrEmpty(cookieVal))
            return false;
        return TryValidateSessionToken(cookieVal, botToken, out userId, out shouldRenew);
    }

    private static CookieOptions BuildSessionCookieOptions(HttpContext context, TimeSpan lifetime)
    {
        bool isHttps = context.Request.IsHttps ||
            string.Equals(context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);

        return new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Path = "/",
            Secure = isHttps,
            SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
            MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
            Expires = lifetime > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(lifetime) : DateTimeOffset.UtcNow.AddDays(-1)
        };
    }

    private static byte[] DeriveSessionKey(string botToken)
    {
        using var kdf = new HMACSHA256(Encoding.UTF8.GetBytes("ValutaBot:SessionCookie:v1"));
        return kdf.ComputeHash(Encoding.UTF8.GetBytes(botToken));
    }

    /// <summary>
    /// Validates raw Telegram init-data (header value or query parameter) against
    /// the bot token. Shared by HTTP auth and any future query-param auth flows —
    /// single HMAC-check implementation, no duplicated logic.
    /// Returns (ok, userId, error).
    /// </summary>
    public static (bool ok, long userId, string? error) ValidateInitData(string initData, string botToken)
    {
        var (ok, userId, error, code) = ValidateInitDataDetailed(initData, botToken);
        if (!ok && code == AuthFailureCode.TokenExpired && error == "Telegram authorization token expired or invalid timestamp")
        {
            return (false, 0, "Invalid Telegram authorization signature");
        }
        return (ok, ok ? userId : 0, error);
    }

    public static (bool ok, long userId, string? error, AuthFailureCode code) ValidateInitDataDetailed(string initData, string botToken)
    {
        long tgUserId = 0;

        if (string.IsNullOrEmpty(initData))
        {
            return (false, 0, "Empty authorization token", AuthFailureCode.EmptyToken);
        }

        // ─── Custom WebApp Signature Validation (with TTL and Clock Skew tolerance) ───
        if (initData.Contains("custom_user_id=") && initData.Contains("custom_user_sign="))
        {
            var parsed = HttpUtility.ParseQueryString(initData);
            if (long.TryParse(parsed["custom_user_id"], out tgUserId) && tgUserId > 0)
            {
                string providedSign = parsed["custom_user_sign"] ?? "";
                string? authDateStr = parsed["auth_date"];

                if (string.IsNullOrEmpty(authDateStr) || !long.TryParse(authDateStr, out long authDateEpoch))
                {
                    // SECURITY: Reject legacy tokens lacking auth_date to prevent permanent, non-expiring credentials
                    return (false, 0, "Legacy token without auth_date is deprecated and no longer accepted", AuthFailureCode.LegacyToken);
                }

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(botToken));
                byte[] expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{tgUserId}:{authDateEpoch}"));

                byte[] providedBytes;
                try
                {
                    providedBytes = Convert.FromHexString(providedSign);
                }
                catch
                {
                    return (false, 0, "Invalid custom authorization signature", AuthFailureCode.InvalidSignature);
                }

                // Verify cryptographic HMAC BEFORE checking timestamp so a forged token can never masquerade as Expired
                if (providedBytes.Length != expectedHash.Length ||
                    !CryptographicOperations.FixedTimeEquals(providedBytes, expectedHash))
                {
                    return (false, 0, "Invalid custom authorization signature", AuthFailureCode.InvalidSignature);
                }

                var authTime = DateTimeOffset.FromUnixTimeSeconds(authDateEpoch).UtcDateTime;
                var now = DateTime.UtcNow;
                // Verify TTL and clock-skew tolerance (60s future, 24h past)
                if (authTime > now.AddSeconds(60) || (now - authTime).TotalHours > 24)
                {
                    return (false, tgUserId, "Custom authorization token expired or invalid timestamp", AuthFailureCode.TokenExpired);
                }

                return (true, tgUserId, null, AuthFailureCode.None);
            }

            return (false, 0, "Invalid custom user ID", AuthFailureCode.InvalidSignature);
        }

        var tgStatus = TelegramInitDataValidator.ValidateDetailed(initData, botToken, out tgUserId, out _);
        return tgStatus switch
        {
            TelegramInitDataValidationStatus.Valid => (true, tgUserId, null, AuthFailureCode.None),
            TelegramInitDataValidationStatus.MissingOrEmpty => (false, 0, "Empty authorization token", AuthFailureCode.EmptyToken),
            TelegramInitDataValidationStatus.Expired => (false, tgUserId, "Telegram authorization token expired or invalid timestamp", AuthFailureCode.TokenExpired),
            _ => (false, 0, "Invalid Telegram authorization signature", AuthFailureCode.InvalidSignature)
        };
    }

    public static string SanitizeAsset(string asset)
    {
        return AssetSanitizer.Sanitize(asset);
    }
}
