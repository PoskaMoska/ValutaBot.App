using System;
using System.Net;
using Microsoft.AspNetCore.Http;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests;

public class SecurityAndUiAuditTests
{
    [Fact]
    public async Task AuthService_RemoteIpNull_InAmbiguousEnv_DoesNotBypass()
    {
        // Simulate missing token so it fails closed if bypass fails
        TelegramNotifier.Init("123456:FAKE-BOT-TOKEN");

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null; // Kestrel didn't populate RemoteIp
        context.Request.Headers["X-Test-Bypass"] = "true";

        // Without Testing environment, bypass must NOT occur
        string? prevEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        string? prevRailwayEnv = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT");
        string? prevRailwayProj = Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID");

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Staging");
        Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("RAILWAY_PROJECT_ID", null);
        try
        {
            var (isAuthorized, error) = await AuthService.IsRequestAuthorized(context);
            // Must not be authorized via dev bypass
            Assert.False(isAuthorized);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", prevEnv);
            Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", prevRailwayEnv);
            Environment.SetEnvironmentVariable("RAILWAY_PROJECT_ID", prevRailwayProj);
        }
    }

    [Fact]
    public async Task AuthService_RemoteIpNull_InStrictTestingEnvWithBypassHeader_Authorizes()
    {
        TelegramNotifier.Init("123456:FAKE-BOT-TOKEN");

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;
        context.Request.Headers["X-Test-Bypass"] = "true";

        string? prevEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        string? prevRailwayEnv = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT");
        string? prevRailwayProj = Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID");

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("RAILWAY_PROJECT_ID", null);
        try
        {
            var (isAuthorized, error) = await AuthService.IsRequestAuthorized(context);
            Assert.True(isAuthorized);
            Assert.Equal(123456789L, (long)context.Items["userId"]!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", prevEnv);
            Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", prevRailwayEnv);
            Environment.SetEnvironmentVariable("RAILWAY_PROJECT_ID", prevRailwayProj);
        }
    }

    [Fact]
    public void WebSocketClientIpResolution_ParsesFirstHopOfXForwardedFor_WhenProxied()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.195, 10.0.0.1";

        IPAddress? clientRemoteIp = context.Connection.RemoteIpAddress;
        if (clientRemoteIp == null || IPAddress.IsLoopback(clientRemoteIp))
        {
            string rawForwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            string firstHop = rawForwarded.Split(',')[0].Trim();
            if (IPAddress.TryParse(firstHop, out var parsedForwarded))
            {
                clientRemoteIp = parsedForwarded;
            }
        }

        string clientIp = clientRemoteIp?.ToString() ?? "unknown";
        Assert.Equal("203.0.113.195", clientIp);
    }

    [Fact]
    public void WebSocketClientIpResolution_UsesUserId_WhenUserIsAuthenticated()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1"); // Shared proxy IP
        context.Items["userId"] = 987654321L;

        IPAddress? clientRemoteIp = context.Connection.RemoteIpAddress;
        string clientIp = clientRemoteIp?.ToString() ?? "unknown";
        if (context.Items.TryGetValue("userId", out var uidObj) && uidObj is long uid && uid > 0)
        {
            clientIp = $"user_{uid}";
        }

        Assert.Equal("user_987654321", clientIp);
    }

    [Fact]
    public void SessionCookie_CreateValidateAndHttpsAttributes_EnforceSecurityContract()
    {
        string botToken = "123456:FAKE-BOT-TOKEN";
        TelegramNotifier.Init(botToken);
        long userId = 987654321L;
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        string token = AuthService.CreateSessionToken(userId, botToken, t0);
        Assert.True(AuthService.TryValidateSessionToken(token, botToken, out long parsedId, out bool shouldRenew, t0.AddHours(2)));
        Assert.Equal(userId, parsedId);
        Assert.False(shouldRenew);

        Assert.True(AuthService.TryValidateSessionToken(token, botToken, out _, out shouldRenew, t0.AddDays(9)));
        Assert.True(shouldRenew);

        Assert.False(AuthService.TryValidateSessionToken(token, botToken, out _, out _, t0.AddDays(15)));

        string tampered = token[..^4] + (token.EndsWith("0000") ? "1111" : "0000");
        Assert.False(AuthService.TryValidateSessionToken(tampered, botToken, out _, out _, t0.AddHours(2)));

        // Verify Set-Cookie header Behind HTTPS Reverse Proxy (X-Forwarded-Proto: https) has HttpOnly; Secure; SameSite=None
        var httpsCtx = new DefaultHttpContext();
        httpsCtx.Request.Headers["X-Forwarded-Proto"] = "https";
        AuthService.IssueSessionCookie(httpsCtx, userId, botToken);
        string setCookie = httpsCtx.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains($"{AuthService.SessionCookieName}=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=none", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthService_ExpiredHeaderFallsBackToMatchingCookie_NeverOnTamperOrMismatch()
    {
        string botToken = "123456:FAKE-BOT-TOKEN";
        TelegramNotifier.Init(botToken);
        long userA = 111222333L;
        long userB = 444555666L;

        string cookieA = AuthService.CreateSessionToken(userA, botToken);
        long oldAuthDate = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds();
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(botToken));
        string expiredSignA = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{userA}:{oldAuthDate}"))).ToLowerInvariant();
        string expiredSignB = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{userB}:{oldAuthDate}"))).ToLowerInvariant();

        string? prevEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        string? prevRailwayEnv = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", "production");
        try
        {
            // 1. Tampered signature with valid cookie MUST fail closed
            var ctxTampered = new DefaultHttpContext();
            ctxTampered.Request.Headers["X-Telegram-Init-Data"] = $"custom_user_id={userA}&custom_user_sign={new string('0', 64)}&auth_date={oldAuthDate}";
            ctxTampered.Request.Headers["Cookie"] = $"{AuthService.SessionCookieName}={cookieA}";
            var resTampered = await AuthService.IsRequestAuthorizedDetailed(ctxTampered);
            Assert.False(resTampered.IsAuthorized);
            Assert.Equal(AuthFailureCode.InvalidSignature, resTampered.Code);

            // 2. Expired authentic header for userB with cookie for userA MUST reject user mismatch
            var ctxMismatch = new DefaultHttpContext();
            ctxMismatch.Request.Headers["X-Telegram-Init-Data"] = $"custom_user_id={userB}&custom_user_sign={expiredSignB}&auth_date={oldAuthDate}";
            ctxMismatch.Request.Headers["Cookie"] = $"{AuthService.SessionCookieName}={cookieA}";
            var resMismatch = await AuthService.IsRequestAuthorizedDetailed(ctxMismatch);
            Assert.False(resMismatch.IsAuthorized);
            Assert.Equal(AuthFailureCode.TokenExpired, resMismatch.Code);

            // 3. Expired authentic header for userA with cookie for userA advances past signature/expiry into live IsUserAllowed check -> AccessDenied
            var ctxMatched = new DefaultHttpContext();
            ctxMatched.Request.Headers["X-Telegram-Init-Data"] = $"custom_user_id={userA}&custom_user_sign={expiredSignA}&auth_date={oldAuthDate}";
            ctxMatched.Request.Headers["Cookie"] = $"{AuthService.SessionCookieName}={cookieA}";
            var resMatched = await AuthService.IsRequestAuthorizedDetailed(ctxMatched);
            Assert.False(resMatched.IsAuthorized);
            Assert.Equal(AuthFailureCode.AccessDenied, resMatched.Code);
            Assert.Equal(userA, resMatched.UserId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", prevEnv);
            Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", prevRailwayEnv);
        }
    }
}
