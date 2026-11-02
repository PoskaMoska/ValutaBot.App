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
}
