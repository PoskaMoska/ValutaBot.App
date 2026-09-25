// ═══════════════════════════════════════════════════════════════════════════════
// END-TO-END INTEGRATION TESTS: /api/analyze → фронтенд
//
// Что проверяет этот файл:
//   • Поднимается РЕАЛЬНЫЙ ASP.NET pipeline в памяти (без сети)
//   • MarketDataFetcher подменяется моком, возвращающим предсказуемые свечи
//   • DB, ML и WebSocket подменяются безопасными заглушками
//   • AuthService пропускает localhost-запросы автоматически (см. AuthService.cs:14)
//   • Проверяется, что JSON-ответ фронтенд получает именно такой, какой ожидает
//
// ЧТО ЭТО ЗАКРЫВАЕТ:
//   TwelveData stub → Orchestrator → DTO → Controller → JSON → (то что видит фронтенд)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.EndToEnd;

// ─── Фабрика: поднимает весь ASP.NET pipeline в памяти ───────────────────────
// Подменяет MarketDataFetcher на StubFetcher, который возвращает 80 предсказуемых
// восходящих форекс-свечей, без каких-либо реальных HTTP-запросов к TwelveData.
public class ValutaBotTestFactory : WebApplicationFactory<ValutaBot.MiniApp.TestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Говорим ASP.NET: мы в тестовой среде
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Убираем реальный MarketDataFetcher и ставим стаб
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(MarketDataFetcher));
            if (descriptor != null) services.Remove(descriptor);

            services.AddSingleton<MarketDataFetcher, StubMarketDataFetcher>();
        });
    }
}

// ─── Стаб: 80 восходящих форекс-свечей ───────────────────────────────────────
// Возвращает детерминированные данные вместо живого TwelveData.
// Это позволяет тестам быть стабильными и не зависеть от сети.
public class StubMarketDataFetcher : MarketDataFetcher
{
    private static readonly MiniAppController.OhlcCandle[] _candles;

    static StubMarketDataFetcher()
    {
        var now   = DateTime.UtcNow;
        var list  = new List<MiniAppController.OhlcCandle>();
        double p  = 1.0800;
        for (int i = 0; i < 80; i++)
        {
            double o = p;
            double h = p + 0.0003;
            double l = p - 0.0001;
            double c = p + 0.0002;    // стабильный тренд вверх
            double v = 1000 + i * 10; // растущий объём
            list.Add(new MiniAppController.OhlcCandle(o, h, l, c, v,
                now.AddMinutes(-(80 - i))));
            p = c;
        }
        _candles = list.ToArray();
    }

    public override Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(
        string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
        => Task.FromResult(_candles.TakeLast(limit).ToArray());
}

// ─── Тесты ───────────────────────────────────────────────────────────────────
public class AnalyzeEndpointTests : IClassFixture<ValutaBotTestFactory>
{
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public AnalyzeEndpointTests(ValutaBotTestFactory factory, ITestOutputHelper output)
    {
        // Запросы идут на localhost → AuthService автоматически пропускает (строка 14 AuthService.cs)
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test-Bypass", "true");
        _output = output;
    }

    // ── 1. Статус 200 и Content-Type: application/json ───────────────────────
    [Fact]
    public async Task Analyze_Returns200_WithJsonContentType()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");

        _output.WriteLine($"Status: {response.StatusCode}");
        _output.WriteLine($"ContentType: {response.Content.Headers.ContentType}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json",
            response.Content.Headers.ContentType?.ToString() ?? "");
    }

    // ── 2. JSON содержит ключ "result" с обязательными полями DTO ────────────
    [Fact]
    public async Task Analyze_ResponseJson_ContainsResultWithDtoFields()
    {
        var response  = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json      = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Response JSON (first 500 chars): {json[..Math.Min(500, json.Length)]}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Верхний уровень: result, risk_management, ui_flags
        Assert.True(root.TryGetProperty("result", out var result),
            $"Отсутствует поле 'result' в JSON: {json[..200]}");
        Assert.True(root.TryGetProperty("risk_management", out _),
            "Отсутствует поле 'risk_management'");
        Assert.True(root.TryGetProperty("ui_flags", out _),
            "Отсутствует поле 'ui_flags'");

        // Обязательные поля DTO, которые читает фронтенд
        string[] required = {
            "direction", "probability", "duration", "adaptiveReasoning",
            "taDirection",  "taConfidence",
            "ofDirection",  "ofConfidence",
            "smcDirection",
            "lgbmDirection", "lgbmConfidence",
            "uiMarketSession", "uiMarketPhase", "uiMarketEntropy",
            "expiryCandles", "atr"
        };

        foreach (var field in required)
        {
            Assert.True(result.TryGetProperty(field, out _),
                $"DTO поле '{field}' отсутствует в result. Фронтенд его ожидает!");
        }
    }

    // ── 3. direction всегда BUY или PUT ──────────────────────────────────────
    [Fact]
    public async Task Analyze_Direction_IsBuyOrPut()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        string dir = doc.RootElement
            .GetProperty("result")
            .GetProperty("direction")
            .GetString() ?? "";

        _output.WriteLine($"direction: '{dir}'");
        Assert.True(dir == "BUY" || dir == "PUT",
            $"direction='{dir}': фронтенд ожидает строго 'BUY' или 'PUT'");
    }

    // ── 3.1. Консенсусные радары (TA, SMC, OF, ML) возвращают BUY, PUT или NEUTRAL 
    [Fact]
    public async Task Analyze_ConsensusRadars_AreValidDirections()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("result");

        string[] radars = { "taDirection", "smcDirection", "ofDirection" };
        var validDirections = new[] { "BUY", "PUT", "NEUTRAL" };

        foreach (var radar in radars)
        {
            if (result.TryGetProperty(radar, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                _output.WriteLine($"{radar}: '{val}'");
                Assert.Contains(val, validDirections);
            }
        }
        
        // ML может быть null/disabled
        if (result.TryGetProperty("lgbmDirection", out var mlProp) && mlProp.ValueKind == JsonValueKind.String)
        {
            var val = mlProp.GetString();
            if (!string.IsNullOrEmpty(val))
            {
                Assert.Contains(val, validDirections);
            }
        }
    }

    // ── 4. probability в диапазоне [0, 100] ──────────────────────────────────
    [Fact]
    public async Task Analyze_Probability_InValidRange()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        int prob = doc.RootElement
            .GetProperty("result")
            .GetProperty("probability")
            .GetInt32();

        _output.WriteLine($"probability: {prob}%");
        Assert.InRange(prob, 0, 100);
    }

    // ── 5. Нет NaN/Infinity в JSON-ответе ────────────────────────────────────
    [Fact]
    public async Task Analyze_Response_ContainsNoNaNOrInfinity()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"JSON length: {json.Length}");

        // NaN и Infinity — невалидный JSON, и фронтенд на них ломается
        Assert.DoesNotContain("NaN",      json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Infinity", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── 6. kelly_percentage неотрицательный ──────────────────────────────────
    [Fact]
    public async Task Analyze_KellyPercentage_NonNegative()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        double kelly = doc.RootElement
            .GetProperty("risk_management")
            .GetProperty("kelly_percentage")
            .GetDouble();

        _output.WriteLine($"kelly_percentage: {kelly}%");
        Assert.True(kelly >= 0, $"kelly_percentage={kelly} не может быть отрицательным");
    }

    // ── 7. Разные таймфреймы — всегда ответ без ошибки ───────────────────────
    [Theory]
    [InlineData("s5")]
    [InlineData("m1")]
    [InlineData("m5")]
    [InlineData("m15")]
    [InlineData("h1")]
    public async Task Analyze_AllTimeframes_Return200(string tf)
    {
        var response = await _client.GetAsync($"/api/analyze?asset=EUR/USD&timeframe={tf}");
        var json     = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"[{tf}] Status: {response.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("result", out _),
            $"[TF={tf}] Нет поля 'result' в ответе");
    }

    // ── 8. Без параметров → 200 с error-полем (не краш) ─────────────────────
    [Fact]
    public async Task Analyze_MissingParams_ReturnsErrorNotCrash()
    {
        var response = await _client.GetAsync("/api/analyze");
        var json     = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"No-params status: {response.StatusCode}, body: {json}");
        // Должен вернуть любой JSON (с error или без), но не 500-крэш
        Assert.True(response.StatusCode != HttpStatusCode.InternalServerError,
            "Сервер упал с 500 при отсутствии параметров");
        Assert.True(json.StartsWith("{") || json.StartsWith("["),
            "Ответ должен быть валидным JSON");
    }

    // ── 9. uiMarketSession не пустая строка ──────────────────────────────────
    [Fact]
    public async Task Analyze_UiMarketSession_IsNotEmpty()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        using var doc     = JsonDocument.Parse(json);
        string session    = doc.RootElement
            .GetProperty("result")
            .GetProperty("uiMarketSession")
            .GetString() ?? "";

        _output.WriteLine($"uiMarketSession: '{session}'");
        Assert.False(string.IsNullOrWhiteSpace(session),
            "uiMarketSession не должна быть пустой строкой");
    }

    // ── 10. /api/time возвращает timestamp ───────────────────────────────────
    [Fact]
    public async Task ApiTime_Returns_ValidTimestamp()
    {
        var response = await _client.GetAsync("/api/time");
        var json     = await response.Content.ReadAsStringAsync();

        _output.WriteLine($"api/time: {json}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("t", out var t),
            "Ответ /api/time должен содержать поле 't'");
        Assert.True(t.GetInt64() > 0, "Timestamp должен быть положительным");
    }

    // ── 11. expiryCandles положительное число ────────────────────────────────
    [Fact]
    public async Task Analyze_ExpiryCandles_IsPositive()
    {
        var response = await _client.GetAsync("/api/analyze?asset=EUR/USD&timeframe=m1");
        var json     = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        int candles = doc.RootElement
            .GetProperty("result")
            .GetProperty("expiryCandles")
            .GetInt32();

        _output.WriteLine($"expiryCandles: {candles}");
        Assert.True(candles > 0, $"expiryCandles={candles}: фронтенд ожидает > 0 для отображения таймера");
    }
}
