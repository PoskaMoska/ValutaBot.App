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
using ValutaBot.App.MiniApp.Models;

namespace ValutaBot.MiniApp;

public static partial class MiniAppController
{
    private static readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public static string? LastExceptionMessage { get; set; }

    public record OhlcCandle(double Open, double High, double Low, double Close, double Volume, DateTime Timestamp = default);
    public record NotifyAdminsRequest(string Message, string ParseMode = "HTML");

    public static System.Net.Http.IHttpClientFactory? HttpFactory { get; set; }
    public static IServiceProvider? Services { get; set; }

    public static void Start(string[] args, int port = 5000)
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
        
        var botSettings = builder.Configuration.GetSection("TradingBotSettings").Get<TradingBotSettings>() ?? new TradingBotSettings();
        builder.Services.Configure<TradingBotSettings>(builder.Configuration.GetSection("TradingBotSettings"));

        // Register Engines and Services in DI
        builder.Services.AddSingleton<MarketDataFetcher>();
        builder.Services.AddSingleton<TechnicalAnalysisEngine>();
        builder.Services.AddSingleton<ITechnicalAnalysisEngine>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        builder.Services.AddSingleton<IMathEngine>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        builder.Services.AddSingleton<IMarketAnalyzer>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        builder.Services.AddSingleton<IRiskGatekeeper>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        // AutoCalibrationEngine — Regime-Aware Signal Weight Engine (minute+ TFs only)
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

        builder.Services.AddHttpClient("TwelveData").AddStandardResilienceHandler();
        builder.Services.AddHttpClient("FNG").AddStandardResilienceHandler();
        builder.Services.AddHttpClient("MLPythonService", client => 
        {
        }).AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(botSettings.FastFailTimeoutSeconds);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(botSettings.FastFailTimeoutSeconds + 1);
            options.CircuitBreaker.SamplingDuration          = TimeSpan.FromSeconds(15);
            options.CircuitBreaker.MinimumThroughput         = 3;
            options.CircuitBreaker.FailureRatio              = 0.5;
            options.CircuitBreaker.BreakDuration             = TimeSpan.FromSeconds(30);
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
                string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";
                string initData = context.Request.Headers["X-Telegram-Init-Data"].ToString();
                string fingerprint = $"{ip}|{initData}";
                
                return RateLimitPartition.GetTokenBucketLimiter(fingerprint, _ =>
                    new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 10000,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(2),
                        TokensPerPeriod = 10000,
                        AutoReplenishment = true
                    });
            });
        });

        // Launch Real-Time WebSocket stream for major CME proxy forex streams (0ms latency)
        // Added USDCAD, USDCHF, USDJPY to ensure all 6 active ML pairs are accumulated in the DB
        // FIX: Removed 'isWeekend' check so the stream always starts. If booted on a weekend, 
        // it simply idles until Monday morning when ticks resume.
        string[] topStreamSymbols = { "EUR/USD", "GBP/USD", "AUD/USD", "USD/CAD", "USD/CHF", "USD/JPY" };
        TwelveDataWebSocketStream.StartStream(topStreamSymbols);

        // Init Telegram notifier from config or env (set in Railway dashboard)
        TelegramNotifier.Init(builder.Configuration["TelegramBotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));

        // Init LightGBM Python ML microservice URL
        MLPythonService.Init(builder.Configuration["MLService:BaseUrl"] ?? Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? "http://localhost:8765");

        builder.Environment.WebRootPath = System.IO.Path.Combine(AppContext.BaseDirectory, "MiniApp", "wwwroot");
        var app = builder.Build();

        HttpFactory = app.Services.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        MLPythonService.SetFactory(HttpFactory);
        Services    = app.Services;

        // Initialize CircuitBreaker (Creates DB Table)
        var cbService = app.Services.GetRequiredService<ValutaBot.MiniApp.Services.ICircuitBreakerService>();
        cbService.InitializeAsync().GetAwaiter().GetResult();

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

        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        RegisterRoutes(app);

        app.Run($"http://0.0.0.0:{port}");
    }

    private static async Task<object> GetFearGreedIndex()
    {
        const string cacheKey = "fear_greed";
        if (_cache.TryGetValue(cacheKey, out object? cached))
            return cached!;

        try
        {
            var json = await HttpFactory!.CreateClient("FNG").GetStringAsync("https://api.alternative.me/fng/?limit=1");
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data")[0];
            var result = new
            {
                value = int.TryParse(data.GetProperty("value").GetString(), out var v) ? v : 50,
                classification = data.GetProperty("value_classification").GetString() ?? "Neutral"
            };
            _cache.Set(cacheKey, (object)result, TimeSpan.FromHours(1));
            return result;
        }
        catch
        {
            return new { value = 50, classification = "Neutral" };
        }
    }
}

