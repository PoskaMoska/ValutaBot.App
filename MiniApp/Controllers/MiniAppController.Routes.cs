using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using ValutaBot.App.MiniApp.Models;

namespace ValutaBot.MiniApp;

public static partial class MiniAppController
{
    private sealed record CachedHealth(DateTime ExpiresUtc, object Payload);
    private static volatile CachedHealth? _cachedHealthResult;

    // SRE / Security: Concurrent WebSocket connection limits
    private static int _activeWebSocketConnections = 0;
    private const int MaxGlobalWebSocketConnections = 500;
    private static readonly ConcurrentDictionary<string, int> _wsConnectionsPerIp = new();
    private const int MaxWsPerIp = 20;
    private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes("{\"hb\":1}");

    public static void RegisterRoutes(WebApplication app)
    {
        // Prometheus /metrics endpoint for Grafana, Railway, and external APM scrapers
        app.MapMetrics();

        // Register domain route groups
        RegisterCoreAndHealthRoutes(app);
        RegisterAnalysisRoutes(app);
        RegisterStatsRoutes(app);
        RegisterAdminAndChaosRoutes(app);
    }

    #region Domain Group 1: Core & Health Endpoints

    private static void RegisterCoreAndHealthRoutes(WebApplication app)
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

        // /api/time for clock-drift compensation
        app.MapGet("/api/time", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return Results.Ok(new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }).RequireRateLimiting("Global");

        // Sentry verification test endpoint (protected against unauthorized quota exhaustion)
        app.MapGet("/api/sentry-test", async (HttpContext context) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            var eventId = Sentry.SentrySdk.CaptureMessage("ValutaBot Sentry Production Verification Test", Sentry.SentryLevel.Info);
            return Results.Ok(new { status = "Event emitted to Sentry", eventId = eventId.ToString() });
        }).RequireRateLimiting("Global");

        app.MapGet("/api/health/live", () => Results.Ok(new { status = "Healthy" }));
        app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy" }));
        app.MapGet("/live", () => Results.Ok(new { status = "Healthy" }));

        // SRE-FIX: Health endpoint for Railway, Kubernetes and external uptime probes (covers DB, ML, WS, TG)
        Func<HttpContext, Task<IResult>> healthHandler = async (HttpContext context) =>
        {
            var cached = _cachedHealthResult;
            if (cached != null && DateTime.UtcNow < cached.ExpiresUtc)
            {
                return Results.Ok(cached.Payload);
            }

            string dbStatus = "Unknown";
            string mlStatus = "Unknown";

            // 1. Check PostgreSQL Database
            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int>(conn, "SELECT 1");
                dbStatus = "Ok";
            }
            catch (Exception ex)
            {
                BotLogger.Error("[HealthCheck] Database check failed", ex);
                ValutaMetrics.HealthCheckFailures.WithLabels("db").Inc();
                return Results.Json(new { status = "Unhealthy", db = "Error", ws = "Unknown", tg = "Unknown", ml = "Unknown" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // 2. Check ML service ping
            try
            {
                var mlUrl = Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? app.Configuration["MLService:BaseUrl"] ?? "http://localhost:8765";
                var hc = HttpFactory?.CreateClient();
                if (hc != null)
                {
                    hc.Timeout = TimeSpan.FromSeconds(5);
                    var mlHealthUrl = $"{mlUrl.TrimEnd('/')}/health";
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var mlResponse = await hc.GetAsync(mlHealthUrl, cts.Token);
                    mlStatus = mlResponse.IsSuccessStatusCode ? "Ok" : "Down";
                    if (!mlResponse.IsSuccessStatusCode)
                    {
                        ValutaMetrics.HealthCheckFailures.WithLabels("ml").Inc();
                    }
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[HealthCheck] ML service probe slow/down: {ex.Message}");
                mlStatus = "Down";
                ValutaMetrics.HealthCheckFailures.WithLabels("ml").Inc();
            }

            // 3. Check Pocket Option Broker WebSocket stream
            string wsStatus = PocketOptionWebSocketStream.IsConnected ? "Ok" : (PriceFeed.UsePocketOption ? "Connecting" : "Disabled");

            // 4. Check Telegram Bot Poller
            string tgStatus = TelegramBotService.IsPollerHealthy ? "Ok" : (TelegramBotService.IsLeader ? "Degraded" : "Standby");

            bool isHealthy = dbStatus == "Ok";
            string overallStatus = isHealthy ? (wsStatus == "Ok" && (tgStatus == "Ok" || tgStatus == "Standby") ? "Healthy" : "Degraded") : "Unhealthy";

            var payload = new { status = overallStatus, db = dbStatus, ml = mlStatus, ws = wsStatus, tg = tgStatus };
            _cachedHealthResult = new CachedHealth(DateTime.UtcNow.AddSeconds(3), payload);

            if (!isHealthy)
            {
                return Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok(payload);
        };

        app.MapGet("/api/health", healthHandler);
        app.MapGet("/health", healthHandler);
        app.MapGet("/ready", healthHandler);

        app.MapGet("/api/fear-greed", async Task<IResult> (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            var fng = await GetFearGreedIndex();
            return Results.Json(fng);
        }).RequireRateLimiting("Global");

        /* Postback Endpoint */
        app.MapGet("/api/postback", async Task<IResult> (HttpContext context) =>
        {
            var query = context.Request.Query;
            
            // SRE-FIX: Fail-closed verification with constant-time comparison
            string? expectedSecret = Environment.GetEnvironmentVariable("POSTBACK_SECRET");
            if (string.IsNullOrWhiteSpace(expectedSecret))
            {
                BotLogger.Error("[Security] POSTBACK_SECRET is not configured on server. Postback rejected.");
                return Results.Problem("Postback endpoint is misconfigured.", statusCode: StatusCodes.Status500InternalServerError);
            }

            string providedSecret = query.TryGetValue("secret", out var secVal) ? secVal.ToString().Trim() : "";
            byte[] expHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expectedSecret));
            byte[] provHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(providedSecret));

            if (!CryptographicOperations.FixedTimeEquals(expHash, provHash))
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
        }).RequireRateLimiting("Global");
    }

    #endregion

    #region Domain Group 2: Analysis & Realtime Data Endpoints

    private static void RegisterAnalysisRoutes(WebApplication app)
    {
        app.MapGet("/api/analyze", async Task<IResult> (HttpContext context, string? asset, string? timeframe, double? payout) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            if (string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(timeframe))
                return Results.Json(new { error = "asset and timeframe are required" }, statusCode: StatusCodes.Status400BadRequest);

            long userId = 0;
            if (context.Items.TryGetValue("userId", out object? uidObj) && uidObj is long uid) userId = uid;

            var userSettings = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.GetSettingsAsync(userId);

            string tf = TimeframeNormalizer.Normalize(timeframe);
            string assetTrimmed = asset.Trim();
            Console.WriteLine($"[ANALYZE] {assetTrimmed} | TF: {tf} | User: {userId}");

            try
            {
                var orchestrator = context.RequestServices.GetRequiredService<ValutaBot.MiniApp.Features.MarketAnalysis.IMarketAnalysisOrchestrator>();
                var result = await orchestrator.ExecuteAnalysisAsync(assetTrimmed, tf, userSettings);
                bool isNeutralSignal = string.IsNullOrEmpty(result.direction)
                    || string.Equals(result.direction, "NEUTRAL", StringComparison.OrdinalIgnoreCase);
                double effectiveProbPct = result.probability > 0 ? result.probability : result.lgbmConfidence;
                double confidence = isNeutralSignal ? 0.5 : Math.Clamp(effectiveProbPct / 100.0, 0.0, 1.0);
                double variance   = 0.0;
                double volatility = result.atr > 0 ? result.atr : 1.0;
                bool isMlOverruled = !isNeutralSignal
                    && !string.IsNullOrEmpty(result.lgbmDirection)
                    && result.lgbmDirection != "NEUTRAL"
                    && !string.Equals(result.direction, result.lgbmDirection, StringComparison.OrdinalIgnoreCase);

                // BO Kelly Criterion
                double effectivePayout = 0.82;
                if (payout.HasValue && double.IsFinite(payout.Value) && payout.Value > 0.0)
                {
                    double pVal = payout.Value;
                    effectivePayout = pVal > 1.0 ? pVal / 100.0 : pVal;
                    effectivePayout = Math.Clamp(effectivePayout, 0.50, 0.98);
                }
                double breakEvenProb = 1.0 / (1.0 + effectivePayout);
                double q = 1.0 - confidence;
                double kellyPct = (isNeutralSignal || confidence <= breakEvenProb || effectivePayout <= 0.0) ? 0.0 : confidence - (q / effectivePayout);
                double fractionalKelly = kellyPct > 0 ? kellyPct * 0.5 : 0.0;
                double adjustedKelly = Math.Min(0.25, Math.Max(0, fractionalKelly * (1.0 - Math.Min(variance, 1.0))));

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
                        payout_rate            = Math.Round(effectivePayout * 100.0, 1),
                        break_even_winrate     = Math.Round(breakEvenProb * 100.0, 2),
                        kelly_percentage       = Math.Round(adjustedKelly * 100.0, 2),
                        volatility_filter_passed = volFilterPassed,
                        variance_penalty       = variance,
                        volatility_value       = volatility
                    },
                    ui_flags = new
                    {
                        is_ml_overruled = isMlOverruled,
                        warning_message = isMlOverruled ? "⚠️ Консенсус (TA+SMC) перевесил сигнал Нейросети" : ""
                    },
                    latency_ms         = (int)Math.Round(LatencyProbe.LastRttMs),
                    send_at_offset_ms  = LatencyProbe.SendAtOffsetMs
                };

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                var json = JsonSerializer.Serialize(finalResult, options);
                return Results.Content(json, "application/json", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API ERR] /api/analyze failed: {ex}");
                return Results.Json(new { error = "Внутренняя ошибка сервера при анализе данных" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }).RequireRateLimiting("Analyze");

        app.MapGet("/api/chart-ohlc", async Task<IResult> (HttpContext context, string? asset, string? timeframe, ValutaBot.MiniApp.MarketDataFetcher fetcher) =>
        {
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            if (string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(timeframe))
                return Results.Json(Array.Empty<OhlcCandle>());
            try
            {
                string tf = TimeframeNormalizer.Normalize(timeframe);
                string clean = ValutaBot.MiniApp.AssetSanitizer.Sanitize(asset);
                DayOfWeek day = DateTime.UtcNow.DayOfWeek;
                string? symbol = ValutaBot.MiniApp.AssetSanitizer.MapSymbolByDayOfWeek(clean, day);
                var ohlc = await fetcher.FetchOhlcWithFallbackAsync(symbol, tf, asset, 30);
                var payload = (ohlc ?? [])
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

        app.Map("/ws/prices", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string? asset = context.Request.Query["asset"];
            if (string.IsNullOrWhiteSpace(asset))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // 1. WebSocket Authentication (Query String token/initData or Header)
            if (!context.Request.Headers.ContainsKey("X-Telegram-Init-Data"))
            {
                string? queryAuth = context.Request.Query["token"].FirstOrDefault()
                                 ?? context.Request.Query["initData"].FirstOrDefault();
                if (!string.IsNullOrEmpty(queryAuth))
                {
                    context.Request.Headers["X-Telegram-Init-Data"] = queryAuth;
                }
            }

            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync($"Unauthorized: {authError}");
                return;
            }

            // 2. Per-IP and Global WebSocket Connection Limiting (DoS protection)
            System.Net.IPAddress? clientRemoteIp = ResolveClientIp(context);
            string clientIp = clientRemoteIp?.ToString() ?? "unknown";
            if (context.Items.TryGetValue("userId", out var uidObj) && uidObj is long uid && uid > 0)
            {
                clientIp = $"user_{uid}";
            }
            int currentIpCount = _wsConnectionsPerIp.AddOrUpdate(clientIp, 1, (_, count) => count + 1);
            if (currentIpCount > MaxWsPerIp)
            {
                int rem = _wsConnectionsPerIp.AddOrUpdate(clientIp, 0, (_, count) => Math.Max(0, count - 1));
                if (rem == 0) _wsConnectionsPerIp.TryRemove(new KeyValuePair<string, int>(clientIp, 0));
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsync("Too many connections from your IP.");
                return;
            }

            if (Interlocked.Increment(ref _activeWebSocketConnections) > MaxGlobalWebSocketConnections)
            {
                Interlocked.Decrement(ref _activeWebSocketConnections);
                int rem = _wsConnectionsPerIp.AddOrUpdate(clientIp, 0, (_, count) => Math.Max(0, count - 1));
                if (rem == 0) _wsConnectionsPerIp.TryRemove(new KeyValuePair<string, int>(clientIp, 0));
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Server connection limit reached.");
                return;
            }

            WebSocket? webSocket = null;
            Task<WebSocketReceiveResult>? receiveTask = null;
            try
            {
                webSocket = await context.WebSockets.AcceptWebSocketAsync();
                var cleanAsset = ValutaBot.MiniApp.AssetSanitizer.Sanitize(asset);

                byte[] buffer = new byte[1024];
                var ct = context.RequestAborted;
                double lastPrice = 0.0;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                receiveTask = webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    bool hasPrice = false;
                    double currentPrice = 0.0;

                    if (PriceFeed.UsePocketOption && PocketOptionWebSocketStream.TryGetLivePrice(cleanAsset, out double poPrice, 3.0))
                    {
                        currentPrice = poPrice;
                        hasPrice = true;
                    }

                    if (hasPrice && Math.Abs(currentPrice - lastPrice) > 1e-7)
                    {
                        lastPrice = currentPrice;
                        string json = $"{{\"price\":{currentPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                        sw.Restart();
                    }
                    else if (sw.ElapsedMilliseconds >= 10000)
                    {
                        // Heartbeat every 10s to keep client watchdog alive during quiet markets (zero-alloc static buffer)
                        await webSocket.SendAsync(new ArraySegment<byte>(HeartbeatBytes), WebSocketMessageType.Text, true, ct);
                        sw.Restart();
                    }

                    var delayTask = Task.Delay(250, ct);
                    var completed = await Task.WhenAny(receiveTask, delayTask);
                    if (completed == receiveTask)
                    {
                        var res = await receiveTask;
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            // RFC 6455: Use CloseOutputAsync when replying to an incoming client Close frame
                            await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            break;
                        }
                        receiveTask = webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    }
                }

                // Graceful Shutdown on host stop / container restart (SIGTERM -> 1001 EndpointUnavailable)
                if (ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                {
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Server restart", shutdownCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS /ws/prices] Connection closed: {ex.Message}");
            }
            finally
            {
                if (receiveTask != null && !receiveTask.IsCompleted)
                {
                    _ = receiveTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
                webSocket?.Dispose();
                Interlocked.Decrement(ref _activeWebSocketConnections);
                int rem = _wsConnectionsPerIp.AddOrUpdate(clientIp, 0, (_, count) => Math.Max(0, count - 1));
                if (rem == 0) _wsConnectionsPerIp.TryRemove(new KeyValuePair<string, int>(clientIp, 0));
            }
        });

        // Endpoint for Tampermonkey PO Data Vacuum (loopback or internal secret only)
        app.MapPost("/api/collector/tick", async (HttpContext context) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            bool isLoopback = !context.Request.Headers.ContainsKey("X-Forwarded-For")
                && remoteIp != null && System.Net.IPAddress.IsLoopback(remoteIp);
            string expectedSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? _internalApiSecretFallback;
            string providedSecret = context.Request.Headers["X-Internal-Secret"].ToString();
            bool hasValidSecret = IsSecretValidConstantTime(providedSecret, expectedSecret);

            if (!isLoopback && !hasValidSecret)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (context.Request.ContentLength > 65536)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var reader = new System.IO.StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            if (body.Length > 65536)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            const string logPath = "po_vacuum_ticks.log";
            try
            {
                var fi = new System.IO.FileInfo(logPath);
                if (fi.Exists && fi.Length > 10 * 1024 * 1024)
                {
                    System.IO.File.Move(logPath, logPath + ".old", overwrite: true);
                }
                System.IO.File.AppendAllText(logPath, $"{DateTime.UtcNow:O} | {body}\n");
            }
            catch { }
            BotLogger.Info($"[PO Vacuum] Captured raw tick: {body}");
            return Results.Ok();
        }).RequireRateLimiting("Global");
    }

    #endregion

    #region Domain Group 3: Stats & Metrics Endpoints

    private static void RegisterStatsRoutes(WebApplication app)
    {
        app.MapGet("/api/stats/overnight", async (HttpContext context) =>
        {
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                var total = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= '2026-10-15 21:55:00+00'::timestamptz;");
                var lastNight = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= (NOW() - INTERVAL '12 hours') AND direction IN ('BUY', 'PUT') AND was_win IS NOT NULL;");
                var wins = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= (NOW() - INTERVAL '12 hours') AND direction IN ('BUY', 'PUT') AND was_win = true;");
                var datasetSamples12h = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM trade_outcomes WHERE created_at >= (NOW() - INTERVAL '12 hours') AND features_json IS NOT NULL;");
                var losses = lastNight - wins;
                
                var candles = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM subminute_candles WHERE asset LIKE '%_PO';");
                var ticks = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM subminute_candles WHERE asset LIKE '%_PO' AND open_time >= (NOW() - INTERVAL '12 hours')::text;");
                var rawTicks = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM po_ticks;");
                
                double winrate = lastNight > 0 ? (double)wins / lastNight * 100 : 0;
                
                return Results.Ok(new {
                    TotalPoEraOutcomesInDb = total,
                    LiveTradesOvernight12h = lastNight,
                    DatasetSamplesOvernight12h = datasetSamples12h,
                    WinsOvernight = wins,
                    LossesOvernight = losses,
                    WinRateOvernight = Math.Round(winrate, 1) + "%",
                    TotalPoSubminuteCandles = candles,
                    CandlesGatheredOvernight = ticks,
                    TotalPoRawTicks = rawTicks,
                    Status = "Database connection stable."
                });
            }
            catch (Exception ex)
            {
                return SafeErrorResponse(context, ex);
            }
        }).RequireRateLimiting("Global");

        app.MapGet("/api/stats/weights", (HttpContext context) =>
        {
            return Results.Json(new { status = "deprecated", message = "OnlineMetaLearner deprecated; Pure Neural Brain (PatchTST) is active." });
        });
        
        app.MapGet("/api/stats/ml", async (HttpContext context) =>
        {
            var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
            if (!isAuthorized)
                return Results.Json(new { error = authError }, statusCode: 401);

            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                var stats = await Dapper.SqlMapper.QueryAsync(conn, @"
                    SELECT 
                        asset, 
                        timeframe, 
                        COUNT(*) as TotalTrades, 
                        SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) as Wins,
                        ROUND(SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) as WinRate
                    FROM trade_outcomes
                    WHERE features_json IS NOT NULL
                      AND direction IN ('BUY', 'PUT')
                      AND was_win IS NOT NULL
                      AND created_at >= '2026-10-15 21:55:00+00'::timestamptz
                    GROUP BY asset, timeframe
                    ORDER BY asset, timeframe
                ");
                return Microsoft.AspNetCore.Http.Results.Json(stats);
            }
            catch (System.Exception ex)
            {
                return SafeErrorResponse(context, ex);
            }
        }).RequireRateLimiting("Global");

        app.MapGet("/api/stats", (Delegate)HandleGetStats).RequireRateLimiting("Global");
        
        app.MapGet("/api/stats/dataset-audit", async (HttpContext context) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            try
            {
                var mlUrl = Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? "http://localhost:8765";
                if (HttpFactory != null)
                {
                    var hc = HttpFactory.CreateClient();
                    hc.Timeout = TimeSpan.FromSeconds(15);
                    var resp = await hc.GetAsync($"{mlUrl.TrimEnd('/')}/diagnostics/audit?notify=false");
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync();
                        return Results.Content(json, "application/json");
                    }
                }
                return Results.Json(new { status = "unavailable" }, statusCode: 503);
            }
            catch (Exception ex)
            {
                return SafeErrorResponse(context, ex);
            }
        }).RequireRateLimiting("Global");

        app.MapGet("/api/signal-stats", (Delegate)HandleGetSignalStats).RequireRateLimiting("Global");
    }

    #endregion

    #region Domain Group 4: Admin, Chaos & Internal Endpoints

    private static void RegisterAdminAndChaosRoutes(WebApplication app)
    {
        // Autonomous Chaos Engineering & Fuzzing Audit Endpoints
        app.MapGet("/api/chaos/report", async (HttpContext context, ValutaBot.MiniApp.Features.ChaosAudit.IChaosAuditService chaos) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var rep = chaos.GetLatestReport();
            return rep != null ? Results.Json(rep) : Results.Json(new { status = "NO_REPORT_YET", message = "Run POST /api/chaos/run to execute initial audit." });
        }).RequireRateLimiting("Global");

        app.MapPost("/api/chaos/run", async (HttpContext context, ValutaBot.MiniApp.Features.ChaosAudit.IChaosAuditService chaos) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context, requireSystemLevel: true))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var report = await chaos.RunFullAuditAsync();
            return Results.Json(report);
        }).RequireRateLimiting("Global");

        app.MapPost("/api/chaos/run/{category}", async (HttpContext context, string category, ValutaBot.MiniApp.Features.ChaosAudit.IChaosAuditService chaos) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context, requireSystemLevel: true))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (Enum.TryParse<ValutaBot.MiniApp.Features.ChaosAudit.Models.ChaosCategory>(category, true, out var cat))
            {
                var report = await chaos.RunCategoryAuditAsync(cat);
                return Results.Json(report);
            }
            return Results.BadRequest(new { error = $"Unknown category '{category}'. Available: Invariants, DataAndDbFaults, StressAndOutages, TradingLogicQuality" });
        }).RequireRateLimiting("Global");

        app.MapGet("/api/admin/clock-skew", async (HttpContext context) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.Json(new { snapshot = ValutaBot.MiniApp.Services.ClockSkewMonitor.GetLatest(), utcNow = DateTime.UtcNow });
        }).RequireRateLimiting("Global");

        app.MapGet("/api/admin/db-stats", async (HttpContext context) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                var telemetry = await ValutaBot.MiniApp.Services.DataRetentionService.MeasureStorageTelemetryAsync(conn);
                return Results.Json(telemetry);
            }
            catch (Exception ex)
            {
                return SafeErrorResponse(context, ex);
            }
        }).RequireRateLimiting("Global");

        app.MapPost("/api/admin/db-maintenance", async (HttpContext context, bool? full) =>
        {
            bool requiresSystem = full == true;
            if (!await HasInternalSecretOrAuthAsync(context, requireSystemLevel: requiresSystem))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                var result = await ValutaBot.MiniApp.Services.DataRetentionService.RunEmergencyCleanupAsync(allowVacuumFull: full == true);
                return Results.Json(new { status = "ok", telemetry = result });
            }
            catch (Exception ex)
            {
                return SafeErrorResponse(context, ex);
            }
        }).RequireRateLimiting("Global");

        // Internal endpoint for ML service -> Telegram admin notifications (Constant-time secret comparison + Rate Limiting)
        app.MapPost("/internal/notify-admins", async Task<IResult> (HttpContext context) =>
        {
            string expectedSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? _internalApiSecretFallback;
            string providedSecret = context.Request.Headers["X-Internal-Secret"].ToString();

            if (!IsSecretValidConstantTime(providedSecret, expectedSecret))
            {
                BotLogger.Warn($"[Security] Blocked unauthorized access to /notify-admins from {context.Connection.RemoteIpAddress}");
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            }

            if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > 65536)
            {
                return Results.Json(new { error = "Payload too large" }, statusCode: 413);
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
                return SafeErrorResponse(context, ex);
            }
        }).RequireRateLimiting("Global");

        /* Admin MetaLearner Endpoints (Deprecated) */
        app.MapPost("/api/admin/reset-meta-weights", async (HttpContext context) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.Ok(new { status = "deprecated", message = "OnlineMetaLearner deprecated; Pure Neural Brain (PatchTST) is active." });
        });

        app.MapGet("/api/admin/meta-weights", async (HttpContext context, string? asset, string? timeframe) =>
        {
            if (!await HasInternalSecretOrAuthAsync(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.Ok(new { status = "deprecated", message = "OnlineMetaLearner deprecated; Pure Neural Brain (PatchTST) is active." });
        });
    }

    #endregion

    private static IResult SafeErrorResponse(HttpContext ctx, Exception ex, int status = 500)
    {
        string traceId = Guid.NewGuid().ToString("N")[..8];
        BotLogger.Error($"[Err:{traceId}] {ctx.Request.Path}", ex);
        return Results.Json(new { error = "Internal server error", traceId }, statusCode: status);
    }

    private static async Task<bool> HasInternalSecretOrAuthAsync(HttpContext context, bool requireSystemLevel = false)
    {
        // 1. Constant-time SHA-256 check on X-Internal-Secret
        string expectedSecret = Environment.GetEnvironmentVariable("INTERNAL_API_SECRET") ?? _internalApiSecretFallback;
        string providedSecret = context.Request.Headers["X-Internal-Secret"].ToString();
        if (IsSecretValidConstantTime(providedSecret, expectedSecret))
        {
            return true;
        }

        // 2. Constant-time SHA-256 check on X-Admin-Key
        string? adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
        string providedAdminKey = context.Request.Headers["X-Admin-Key"].ToString();
        if (!string.IsNullOrEmpty(adminKey) && IsSecretValidConstantTime(providedAdminKey, adminKey))
        {
            return true;
        }

        if (requireSystemLevel)
        {
            return false;
        }

        // 3. Admin session authorization via Telegram MiniApp
        var (isAuthorized, _) = await AuthService.IsRequestAuthorized(context);
        if (!isAuthorized || context.Items["userId"] is not long uid) return false;
        return await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsAdminAsync(uid);
    }
}
