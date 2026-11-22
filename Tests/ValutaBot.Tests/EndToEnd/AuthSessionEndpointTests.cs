using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ValutaBot.Tests.EndToEnd;

public class AuthSessionEndpointTests : IClassFixture<ValutaBotTestFactory>
{
    private readonly ValutaBotTestFactory _factory;

    public AuthSessionEndpointTests(ValutaBotTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostAuthSession_AuthorizedBypassReturnsOk_UnauthenticatedReturns401WithCode()
    {
        using var authedClient = _factory.CreateClient();
        authedClient.DefaultRequestHeaders.Add("X-Test-Bypass", "true");

        var okResp = await authedClient.PostAsync("/api/auth/session", content: null);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        var okJson = await okResp.Content.ReadAsStringAsync();
        using var okDoc = JsonDocument.Parse(okJson);
        Assert.True(okDoc.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("OK", okDoc.RootElement.GetProperty("code").GetString());

        // Without X-Test-Bypass header, fail-closed 401 returns structured JSON error + code
        using var unauthClient = _factory.CreateClient();
        var unauthResp = await unauthClient.PostAsync("/api/auth/session", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);
        var unauthJson = await unauthResp.Content.ReadAsStringAsync();
        using var unauthDoc = JsonDocument.Parse(unauthJson);
        Assert.False(unauthDoc.RootElement.GetProperty("authenticated").GetBoolean());
        string? errCode = unauthDoc.RootElement.GetProperty("code").GetString();
        Assert.True(errCode == "TOKEN_MISSING" || errCode == "SERVER_MISCONFIGURED", $"Unexpected code: {errCode}");
    }
}
