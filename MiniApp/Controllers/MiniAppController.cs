using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;
using Prometheus;
using ValutaBot.App.MiniApp.Models;

namespace ValutaBot.MiniApp;

public static partial class MiniAppController
{
    public static volatile bool IsSelfRecycleRequested = false;

    public static bool IsSecretValidConstantTime(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
            return false;
        byte[] provBytes = Encoding.UTF8.GetBytes(provided);
        byte[] expBytes = Encoding.UTF8.GetBytes(expected);
        byte[] provHash = SHA256.HashData(provBytes);
        byte[] expHash = SHA256.HashData(expBytes);
        return CryptographicOperations.FixedTimeEquals(provHash, expHash);
    }

    public static bool IsPrivateOrLoopback(System.Net.IPAddress? ip)
    {
        if (ip == null) return true;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10) return true; // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true; // 192.168.0.0/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // 100.64.0.0/10 CGNAT (Railway/K8s)
            if (b[0] == 169 && b[1] == 254) return true; // 169.254.0.0/16 Link-local
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            byte[] b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // Unique local fc00::/7
        }
        return false;
    }

    public static System.Net.IPAddress? ResolveClientIp(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp != null && !IsPrivateOrLoopback(remoteIp))
        {
            return remoteIp;
        }

        // 1. Cloudflare prioritized (cannot be spoofed from client)
        string cfIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? "";
        if (!string.IsNullOrEmpty(cfIp) && System.Net.IPAddress.TryParse(cfIp.Trim(), out var parsedCf))
        {
            return parsedCf;
        }

        // 2. X-Real-IP
        string realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault() ?? "";
        if (!string.IsNullOrEmpty(realIp) && System.Net.IPAddress.TryParse(realIp.Trim(), out var parsedReal))
        {
            return parsedReal;
        }

        // 3. X-Forwarded-For: parse right-to-left to find first non-private client IP
        string rawForwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "";
        if (!string.IsNullOrWhiteSpace(rawForwarded))
        {
            var hops = rawForwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = hops.Length - 1; i >= 0; i--)
            {
                if (System.Net.IPAddress.TryParse(hops[i], out var parsedHop) && !IsPrivateOrLoopback(parsedHop))
                {
                    return parsedHop;
                }
            }
            if (hops.Length > 0 && System.Net.IPAddress.TryParse(hops[0], out var firstHop))
            {
                return firstHop;
            }
        }

        return remoteIp;
    }

    private static bool IsTransientSentryException(Exception? ex)
    {
        if (ex == null) return false;
        if (ex is Polly.Timeout.TimeoutRejectedException) return true;
        if (ex is System.Net.WebSockets.WebSocketException) return true;
        if (ex is System.Net.Sockets.SocketException) return true;
        if (ex is OperationCanceledException or TaskCanceledException) return true;
        if (ex is System.IO.IOException ioEx && (ioEx.InnerException is System.Net.Sockets.SocketException ||
            ioEx.Message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
            ioEx.Message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
            ioEx.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase))) return true;
        if (ex is System.Net.Http.HttpRequestException httpEx && (httpEx.InnerException is System.Net.Sockets.SocketException || httpEx.InnerException is System.IO.IOException)) return true;
        if (ex is Npgsql.NpgsqlException npgEx && (npgEx.IsTransient || npgEx.InnerException is System.Net.Sockets.SocketException || npgEx.InnerException is System.IO.IOException)) return true;
        return false;
    }

    private static readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public static string? LastExceptionMessage { get; set; }
    private static readonly string _internalApiSecretFallback = Guid.NewGuid().ToString();

    public record OhlcCandle(double Open, double High, double Low, double Close, double Volume, DateTime Timestamp = default)
    {
        public bool IsValid => double.IsFinite(Open) && double.IsFinite(High) && double.IsFinite(Low) &&
                               double.IsFinite(Close) && double.IsFinite(Volume) &&
                               Open > 0 && High >= Low && Low > 0 && Close > 0 && Volume >= 0;
    }
    public record NotifyAdminsRequest(string Message, string ParseMode = "HTML");

    public static System.Net.Http.IHttpClientFactory? HttpFactory { get; set; }
    public static IServiceProvider? Services { get; set; }

    public static async Task StartAsync(string[] args, int port = 5000)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("[Live Core] TradeBE_bot \"v\" MiniApp Server");

        string? envPort = Environment.GetEnvironmentVariable("PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int parsedPort))
        {
            port = parsedPort;
        }

        Console.WriteLine($"[+] Port: {port}");
        Console.WriteLine("========================================");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = System.IO.Path.Combine(AppContext.BaseDirectory, "MiniApp", "wwwroot")
        });

        // Sentry production error tracking & APM (if SENTRY_DSN configured)
        string? sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN") 
                         ?? Environment.GetEnvironmentVariable("SENTRY_DSN_CSHARP") 
                         ?? builder.Configuration["Sentry:Dsn"];
        if (!string.IsNullOrWhiteSpace(sentryDsn))
        {
            builder.WebHost.UseSentry(options =>
            {
                options.Dsn = sentryDsn;
                options.Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "production";
                options.TracesSampleRate = 0.2;
                options.AutoSessionTracking = true;

                // SRE-FIX: Filter out Polly events, transient broker disconnects, and network jitter to protect Sentry quota
                options.AddExceptionFilterForType<Polly.Timeout.TimeoutRejectedException>();
                options.AddExceptionFilterForType<System.Net.WebSockets.WebSocketException>();
                options.AddExceptionFilterForType<System.Net.Sockets.SocketException>();
                options.AddExceptionFilterForType<OperationCanceledException>();
                options.SetBeforeSend((sentryEvent, hint) =>
                {
                    if (sentryEvent.Logger != null && (sentryEvent.Logger.StartsWith("Polly") || sentryEvent.Logger.StartsWith("Microsoft.Extensions.Http.Resilience")))
                    {
                        return null; // Drop internal resilience logs
                    }
                    if (IsTransientSentryException(sentryEvent.Exception))
                    {
                        return null;
                    }
                    if (sentryEvent.Message?.Message is { } msg)
                    {
                        if (msg.Contains("[PO WS] Connection error", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("[PO WS] Watchdog detected silent drop", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("[PO WS] Receive frame timed out", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("Transient polling jitter", StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }
                    }
                    return sentryEvent;
                });
            });
            Console.WriteLine("[Sentry] Production monitoring initialized.");
        }

        // Security: HttpClient logs full request URLs at Information level. The Telegram API URL
        // contains the bot token, so keep these categories at Warning to avoid leaking it into logs.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Logging.AddFilter("Polly", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Http.Resilience", Microsoft.Extensions.Logging.LogLevel.Warning);
        
        var botSettings = builder.Configuration.GetSection("TradingBotSettings").Get<TradingBotSettings>() ?? new TradingBotSettings();
        builder.Services.Configure<TradingBotSettings>(builder.Configuration.GetSection("TradingBotSettings"));
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore;
            options.ShutdownTimeout = TimeSpan.FromSeconds(15);
            options.ServicesStopConcurrently = true;
        });

        // Register Engines and Services in DI
        builder.Services.AddSingleton<MarketDataFetcher>();
        builder.Services.AddSingleton<TechnicalAnalysisEngine>();
        builder.Services.AddSingleton<ITechnicalAnalysisEngine>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        builder.Services.AddSingleton<IMathEngine>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        builder.Services.AddSingleton<IMarketAnalyzer>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        builder.Services.AddSingleton<IRiskGatekeeper>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        // AutoCalibrationEngine  Regime-Aware Signal Weight Engine (minute+ TFs only)
        builder.Services.AddSingleton<AutoCalibrationEngine>();
        builder.Services.AddSingleton<IAutoCalibrationEngine>(sp => sp.GetRequiredService<AutoCalibrationEngine>());
        
        builder.Services.AddSingleton<IConfluenceMatrixEngine>(sp => new ConfluenceMatrixEngine(
            sp.GetRequiredService<MarketDataFetcher>(),
            sp.GetRequiredService<IMarketAnalyzer>(),
            sp.GetRequiredService<IAutoCalibrationEngine>()));
        builder.Services.AddSingleton<TradeTimeoutEngine>();
        builder.Services.AddSingleton<ITradeTimeoutEngine>(sp => sp.GetRequiredService<TradeTimeoutEngine>());

        
        // Register CircuitBreakerService
        builder.Services.AddSingleton<ValutaBot.MiniApp.Services.ICircuitBreakerService>(sp =>
            new ValutaBot.MiniApp.Services.CircuitBreakerService(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TradingBotSettings>>(),
                () => ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection()
            ));

        // Register Orchestrator
        builder.Services.AddTransient<ValutaBot.MiniApp.Features.MarketAnalysis.IMarketAnalysisOrchestrator, ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator>();
        builder.Services.AddSingleton<ValutaBot.MiniApp.Features.ChaosAudit.IChaosAuditService, ValutaBot.MiniApp.Features.ChaosAudit.ChaosAuditService>();

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowMiniApp", p => p
                .SetIsOriginAllowed(origin => 
                {
                    if (string.IsNullOrEmpty(origin)) return false;
                    var host = new Uri(origin).Host.ToLowerInvariant();
                    return host == "web.telegram.org" || 
                           host.EndsWith("ngrok-free.dev") || 
                           host.EndsWith("ngrok.io") ||
                           host.EndsWith("railway.app") ||
                           host == "localhost" || 
                           host == "127.0.0.1";
                })
                .WithMethods("GET", "POST", "OPTIONS")
                .WithHeaders("X-Telegram-Init-Data", "Content-Type", "Accept"));
        });
        builder.Services.AddHostedService<TelegramBotService>();
        // FIX #6: Verification of pending trades
        builder.Services.AddHostedService<PendingTradeVerificationService>();
        builder.Services.AddHostedService<HistoricalCandleAccumulatorService>();
        builder.Services.AddHostedService<ValutaBot.MiniApp.Services.DataRetentionService>(); // Accumulates live m1 candles into historical_candles for weekend OTC proxy
        builder.Services.AddHostedService<ValutaBot.App.MiniApp.Services.SelfDiagnosticService>(); // Post-deploy self-scanner
        builder.Services.AddHostedService<ValutaBot.MiniApp.Services.AutoTradingScannerService>(); // Dual-engine architecture (fast subminute, slow m1)
        builder.Services.AddHostedService<ValutaBot.MiniApp.Services.TrialReminderBackgroundService>(); // 3-day trial reminders & funnel

        builder.Services.AddHttpClient("MLPythonService", client => 
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true
        }).AddStandardResilienceHandler(options =>
        {
            // Never retry timed-out live scalping predictions (retrying an s5 candle after 4-6s is stale and causes a retry storm)
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Min(4, Math.Max(3, botSettings.FastFailTimeoutSeconds)));
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.CircuitBreaker.SamplingDuration          = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.MinimumThroughput         = 5;
            options.CircuitBreaker.FailureRatio              = 0.7;
            options.CircuitBreaker.BreakDuration             = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddHttpClient("Telegram", client => 
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        builder.Services.AddHttpClient("MLPythonLongRunning", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(12);
        });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
                await context.HttpContext.Response.WriteAsync("{\"error\":\"Too many requests\"}");
            };

            options.AddPolicy("Global", context =>
            {
                System.Net.IPAddress? clientIp = ResolveClientIp(context);
                string partitionKey;
                if (clientIp == null)
                {
                    partitionKey = "unknown";
                }
                else if (clientIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !clientIp.IsIPv4MappedToIPv6)
                {
                    byte[] bytes = clientIp.GetAddressBytes();
                    Array.Clear(bytes, 8, 8); // /64 subnet mask
                    partitionKey = new System.Net.IPAddress(bytes).ToString();
                }
                else
                {
                    partitionKey = clientIp.ToString();
                }
                
                return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ =>
                    new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 120,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                        TokensPerPeriod = 60,
                        AutoReplenishment = true
                    });
            });

            // SRE-FIX: Dedicated strict rate limiting for /api/analyze to prevent CPU/ML inference DoS
            // Partitions primarily by Telegram userId, falling back to IP (prevents proxy evasion and CGNAT collision)
            options.AddPolicy("Analyze", context =>
            {
                string partitionKey = "unknown";

                if (context.Items.TryGetValue("userId", out object? uidObj) && uidObj is long uid && uid > 0)
                {
                    partitionKey = $"user_{uid}";
                }
                else
                {
                    string initHeader = context.Request.Headers["X-Telegram-Init-Data"].ToString();
                    if (!string.IsNullOrEmpty(initHeader))
                    {
                        if (initHeader.Contains("custom_user_id="))
                        {
                            var parsed = System.Web.HttpUtility.ParseQueryString(initHeader);
                            if (long.TryParse(parsed["custom_user_id"], out long cUid) && cUid > 0)
                                partitionKey = $"user_{cUid}";
                        }
                        else if (initHeader.Contains("user="))
                        {
                            var parsed = System.Web.HttpUtility.ParseQueryString(initHeader);
                            string userJson = parsed["user"] ?? "";
                            if (!string.IsNullOrEmpty(userJson))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(userJson);
                                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                                        partitionKey = $"user_{idProp.GetInt64()}";
                                }
                                catch { }
                            }
                        }
                    }

                    if (partitionKey == "unknown")
                    {
                        System.Net.IPAddress? clientIp = ResolveClientIp(context);
                        if (clientIp == null)
                        {
                            partitionKey = "unknown";
                        }
                        else if (clientIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !clientIp.IsIPv4MappedToIPv6)
                        {
                            byte[] bytes = clientIp.GetAddressBytes();
                            Array.Clear(bytes, 8, 8); // /64 subnet mask
                            partitionKey = new System.Net.IPAddress(bytes).ToString();
                        }
                        else
                        {
                            partitionKey = clientIp.ToString();
                        }
                    }
                }

                return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ =>
                    new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 20,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                        TokensPerPeriod = 5,
                        AutoReplenishment = true
                    });
            });
        });


        // Init Telegram notifier from config or env (set in Railway dashboard)
        TelegramNotifier.Init(builder.Configuration["TelegramBotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));

        // Init Neural Brain (PatchTST) Python ML microservice URL
        MLPythonService.Init(Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? builder.Configuration["MLService:BaseUrl"] ?? "http://localhost:8765");

        builder.Environment.WebRootPath = System.IO.Path.Combine(AppContext.BaseDirectory, "MiniApp", "wwwroot");
        var app = builder.Build();
        // Initialize Database, Tables & Core Engines before starting services
        try
        {
            await ValutaBot.App.MiniApp.Data.DbConnectionFactory.InitializeAsync();
        }
        catch (Exception dbInitEx)
        {
            BotLogger.Error("[Startup] Non-fatal DB initialization warning", dbInitEx);
        }

        // Launch Autonomous Pocket Option OTC stream (24/7 direct socket feed)
        PocketOptionWebSocketStream.StartStream();

        HttpFactory = app.Services.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        MLPythonService.SetFactory(HttpFactory);
        Services    = app.Services;

        var cbService = app.Services.GetRequiredService<ValutaBot.MiniApp.Services.ICircuitBreakerService>();
        await cbService.InitializeAsync();
        ValutaBot.MiniApp.TradeOutcomeTracker.CircuitBreaker = cbService;
        ValutaBot.MiniApp.TradeOutcomeTracker.AutoCalib = app.Services.GetRequiredService<ValutaBot.MiniApp.IAutoCalibrationEngine>();

        LatencyProbe.StartBackground(HttpFactory, app.Lifetime.ApplicationStopping);
        // SECURITY: Global HTTP Security Headers (prevent MIME-sniffing, XSS, Clickjacking, etc.)
        // Registered BEFORE UseStaticFiles to ensure static files (index.html, JS, CSS) receive security headers
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
            context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
            // Allow framing only from official Telegram Web clients (prevent Clickjacking)
            context.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'self' https://web.telegram.org https://*.web.telegram.org https://telegram.org https://*.telegram.org tg://*;");
            await next();
        });

        app.UseStaticFiles();
        app.UseCors("AllowMiniApp");

        // SECURITY: Metrics endpoint authentication with constant-time token verification for Prometheus / Grafana Cloud
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                string expectedToken = Environment.GetEnvironmentVariable("METRICS_BEARER_TOKEN")
                                    ?? Environment.GetEnvironmentVariable("INTERNAL_API_SECRET")
                                    ?? _internalApiSecretFallback;
                string authHeader = context.Request.Headers.Authorization.ToString();
                string providedToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authHeader["Bearer ".Length..].Trim()
                    : authHeader.Trim();

                if (!IsSecretValidConstantTime(providedToken, expectedToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.Append("WWW-Authenticate", "Bearer realm=\"Metrics\"");
                    await context.Response.WriteAsync("Unauthorized: Invalid or missing Metrics Bearer token.");
                    return;
                }
            }
            await next();
        });

        app.UseHttpMetrics();
        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        RegisterRoutes(app);

        // SRE-FIX: Gracefully close broker WebSocket, flush in-memory ticks and emergency trade outcomes before Kestrel stops
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                Console.WriteLine("[Shutdown] Application stopping: stopping PocketOption WebSocket stream...");
                PocketOptionWebSocketStream.StopStream();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Shutdown] Error stopping PocketOption stream: {ex.Message}");
            }

            try
            {
                Console.WriteLine("[TickCollector] Application stopping: performing final tick flush to DB...");
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                RealtimeTickCollector.StopAsync(shutdownCts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TickCollector] Application stopping flush error: {ex.Message}");
            }

            try
            {
                Console.WriteLine("[TradeRepository] Application stopping: draining emergency trade outcomes to DB...");
                ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.DrainEmergencyOutcomesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TradeRepository] Application stopping drain error: {ex.Message}");
            }

            try
            {
                Console.WriteLine("[MLPython] Application stopping: terminating Python child process...");
                MLPythonService.StopProcess();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MLPython] Application stopping error: {ex.Message}");
            }
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string dbStatus = "\u274C Ошибка";
                    try {
                        using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                        await conn.OpenAsync();
                        using var cmd = new Npgsql.NpgsqlCommand("SELECT 1", conn);
                        await cmd.ExecuteScalarAsync();
                        dbStatus = "\u2705 Подключено";
                    } catch { }

                    string mlStatus = "\u274C Ошибка (Таймаут)";
                    var client = HttpFactory?.CreateClient();
                    if (client != null) {
                        client.Timeout = TimeSpan.FromSeconds(5);
                        var mlBaseUrl = Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? builder.Configuration["MLService:BaseUrl"] ?? "http://localhost:8765";
                        string url = $"{mlBaseUrl.TrimEnd('/')}/health";
                        
                        for (int i = 0; i < 20; i++)
                        {
                            try {
                                var mlResp = await client.GetAsync(url);
                                if (mlResp.IsSuccessStatusCode) {
                                    mlStatus = "\u2705 Подключено";
                                    break;
                                }
                            } catch { }
                            await Task.Delay(1000);
                        }
                    }

                    string wsStatus = "\u274C Ожидание подключения";
                    if (PriceFeed.UsePocketOption)
                    {
                        for (int t = 0; t < 8; t++)
                        {
                            if (PocketOptionWebSocketStream.IsConnected)
                            {
                                wsStatus = "\u2705 Pocket Option OTC (24/7)";
                                break;
                            }
                            await Task.Delay(1000);
                        }
                        if (!PocketOptionWebSocketStream.IsConnected)
                            wsStatus = "\u23F3 Подключение к Pocket Option OTC...";
                    }


                    string msg = "\U0001F680 <b>ValutaBot Успешно Запущен!</b>\n\n\u2699\uFE0F <b>Системная диагностика:</b>\n- C# Датчики: \u2705 ОК\n- PostgreSQL: " + dbStatus + "\n- Нейросеть (PatchTST): " + mlStatus + "\n- WebSocket: " + wsStatus + "\n\n<i>Бот полностью в сети и мониторит рынок.</i>";
                    await ValutaBot.MiniApp.TelegramBotService.SendMessageToAdmins(msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Startup Admin Notification Error: " + ex.Message);
                }
            });
        });

        await app.RunAsync($"http://0.0.0.0:{port}");
    }

    private static Task<object> GetFearGreedIndex()
    {
        return Task.FromResult<object>(new { value = 50, classification = "Neutral" });
    }
}
