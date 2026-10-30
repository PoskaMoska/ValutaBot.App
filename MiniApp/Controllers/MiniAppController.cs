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

    private static readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public static string? LastExceptionMessage { get; set; }
    private static readonly string _internalApiSecretFallback = Guid.NewGuid().ToString();

    public record OhlcCandle(double Open, double High, double Low, double Close, double Volume, DateTime Timestamp = default);
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

                // Filter out Polly resilience pipeline events (timeouts, retries) from triggering Sentry alarms.
                // These are normal resilience operations gracefully handled by fallback indicators (TA+SMC).
                options.AddExceptionFilterForType<Polly.Timeout.TimeoutRejectedException>();
                options.SetBeforeSend((sentryEvent, hint) =>
                {
                    if (sentryEvent.Logger != null && (sentryEvent.Logger.StartsWith("Polly") || sentryEvent.Logger.StartsWith("Microsoft.Extensions.Http.Resilience")))
                    {
                        return null; // Drop internal resilience logs
                    }
                    if (sentryEvent.Exception is Polly.Timeout.TimeoutRejectedException)
                    {
                        return null;
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
            options.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);

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
                System.Net.IPAddress? clientIp = context.Connection.RemoteIpAddress;
                if (clientIp == null || System.Net.IPAddress.IsLoopback(clientIp))
                {
                    string rawHeader = context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "";
                    string firstHop = rawHeader.Split(',')[0].Trim();
                    if (System.Net.IPAddress.TryParse(firstHop, out var parsedForwarded))
                    {
                        clientIp = parsedForwarded;
                    }
                }
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
        });


        // Init Telegram notifier from config or env (set in Railway dashboard)
        TelegramNotifier.Init(builder.Configuration["TelegramBotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));

        // Init Neural Brain (PatchTST) Python ML microservice URL
        MLPythonService.Init(Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? builder.Configuration["MLService:BaseUrl"] ?? "http://localhost:8765");

        builder.Environment.WebRootPath = System.IO.Path.Combine(AppContext.BaseDirectory, "MiniApp", "wwwroot");
        var app = builder.Build();
        // Initialize Database, Tables & Core Engines before starting services
        await ValutaBot.App.MiniApp.Data.DbConnectionFactory.InitializeAsync();

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
        app.UseStaticFiles();
        app.UseCors("AllowMiniApp");
        
        // SECURITY: Global HTTP Security Headers (prevent MIME-sniffing, XSS, etc.)
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
            context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
            // Allow framing only from Telegram (to allow WebApp to work inside Telegram UI)
            context.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'self' https://web.telegram.org tg://*");
            await next();
        });

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

        // SRE-FIX: Gracefully close broker WebSocket and flush in-memory ticks before Kestrel stops
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
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                RealtimeTickCollector.StopAsync(shutdownCts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TickCollector] Application stopping flush error: {ex.Message}");
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
