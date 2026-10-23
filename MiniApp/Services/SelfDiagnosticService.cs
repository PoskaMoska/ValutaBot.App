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
    private int _consecutiveDbFailures = 0;
    private bool _mlWasHealthy = true;
    private int _consecutiveMlFailures = 0;
    private const int MaxConsecutiveFailures = 3; // Require 3 consecutive failures before alerting Telegram
    private bool _patchTstWasHealthy = true;
    private bool _feedWasHealthy = true;
    private bool _memWasHealthy = true;
    private DateTime _lastV3CheckTime = DateTime.MinValue;
    private DateTime _lastDatasetAuditTime = DateTime.MinValue;
    private DateTime _lastDatasetTelegramDigestDate = DateTime.MinValue;
    private DateTime _lastChaosAuditTime = DateTime.MinValue;

    private readonly string _mlStateFilePath = "ml_service/data/ml_health_state.txt";

    public SelfDiagnosticService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;

        // Clean slate on startup: do not inherit stale crash state from previous deployment
        try 
        {
            if (System.IO.File.Exists(_mlStateFilePath))
            {
                System.IO.File.Delete(_mlStateFilePath);
            }
        }
        catch { }
        _mlWasHealthy = true;
        _patchTstWasHealthy = true;
        _feedWasHealthy = true;
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

            // Responsive scan: every 1 minute normally, or every 15 seconds if recovering from an incident
            var scanInterval = (!_mlWasHealthy || !_dbWasHealthy)
                ? TimeSpan.FromSeconds(15)
                : TimeSpan.FromMinutes(1);
            await Task.Delay(scanInterval, stoppingToken);
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
            _consecutiveDbFailures = 0;
            
            if (!_dbWasHealthy)
            {
                _dbWasHealthy = true;
                await TelegramBotService.SendMessageToAdmins("✅ <b>Database Connection Restored</b>\nBackend DB is operating normally again.");
            }
        }
        catch (Exception ex)
        {
            _consecutiveDbFailures++;
            BotLogger.Warn($"[Diagnostics] DB check failed ({_consecutiveDbFailures}/{MaxConsecutiveFailures}): {ex.Message}");
            if (_consecutiveDbFailures >= MaxConsecutiveFailures && _dbWasHealthy)
            {
                _dbWasHealthy = false;
                await TelegramBotService.SendMessageToAdmins($"🚨 <b>Database Connection Lost</b>\nBackend self-scan failed to reach DB:\n<code>{ex.Message}</code>");
            }
        }

        // 2. Check ML Python Service Health
        try
        {
            var mlUrl = Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? _configuration["MLService:BaseUrl"] ?? "http://localhost:8765";
            if (httpFactory != null)
            {
                var hc = httpFactory.CreateClient();
                hc.Timeout = TimeSpan.FromSeconds(15);
                var mlResponse = await hc.GetAsync(new Uri($"{mlUrl.TrimEnd('/')}/health"), stoppingToken);
                
                if (mlResponse.IsSuccessStatusCode)
                {
                    currentMlHealthy = true;
                    _consecutiveMlFailures = 0;
                    if (!_mlWasHealthy)
                    {
                        _mlWasHealthy = true;
                        try { System.IO.File.WriteAllText(_mlStateFilePath, "true"); } catch { }
                        await TelegramBotService.SendMessageToAdmins("✅ <b>Нейросеть снова в строю</b>\nСвязь с сервером машинного обучения успешно восстановлена.");
                    }

                    // Deep Neural Brain Check
                    try
                    {
                        var content = await mlResponse.Content.ReadAsStringAsync(stoppingToken);
                        using var doc = System.Text.Json.JsonDocument.Parse(content);
                        bool patchAvailable = doc.RootElement.TryGetProperty("patch_tst_available", out var ptProp) && ptProp.GetBoolean();
                        
                        if (!patchAvailable && _patchTstWasHealthy)
                        {
                            _patchTstWasHealthy = false;
                            await TelegramBotService.SendMessageToAdmins("⚠️ <b>PatchTST Neural Brain не активен</b>\nМодель глубокого обучения недоступна, инференс переключен на резервный контур C# Confluence Matrix.");
                        }
                        else if (patchAvailable && !_patchTstWasHealthy)
                        {
                            _patchTstWasHealthy = true;
                            await TelegramBotService.SendMessageToAdmins("✅ <b>PatchTST Neural Brain активен</b>\nSOTA трансформер успешно загружен и обрабатывает запросы.");
                        }
                    }
                    catch (Exception exDeep)
                    {
                        BotLogger.Warn($"[Diagnostics] Deep ML parsing error: {exDeep.Message}");
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
            _consecutiveMlFailures++;
            BotLogger.Warn($"[Diagnostics] ML check failed ({_consecutiveMlFailures}/{MaxConsecutiveFailures}): {ex.Message}");
            if (_consecutiveMlFailures >= MaxConsecutiveFailures && _mlWasHealthy)
            {
                _mlWasHealthy = false;
                try { System.IO.File.WriteAllText(_mlStateFilePath, "false"); } catch { }
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
        
        // 5. Market Feed Liveness Check (subminute_candles not stalled)
        try
        {
            // Pocket Option OTC operates 24/7; legacy Forex closes Fri 21:00 UTC to Sun 21:00 UTC
            bool isFeedPaused = !PriceFeed.UsePocketOption && (
                (DateTime.UtcNow.DayOfWeek == DayOfWeek.Saturday) ||
                (DateTime.UtcNow.DayOfWeek == DayOfWeek.Sunday && DateTime.UtcNow.Hour < 21) ||
                (DateTime.UtcNow.DayOfWeek == DayOfWeek.Friday && DateTime.UtcNow.Hour >= 21));

            if (!isFeedPaused)
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                string feedQuery = PriceFeed.UsePocketOption
                    ? "SELECT MAX(open_time) FROM subminute_candles WHERE asset IN ('EURUSD_PO', 'GBPUSD_PO', 'AUDUSD_PO', 'USDCAD_PO', 'USDJPY_PO', 'USDCHF_PO') AND interval = 's5'"
                    : "SELECT MAX(open_time) FROM subminute_candles WHERE interval = 's5'";
                var latestCandleStr = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<string>(conn, feedQuery);
                
                if (DateTime.TryParse(latestCandleStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var latestCandleUtc))
                {
                    var stallDuration = DateTime.UtcNow - latestCandleUtc;
                    if (stallDuration > TimeSpan.FromMinutes(2))
                    {
                        if (PriceFeed.UsePocketOption)
                        {
                            PocketOptionWebSocketStream.ForceReconnect($"subminute_candles stalled for {(int)stallDuration.TotalSeconds}s");
                        }

                        if (_feedWasHealthy)
                        {
                            _feedWasHealthy = false;
                            await TelegramBotService.SendMessageToAdmins($"⚠️ <b>Задержка котировок рынка</b>\nСвежие свечи в subminute_candles отстают на {(int)stallDuration.TotalSeconds}с. Автоматически запущен принудительный реконнект Pocket Option OTC WebSocket.");
                        }
                    }
                    else if (!_feedWasHealthy)
                    {
                        _feedWasHealthy = true;
                        await TelegramBotService.SendMessageToAdmins("✅ <b>Поток котировок Pocket Option OTC восстановлен</b>\nСвечи поступают в базу в реальном времени.");
                    }
                }
            }
        }
        catch (Exception exFeed)
        {
            BotLogger.Warn($"[Diagnostics] Market feed liveness check warning: {exFeed.Message}");
        }

        // 6. V3 Dataset Accumulator Logging
        if (DateTime.UtcNow - _lastV3CheckTime >= TimeSpan.FromMinutes(15))
        {
            _lastV3CheckTime = DateTime.UtcNow;
            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                var poV3Count = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int>(conn,
                    "SELECT COUNT(*) FROM trade_outcomes WHERE features_json IS NOT NULL AND created_at >= '2026-10-15 21:55:00+00'::timestamptz");
                BotLogger.Info($"[Diagnostics] V3 PO-Era Dataset Progress: {poV3Count} clean Pocket Option OTC records accumulated with 160-candle sequences & macro context.");
            }
            catch (Exception exV3)
            {
                BotLogger.Warn($"[Diagnostics] V3 count check warning: {exV3.Message}");
            }
        }

        // 7. Autonomous Dataset & Pipeline Sanity Audit (every 6 hours, plus daily Telegram digest)
        if (currentMlHealthy && httpFactory != null && (DateTime.UtcNow - _lastDatasetAuditTime >= TimeSpan.FromHours(6)))
        {
            _lastDatasetAuditTime = DateTime.UtcNow;
            try
            {
                var mlUrl = Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? _configuration["MLService:BaseUrl"] ?? "http://localhost:8765";
                var hc = httpFactory.CreateClient();
                hc.Timeout = TimeSpan.FromSeconds(30);
                var auditResp = await hc.GetAsync(new Uri($"{mlUrl.TrimEnd('/')}/diagnostics/audit?notify=false"), stoppingToken);
                if (auditResp.IsSuccessStatusCode)
                {
                    var auditJson = await auditResp.Content.ReadAsStringAsync(stoppingToken);
                    using var doc = System.Text.Json.JsonDocument.Parse(auditJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var st) && st.GetString() == "ok")
                    {
                        int totalChecked = root.GetProperty("total_checked").GetInt32();
                        int corrupted = root.GetProperty("corrupted_json").GetInt32();
                        int shortWin = root.GetProperty("short_candle_windows").GetInt32();
                        int gappedWin = root.GetProperty("gapped_candle_windows").GetInt32();
                        int flatWin = root.GetProperty("flat_frozen_windows").GetInt32();
                        double zeroVolPct = root.GetProperty("zero_vol_candle_pct").GetDouble();
                        double zeroMfePct = root.GetProperty("zero_mfe_loss_pct").GetDouble();

                        int deadSensorsCount = root.TryGetProperty("dead_sensors", out var dsArr) && dsArr.ValueKind == System.Text.Json.JsonValueKind.Array ? dsArr.GetArrayLength() : 0;
                        bool hasCriticalAnomaly = corrupted > 0 || deadSensorsCount > 0 || flatWin > 20 || shortWin > 10;
                        bool isDailyDigestDue = DateTime.UtcNow.Date > _lastDatasetTelegramDigestDate && DateTime.UtcNow.Hour >= 9;

                        if (hasCriticalAnomaly || isDailyDigestDue)
                        {
                            _lastDatasetTelegramDigestDate = DateTime.UtcNow.Date;
                            string icon = hasCriticalAnomaly ? "🟡" : "🟢";
                            string msg = $"{icon} <b>Авто-Аудит чистоты датасета (V3 Sanity Check)</b>\n" +
                                         $"Проверено последних слепков: <b>{totalChecked}</b>\n" +
                                         $"• Битый JSON / Короткие окна: <b>{corrupted} / {shortWin}</b>\n" +
                                         $"• Разрывы внутри 160 свечей: <b>{gappedWin}</b> ({(totalChecked > 0 ? gappedWin * 100.0 / totalChecked : 0):F1}%)\n" +
                                         $"• Замороженные свечи (>50% flat): <b>{flatWin}</b>\n" +
                                         $"• Нулевой тиковый объём: <b>{zeroVolPct:F2}%</b>\n" +
                                         $"• Убытки с MFE=0 (сразу против входа): <b>{zeroMfePct:F1}%</b>\n" +
                                         $"• Залипшие датчики MacroContext: <b>{(deadSensorsCount == 0 ? "Нет (все активны)" : deadSensorsCount.ToString())}</b>";
                            await TelegramBotService.SendMessageToAdmins(msg);
                        }
                    }
                }
            }
            catch (Exception exAudit)
            {
                BotLogger.Warn($"[Diagnostics] Autonomous dataset audit notice: {exAudit.Message}");
            }
        }
        
        // 8. Continuous Autonomous In-Process Saboteur (Micro-Assault every 3 minutes)
        if (DateTime.UtcNow - _lastChaosAuditTime >= TimeSpan.FromMinutes(3))
        {
            _lastChaosAuditTime = DateTime.UtcNow;
            try
            {
                var chaosService = scope.ServiceProvider.GetService<ValutaBot.MiniApp.Features.ChaosAudit.IChaosAuditService>();
                if (chaosService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await chaosService.RunSingleSabotageRoundAsync(stoppingToken);
                        }
                        catch (Exception exChaos)
                        {
                            BotLogger.Warn($"[Saboteur] Background assault error: {exChaos.Message}");
                        }
                    }, stoppingToken);
                }
            }
            catch (Exception exChaos)
            {
                BotLogger.Warn($"[Saboteur] Micro-assault trigger notice: {exChaos.Message}");
            }
        }

        if (!currentDbHealthy || !currentMlHealthy)
        {
            BotLogger.Warn($"[Diagnostics] Health scan complete. DB: {currentDbHealthy}, ML: {currentMlHealthy}");
        }
    }
}
