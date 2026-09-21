using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.Core;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Services;

/// <summary>
/// Background service that continuously scans the backend for issues after deployment.
/// Acts as a self-healing and alert monitor for Railway deployments.
/// </summary>
public class SelfDiagnosticService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    
    // State tracking to prevent notification spam
    private bool _dbWasHealthy = true;
    private bool _mlWasHealthy = true;
    private bool _memWasHealthy = true;

    public SelfDiagnosticService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[Diagnostics] Self-scanning diagnostics service started.");
        
        // Wait 30 seconds before first scan to allow all systems to initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDiagnosticScanAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                BotLogger.Error("[Diagnostics] Error running diagnostic scan", ex);
            }

            // Run scan every 5 minutes
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task RunDiagnosticScanAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var httpFactory = scope.ServiceProvider.GetService<IHttpClientFactory>();
        
        bool currentDbHealthy = false;
        bool currentMlHealthy = false;
        
        // 1. Check Database Health
        try
        {
            using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
            await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int>(conn, "SELECT 1");
            currentDbHealthy = true;
            
            if (!_dbWasHealthy)
            {
                _dbWasHealthy = true;
                await TelegramBotService.SendMessageToAdmins("✅ <b>Database Connection Restored</b>\nBackend DB is operating normally again.");
            }
        }
        catch (Exception ex)
        {
            if (_dbWasHealthy)
            {
                _dbWasHealthy = false;
                await TelegramBotService.SendMessageToAdmins($"🚨 <b>Database Connection Lost</b>\nBackend self-scan failed to reach DB:\n<code>{ex.Message}</code>");
            }
        }

        // 2. Check ML Python Service Health
        try
        {
            var mlUrl = _configuration["MLService:BaseUrl"] ?? Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? "http://localhost:8765";
            if (httpFactory != null)
            {
                var hc = httpFactory.CreateClient();
                hc.Timeout = TimeSpan.FromSeconds(5);
                var mlResponse = await hc.GetAsync(new Uri($"{mlUrl.TrimEnd('/')}/health"), stoppingToken);
                
                if (mlResponse.IsSuccessStatusCode)
                {
                    currentMlHealthy = true;
                    if (!_mlWasHealthy)
                    {
                        _mlWasHealthy = true;
                        await TelegramBotService.SendMessageToAdmins("✅ <b>Нейросеть снова в строю</b>\nСвязь с сервером машинного обучения успешно восстановлена.");
                    }
                }
                else
                {
                    throw new Exception($"Status Code: {mlResponse.StatusCode}");
                }
            }
        }
        catch (Exception ex)
        {
            if (_mlWasHealthy)
            {
                _mlWasHealthy = false;
                await TelegramBotService.SendMessageToAdmins($"🚨 <b>Нейросеть временно недоступна</b>\nВключен резервный алгоритм базовых индикаторов (TA+SMC).\nОшибка: <code>{ex.Message}</code>");
            }
        }

        // 3. Check UI Frontend Health (Synthetic local request)
        try
        {
            if (httpFactory != null)
            {
                var hc = httpFactory.CreateClient();
                hc.Timeout = TimeSpan.FromSeconds(3);
                // Note: The app runs on port configured by environment, assume internal port is available
                // We'll check the local static files indirectly or via the web server loopback
                var port = _configuration["PORT"] ?? "5000";
                var uiResponse = await hc.GetAsync($"http://localhost:{port}/");
                
                if (uiResponse.IsSuccessStatusCode)
                {
                    var html = await uiResponse.Content.ReadAsStringAsync();
                    if (html.Contains("<!DOCTYPE html>"))
                    {
                        // Frontend is being served properly
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[Diagnostics] Synthetic UI check failed (might be normal depending on port config): {ex.Message}");
        }

        // 4. Resource Scanning (RAM)
        try
        {
            using var process = Process.GetCurrentProcess();
            // Memory in MB
            long memoryUsedMb = process.PrivateMemorySize64 / (1024 * 1024);
            
            // If memory exceeds 750MB, flag it
            bool currentMemHealthy = memoryUsedMb < 750;
            
            if (!currentMemHealthy && _memWasHealthy)
            {
                _memWasHealthy = false;
                await TelegramBotService.SendMessageToAdmins($"⚠️ <b>High Memory Usage Detected</b>\nValutaBot backend is consuming {memoryUsedMb} MB of RAM. Consider restarting if it grows past 1GB.");
            }
            else if (currentMemHealthy && !_memWasHealthy && memoryUsedMb < 500)
            {
                // Reset flag when memory drops back down below a safer threshold
                _memWasHealthy = true;
            }
        }
        catch
        {
            // Ignore if OS doesn't support reading memory counters gracefully
        }
    }
}
