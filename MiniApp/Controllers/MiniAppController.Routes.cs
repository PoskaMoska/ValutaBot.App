using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.App.MiniApp.Models;

namespace ValutaBot.MiniApp;

public static partial class MiniAppController
{
    public static void RegisterRoutes(WebApplication app)
    {
        app.MapGet("/", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            
            bool isNgrok = (context.Request.Host.Value ?? "").Contains("ngrok", StringComparison.OrdinalIgnoreCase);
            if (isNgrok &&
                !context.Request.Headers.ContainsKey("ngrok-skip-browser-warning") &&
                !context.Request.Query.ContainsKey("ngrok_passed"))
            {
                string bypassScript = @"<!DOCTYPE html><html><head><script>
                        var xhr = new XMLHttpRequest();
                        xhr.open('GET', window.location.href, true);
                        xhr.setRequestHeader('ngrok-skip-browser-warning', 'true');
                        xhr.onreadystatechange = function () { if (xhr.readyState === 4) { var url = new URL(window.location.href); url.searchParams.set('ngrok_passed', '1'); window.location.href = url.toString(); } };
                        xhr.send();
                    </script></head><body style='background:#0d0e1e; display:flex; justify-content:center; align-items:center; height:100vh; color:#8a4bfb; font-family:sans-serif;'>Loading...</body></html>";
                await context.Response.WriteAsync(bypassScript);
                return;
            }
            await context.Response.SendFileAsync(System.IO.Path.Combine(app.Environment.WebRootPath, "index.html"));
        });



        // FIX: /api/time was called by frontend (syncTime in api.js) but never registered.
        // Every page load produced a 404, leaving timeOffset=0 and disabling clock-drift compensation.
        app.MapGet("/api/time", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return Results.Ok(new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        });

        // SELF-HEALING: Endpoint Здоровья для Railway
        app.MapGet("/api/health", async (HttpContext context) =>
        {
            try
            {
                // 1. Проверяем БД
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int>(conn, "SELECT 1");

                // 2. Проверяем ML сервис (просто пинг базового урла)
                var mlUrl = app.Configuration["MLService:BaseUrl"] ?? Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? "http://localhost:8765";
                var hc = HttpFactory?.CreateClient();
                if (hc != null)
                {
                    hc.Timeout = TimeSpan.FromSeconds(3);
                    var mlResponse = await hc.GetAsync(mlUrl);
                    bool mlIsOk = mlResponse.IsSuccessStatusCode;
                    return Results.Ok(new { status = "Healthy", db = "Ok", ml = mlIsOk ? "Ok" : "Down" });
                }
                return Results.Ok(new { status = "Healthy", db = "Ok", ml = "Unknown" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { status = "Unhealthy", error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/analyze", async Task<IResult> (HttpContext context, string? asset, string? timeframe) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            if (string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(timeframe))
                return Results.Json(new { error = "asset and timeframe are required" });

            long userId = 0;
            if (context.Items.TryGetValue("userId", out object? uidObj) && uidObj is long uid) userId = uid;

            var userSettings = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.GetSettingsAsync(userId);

            string tf = timeframe.ToLower().Trim();
            string assetTrimmed = asset.Trim();
            Console.WriteLine($"[ANALYZE] {assetTrimmed} | TF: {tf} | User: {userId}");

            try
            {
                var orchestrator = context.RequestServices.GetRequiredService<ValutaBot.MiniApp.Features.MarketAnalysis.IMarketAnalysisOrchestrator>();
                var result = await orchestrator.ExecuteAnalysisAsync(assetTrimmed, tf, userSettings);
                // result is now a strongly-typed AnalysisResponseDto — read fields directly, no JSON parsing needed
                double confidence = Math.Clamp(result.lgbmConfidence / 100.0, 0.0, 1.0);
                double variance   = 0.0; // not currently in DTO; reserved for future
                double volatility = result.atr > 0 ? result.atr : 1.0;
                bool isMlOverruled = !string.IsNullOrEmpty(result.direction)
                    && result.direction != "NEUTRAL"
                    && !string.IsNullOrEmpty(result.lgbmDirection)
                    && result.lgbmDirection != "NEUTRAL"
                    && !string.Equals(result.direction, result.lgbmDirection, StringComparison.OrdinalIgnoreCase);

                // BO Kelly Criterion
                double payout = 0.82;
                double q = 1.0 - confidence;
                double kellyPct = confidence - (q / payout);
                double fractionalKelly = kellyPct > 0 ? kellyPct * 0.5 : 0.0;
                double adjustedKelly = Math.Max(0, fractionalKelly * (1.0 - Math.Min(variance, 1.0)));

                double minVolThreshold = 0.0001;
                bool volFilterPassed = volatility >= minVolThreshold;
                if (!volFilterPassed) adjustedKelly = 0.0;

                var finalResult = new
                {
                    result = result,
                    config = new
                    {
                        ml = userSettings.EnableMl,
                        smc = userSettings.EnableSmc,
                        of = userSettings.EnableOf
                    },
                    risk_management = new
                    {
                        kelly_percentage       = Math.Round(adjustedKelly * 100.0, 2),
                        volatility_filter_passed = volFilterPassed,
                        variance_penalty       = variance,
                        volatility_value       = volatility
                    },
                    ui_flags = new
                    {
                        is_ml_overruled = isMlOverruled,
                        warning_message = isMlOverruled ? "⚠️ Консенсус (TA+OF) перевесил сигнал Нейросети" : ""
                    },
                    latency_ms         = (int)Math.Round(LatencyProbe.LastRttMs),
                    send_at_offset_ms  = LatencyProbe.SendAtOffsetMs
                };

                var options = new JsonSerializerOptions
                {
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                var json = JsonSerializer.Serialize(finalResult, options);
                return Results.Content(json, "application/json", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API ERR] /api/analyze failed: {ex}");
                return Results.Json(new { error = ex.Message });
            }
        }).RequireRateLimiting("Global");

        app.MapGet("/api/chart-ohlc", async Task<IResult> (HttpContext context, string? asset, string? timeframe, ValutaBot.MiniApp.MarketDataFetcher fetcher) =>
        {
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            if (string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(timeframe))
                return Results.Json(Array.Empty<OhlcCandle>());
            try
            {
                string clean = ValutaBot.MiniApp.AssetSanitizer.Sanitize(asset);
                DayOfWeek day = DateTime.UtcNow.DayOfWeek;
                string? symbol = ValutaBot.MiniApp.AssetSanitizer.MapSymbolByDayOfWeek(clean, day);
                var ohlc = await fetcher.FetchOhlcWithFallbackAsync(symbol, timeframe, asset, 30);
                var payload = (ohlc ?? Array.Empty<OhlcCandle>())
                    .TakeLast(30)
                    .Select(c => new
                    {
                        open = c.Open,
                        high = c.High,
                        low = c.Low,
                        close = c.Close,
                        time = c.Timestamp != default ? new DateTimeOffset(c.Timestamp).ToUnixTimeSeconds() : 0
                    }).ToArray();
                return Results.Json(payload);
            }
            catch
            {
                return Results.Json(Array.Empty<object>());
            }
        }).RequireRateLimiting("Global");

        app.MapGet("/api/stats", (Delegate)HandleGetStats).RequireRateLimiting("Global");
        app.MapGet("/api/signal-stats", (Delegate)HandleGetSignalStats).RequireRateLimiting("Global");

        // Internal endpoint for ML service -> Telegram admin notifications
        app.MapPost("/internal/notify-admins", async Task<IResult> (HttpContext context) =>
        {
            string expectedSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? _internalApiSecretFallback;
            if (!context.Request.Headers.TryGetValue("X-Internal-Secret", out var providedSecret) || providedSecret != expectedSecret)
            {
                BotLogger.Warn($"[Security] Blocked unauthorized access to /notify-admins from {context.Connection.RemoteIpAddress}");
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            }

            try
            {
                var body = await System.Text.Json.JsonSerializer.DeserializeAsync<NotifyAdminsRequest>(
                    context.Request.Body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (body == null || string.IsNullOrWhiteSpace(body.Message))
                    return Results.Json(new { error = "empty message" }, statusCode: 400);

                await TelegramBotService.SendMessageToAdmins(body.Message);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                BotLogger.Error("[InternalNotify] Error", ex);
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/fear-greed", async Task<IResult> (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            var fng = await GetFearGreedIndex();
            return Results.Json(fng);
        });

        /* Postback Endpoint */
        app.MapGet("/api/postback", async Task<IResult> (HttpContext context) =>
        {
            var query = context.Request.Query;
            
            // SECURITY: Verify Postback Secret
            string expectedSecret = Environment.GetEnvironmentVariable("POSTBACK_SECRET") ?? Guid.NewGuid().ToString();
            string providedSecret = query.TryGetValue("secret", out var secVal) ? secVal.ToString().Trim() : "";
            
            if (string.IsNullOrEmpty(providedSecret) || providedSecret != expectedSecret)
            {
                BotLogger.Warn($"[Security] Unauthorized postback attempt blocked (Invalid Secret). IP: {context.Connection.RemoteIpAddress}");
                return Results.Unauthorized();
            }

            string pocketId = query.TryGetValue("pocketId", out var pVal) ? pVal.ToString().Trim() : "";
            string status = query.TryGetValue("status", out var sVal) ? sVal.ToString().Trim().ToLower() : "";
            
            double deposit = 0;
            if (query.TryGetValue("deposit", out var dVal))
            {
                double.TryParse(dVal.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out deposit);
            }

            long chatId = 0;
            if (query.TryGetValue("chatId", out var cVal))
            {
                long.TryParse(cVal.ToString(), out chatId);
            }

            if (string.IsNullOrEmpty(pocketId))
            {
                return Results.BadRequest(new { success = false, error = "pocketId is required" });
            }

            BotLogger.Info($"[Postback] Verified Postback: pocketId={pocketId}, chatId={chatId}, status={status}, deposit={deposit}");

            await TelegramBotService.ProcessPostback(chatId, pocketId, status, deposit);

            return Results.Ok(new { success = true, message = "Postback processed successfully" });
        });
    }
}
