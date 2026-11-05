using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using File = System.IO.File;

namespace ValutaBot.MiniApp;

public partial class TelegramBotService : BackgroundService
{
    private static string _baseUrl = "https://api.telegram.org";
    internal static string GetBaseUrl() => _baseUrl;
    internal static void SetBaseUrl(string url) => _baseUrl = url?.TrimEnd('/') ?? _baseUrl;

    internal static readonly HttpClient _httpClient = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    }) { Timeout = TimeSpan.FromSeconds(35) };

    private static readonly IMemoryCache UserLastActivity = new MemoryCache(new MemoryCacheOptions());
    private static string _webAppUrl = "https://chowder-dreamland-spotlight.ngrok-free.dev";

    private static long _tgBlockedUntilTicks = 0;
    private static readonly ConcurrentDictionary<long, long> _lastOutboundTicksPerChat = new();
    private static long _lastGlobalSendTick = 0;
    private static readonly object _globalThrottleLock = new();

    private static long _lastSuccessfulPollUtcTicks = 0;
    private static volatile bool _isLeader = false;
    public static bool IsLeader => _isLeader;
    public static bool IsPollerHealthy => (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSuccessfulPollUtcTicks)) < TimeSpan.FromSeconds(90).Ticks;

    /// <summary>
    /// Evicts stale per-chat throttling timestamps older than 5 minutes to prevent memory leaks.
    /// </summary>
    public static void PruneOutboundThrottleCache()
    {
        long cutoffTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;
        foreach (var kvp in _lastOutboundTicksPerChat)
        {
            if (kvp.Value < cutoffTicks)
            {
                _lastOutboundTicksPerChat.TryRemove(kvp.Key, out _);
            }
        }
    }

    internal static async Task WaitTgGateAsync(CancellationToken ct = default)
    {
        long blockedUntil = Interlocked.Read(ref _tgBlockedUntilTicks);
        if (blockedUntil > 0)
        {
            var remaining = new DateTime(blockedUntil, DateTimeKind.Utc) - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, ct);
            }
        }
    }

    internal static async Task ThrottleOutboundAsync(long chatId, CancellationToken ct = default)
    {
        // 1. Reactive 429 circuit-breaker wait
        await WaitTgGateAsync(ct);

        // 2. Global rate throttle: enforce <= 25 msg/s globally (minimum 40ms interval between outbound HTTP requests)
        long globalDelayMs = 0;
        lock (_globalThrottleLock)
        {
            long now = DateTime.UtcNow.Ticks;
            long nextAllowed = Math.Max(now, _lastGlobalSendTick + TimeSpan.FromMilliseconds(40).Ticks);
            if (nextAllowed > now)
            {
                globalDelayMs = (long)TimeSpan.FromTicks(nextAllowed - now).TotalMilliseconds;
            }
            _lastGlobalSendTick = nextAllowed;
        }
        if (globalDelayMs > 0)
        {
            await Task.Delay((int)globalDelayMs, ct);
        }

        // 3. Per-chat rate limit: enforce at most 1 msg/s per specific chat (1050ms interval)
        if (chatId != 0)
        {
            while (true)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = _lastOutboundTicksPerChat.GetOrAdd(chatId, 0);
                long elapsedTicks = nowTicks - lastTicks;
                long minIntervalTicks = TimeSpan.FromMilliseconds(1050).Ticks;

                if (elapsedTicks < minIntervalTicks)
                {
                    int delayMs = (int)TimeSpan.FromTicks(minIntervalTicks - elapsedTicks).TotalMilliseconds;
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct);
                        continue;
                    }
                }

                if (_lastOutboundTicksPerChat.TryUpdate(chatId, DateTime.UtcNow.Ticks, lastTicks))
                {
                    break;
                }
            }
        }
    }

    internal static void RegisterTgBlock(int seconds)
    {
        int clampedSec = Math.Clamp(seconds, 1, 3600);
        long newBlockUntil = DateTime.UtcNow.AddSeconds(clampedSec).Ticks;
        while (true)
        {
            long current = Interlocked.Read(ref _tgBlockedUntilTicks);
            if (newBlockUntil <= current) break;
            if (Interlocked.CompareExchange(ref _tgBlockedUntilTicks, newBlockUntil, current) == current)
            {
                BotLogger.Warn($"[TG Bot API] Centralized rate-limit gate engaged for {clampedSec}s across all Telegram bot workers.");
                break;
            }
        }
    }

    public static void InvalidateUserCache(long chatId)
    {
        UserLastActivity.Remove($"allowed_{chatId}");
        BotLogger.Info($"[Security] Invalidated access cache for chatId: {chatId}");
    }

    public static async Task<bool> IsUserAllowed(long chatId)
    {
        string cacheKey = $"allowed_{chatId}";
        if (UserLastActivity.TryGetValue(cacheKey, out bool cachedAllowed) && cachedAllowed)
        {
            return true;
        }

        bool isAllowed = false;
        if (await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsAdminAsync(chatId))
            isAllowed = true;
        else if (await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsUserAllowedAsync(chatId))
            isAllowed = true;
        else
        {
            var reg = await ValutaBot.App.MiniApp.Data.Repositories.RegistrationRepository.GetRegistrationByChatIdAsync(chatId);
            if (reg != null && reg.HasRegistered)
            {
                if (reg.HasDeposited)
                {
                    await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAllowedUserAsync(chatId);
                    isAllowed = true;
                }
                else if (reg.TrialEndsAt.HasValue && reg.TrialEndsAt.Value > DateTime.UtcNow)
                {
                    isAllowed = true;
                }
            }
        }

        if (isAllowed)
        {
            UserLastActivity.Set(cacheKey, true, TimeSpan.FromSeconds(60));
        }

        return isAllowed;
    }

    public static async Task SendMessageToAdmins(string text)
    {
        string? token = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(text)) return;

        string payloadText = text.Length > 3500 ? text[..3500] + "…" : text;
        var adminIds = new System.Collections.Generic.HashSet<long>();

        try
        {
            foreach (var id in await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.GetAdminChatIdsAsync())
                adminIds.Add(id);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[Admins] DB admin lookup failed, falling back to env: {ex.GetType().Name}");
        }

        string envAdmin = Environment.GetEnvironmentVariable("ADMIN_CHAT_ID") ?? Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "";
        foreach (var part in envAdmin.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out long adminId))
                adminIds.Add(adminId);
        }

        foreach (var adminId in adminIds)
        {
            try
            {
                await SendMessage(token, adminId, payloadText);
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[Admins] Failed to deliver alert to {adminId}: {ex.GetType().Name}");
            }
            finally
            {
                await Task.Delay(40); // 40ms throttle (<= 25 msg/s, complies with Telegram 30 msg/s limit)
            }
        }
    }

    internal enum UserState
    {
        None,
        AwaitingId,
        AwaitingDeleteId,
        AwaitingAddAdminId
    }

    internal static readonly ConcurrentDictionary<long, UserState> UserStates = new();
    internal static readonly ConcurrentDictionary<long, string> UserSubmittedIds = new();
    private static readonly ConcurrentDictionary<long, DateTime> _userStateTimestamps = new();

    internal static void SetUserState(long chatId, UserState state)
    {
        UserStates[chatId] = state;
        _userStateTimestamps[chatId] = DateTime.UtcNow;
    }

    public static bool TryClearUserState(long chatId)
    {
        _userStateTimestamps.TryRemove(chatId, out _);
        UserSubmittedIds.TryRemove(chatId, out _);
        return UserStates.TryRemove(chatId, out _);
    }

    /// <summary>
    /// Evicts expired FSM user states (default 15 minutes TTL) to prevent memory leaks and zombie sessions.
    /// </summary>
    public static void PruneExpiredUserStates(TimeSpan? ttl = null)
    {
        var stateTtl = ttl ?? TimeSpan.FromMinutes(15);
        var cutoff = DateTime.UtcNow - stateTtl;
        foreach (var kvp in _userStateTimestamps)
        {
            if (kvp.Value < cutoff)
            {
                UserStates.TryRemove(kvp.Key, out _);
                UserSubmittedIds.TryRemove(kvp.Key, out _);
                _userStateTimestamps.TryRemove(kvp.Key, out _);
                BotLogger.Info($"[TG Bot FSM] Evicted expired user state for chatId {kvp.Key}");
            }
        }
    }

    private static async Task<long> LoadPersistedOffsetAsync(CancellationToken ct = default)
    {
        try
        {
            string? raw = await ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetBotStateAsync("telegram_update_offset", ct);
            if (long.TryParse(raw, out long dbOffset) && dbOffset > 0)
            {
                BotLogger.Info($"[TG Bot] Loaded persistent Telegram offset from database: {dbOffset}");
                return dbOffset;
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[TG Bot] Failed to load persistent offset from database: {ex.Message}");
        }
        return 0;
    }

    private static async Task SavePersistedOffsetAsync(long offset, CancellationToken ct = default)
    {
        if (offset <= 0) return;
        try
        {
            await ValutaBot.App.MiniApp.Data.DbConnectionFactory.SetBotStateAsync("telegram_update_offset", offset.ToString(), ct);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[TG Bot] Failed to persist offset {offset} to database: {ex.Message}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? token = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("[TG Bot] Telegram Bot Token is not set. Bot service will not run.");
            return;
        }

        try {
            var config = MiniAppController.Services?.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) as Microsoft.Extensions.Configuration.IConfiguration;
            _webAppUrl = config?["WebAppUrl"] ?? Environment.GetEnvironmentVariable("WEBAPP_URL") ?? "https://chowder-dreamland-spotlight.ngrok-free.dev";
        } catch {
            _webAppUrl = Environment.GetEnvironmentVariable("WEBAPP_URL") ?? "https://chowder-dreamland-spotlight.ngrok-free.dev";
        }

        Console.WriteLine($"[TG Bot] Service initialized. WebApp URL: {_webAppUrl}");

        try { await ValutaBot.App.MiniApp.Data.DbConnectionFactory.InitializeAsync(); } catch (Exception dbEx) { BotLogger.Warn($"[TG Bot] Startup DB init warning: {dbEx.Message}"); }

        // Auto-seed admin IDs from env ADMIN_CHAT_ID / ADMIN_IDS
        string envAdmin = Environment.GetEnvironmentVariable("ADMIN_CHAT_ID") ?? Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "";
        foreach (var part in envAdmin.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out long parsedEnvAdmin))
            {
                try { await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAdminAsync(parsedEnvAdmin); } catch { }
            }
        }

        // SRE-FIX: Single-Instance Distributed Lock during Kubernetes rolling update
        await using var leaderLock = new ValutaBot.MiniApp.Services.PostgresDistributedLock("valutabot_telegram_leader");
        await leaderLock.WaitAcquireAsync(TimeSpan.FromSeconds(2), stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        _isLeader = true;
        BotLogger.Info("[TG Bot] Active Leader role acquired. Starting long-polling update loop.");

        try
        {
            long initialOffset = await LoadPersistedOffsetAsync(stoppingToken);
            await PollUpdatesLoopAsync(token, initialOffset, leaderLock, stoppingToken);
        }
        finally
        {
            _isLeader = false;
            await leaderLock.ReleaseAsync();
        }
    }

    private async Task PollUpdatesLoopAsync(string token, long initialOffset, ValutaBot.MiniApp.Services.PostgresDistributedLock leaderLock, CancellationToken stoppingToken)
    {
        long offset = initialOffset;
        var inFlightHandlers = new ConcurrentDictionary<Task, byte>();
        DateTime lastPruneTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!leaderLock.IsHeld)
                {
                    _isLeader = false;
                    BotLogger.Warn("[TG Bot] Advisory lock lost due to network partition/disconnect. Relinquishing leader role...");
                    await leaderLock.WaitAcquireAsync(TimeSpan.FromSeconds(2), stoppingToken);
                    if (stoppingToken.IsCancellationRequested) break;
                    _isLeader = true;
                    BotLogger.Info("[TG Bot] Advisory lock re-acquired. Resuming polling as active leader.");
                }

                // Periodic TTL eviction across per-chat rate limits and orphaned user states
                if (DateTime.UtcNow - lastPruneTime > TimeSpan.FromMinutes(2))
                {
                    lastPruneTime = DateTime.UtcNow;
                    PruneOutboundThrottleCache();
                    PruneExpiredUserStates();
                    ValutaBot.App.MiniApp.Data.Repositories.UserRepository.PruneExpiredSettings();
                }

                string url = $"https://api.telegram.org/bot{token}/getUpdates?offset={offset}&timeout=30";
                var client = MiniAppController.HttpFactory?.CreateClient("Telegram") ?? _httpClient;
                var response = await client.GetAsync(url, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    int backoffSeconds = 5;

                    if (statusCode == 409)
                    {
                        backoffSeconds = 10 + Random.Shared.Next(1, 5);
                        BotLogger.Warn($"[TG Bot] 409 Conflict: another bot instance is polling Telegram updates. Backing off for {backoffSeconds}s...");
                    }
                    else if (statusCode == 429)
                    {
                        backoffSeconds = 30;
                        if (response.Headers.RetryAfter?.Delta is { } d)
                        {
                            backoffSeconds = Math.Max(1, (int)Math.Ceiling(d.TotalSeconds));
                        }
                        else
                        {
                            try
                            {
                                if (response.Content != null)
                                {
                                    var errJson = await response.Content.ReadAsStringAsync(stoppingToken);
                                    if (!string.IsNullOrWhiteSpace(errJson))
                                    {
                                        using var errDoc = JsonDocument.Parse(errJson);
                                        if (errDoc.RootElement.TryGetProperty("parameters", out var param) &&
                                            param.TryGetProperty("retry_after", out var retryAfter))
                                        {
                                            backoffSeconds = Math.Max(1, retryAfter.GetInt32());
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        backoffSeconds += Random.Shared.Next(0, 3);
                        RegisterTgBlock(backoffSeconds);
                        BotLogger.Warn($"[TG Bot] Rate Limited (429). Global gate engaged for {backoffSeconds}s.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                    continue;
                }

                Interlocked.Exchange(ref _lastSuccessfulPollUtcTicks, DateTime.UtcNow.Ticks);

                var jsonStr = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("result", out var resultArr) && resultArr.ValueKind == JsonValueKind.Array)
                {
                    long highestBatchUpdateId = -1;
                    foreach (var update in resultArr.EnumerateArray())
                    {
                        long updateId = update.GetProperty("update_id").GetInt64();
                        if (updateId > highestBatchUpdateId) highestBatchUpdateId = updateId;
                        offset = updateId + 1;

                        if (update.TryGetProperty("message", out var message))
                        {
                            long chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
                            string text = message.TryGetProperty("text", out var tProp) ? (tProp.GetString() ?? "") : "";
                            
                            string username = "";
                            if (message.TryGetProperty("from", out var fromUser))
                            {
                                username = fromUser.TryGetProperty("username", out var uProp) ? (uProp.GetString() ?? "") : "";
                            }

                            // SECURITY: Anti-Spam Rate Limiter (Max 1 msg per second)
                            if (UserLastActivity.TryGetValue(chatId, out _))
                            {
                                BotLogger.Warn($"[Anti-Spam 🛡️] Dropped flood message from {chatId}");
                                continue;
                            }
                            UserLastActivity.Set(chatId, true, TimeSpan.FromSeconds(1));

                            var msgTask = Task.Run(async () =>
                            {
                                try { await HandleMessage(token, chatId, text, username, _webAppUrl); }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TG Bot] HandleMessage error ({chatId}): {ex.Message}");
                                }
                            });
                            inFlightHandlers.TryAdd(msgTask, 0);
                            _ = msgTask.ContinueWith(t => inFlightHandlers.TryRemove(t, out _), TaskContinuationOptions.ExecuteSynchronously);
                        }
                        else if (update.TryGetProperty("callback_query", out var callbackQuery))
                        {
                            // FIX: callback_query may be an inline callback without a "message" field
                            if (!callbackQuery.TryGetProperty("message", out var cbMessage)) continue;

                            string queryId = callbackQuery.TryGetProperty("id", out var qId) ? (qId.GetString() ?? "") : "";
                            long chatId = cbMessage.GetProperty("chat").GetProperty("id").GetInt64();
                            string data = callbackQuery.TryGetProperty("data", out var dProp) ? (dProp.GetString() ?? "") : "";
                            int messageId = cbMessage.GetProperty("message_id").GetInt32();

                            if (string.IsNullOrEmpty(data)) continue; // no data to process

                            string username = "";
                            if (callbackQuery.TryGetProperty("from", out var fromUser))
                            {
                                username = fromUser.TryGetProperty("username", out var uProp) ? (uProp.GetString() ?? "") : "";
                            }

                            // SECURITY: Anti-Spam Rate Limiter for Callbacks
                            if (UserLastActivity.TryGetValue($"cb_{chatId}", out _))
                            {
                                BotLogger.Warn($"[Anti-Spam 🛡️] Dropped flood callback from {chatId}");
                                // SRE-FIX: Dismiss Telegram inline button loading spinner even when rate-limited
                                _ = AnswerCallbackQuery(token, queryId, "⏳ Пожалуйста, подождите секунду...");
                                continue;
                            }
                            UserLastActivity.Set($"cb_{chatId}", true, TimeSpan.FromSeconds(1));

                            var cbTask = Task.Run(async () =>
                            {
                                try { await HandleCallback(token, queryId, chatId, data, messageId, username, _webAppUrl); }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TG Bot] HandleCallback error ({chatId}): {ex.Message}");
                                }
                            });
                            inFlightHandlers.TryAdd(cbTask, 0);
                            _ = cbTask.ContinueWith(t => inFlightHandlers.TryRemove(t, out _), TaskContinuationOptions.ExecuteSynchronously);
                        }
                    }

                    if (highestBatchUpdateId >= 0)
                    {
                        await SavePersistedOffsetAsync(offset, stoppingToken);
                    }
                }
            }
            catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex is System.Net.Http.HttpRequestException || ex is System.IO.IOException || ex is TaskCanceledException)
                    BotLogger.Warn($"[TG Bot] Transient polling jitter: {ex.Message}");
                else
                    BotLogger.Error($"[TG Bot] Error in polling loop: {ex.Message}", ex);
                await Task.Delay(2000, stoppingToken);
            }
        }

        if (!inFlightHandlers.IsEmpty)
        {
            BotLogger.Info($"[TG Bot] Graceful Shutdown: waiting up to 12s for {inFlightHandlers.Count} in-flight Telegram handlers...");
            await Task.WhenAny(Task.WhenAll(inFlightHandlers.Keys), Task.Delay(TimeSpan.FromSeconds(12)));
        }

        if (offset > 0)
        {
            await SavePersistedOffsetAsync(offset, CancellationToken.None);
        }
    }

    internal static async Task<HttpResponseMessage> PostTelegramJsonWithRetryAsync(string url, string jsonPayload, CancellationToken ct = default)
    {
        var client = MiniAppController.HttpFactory?.CreateClient("Telegram") ?? _httpClient;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await WaitTgGateAsync(ct);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            
            HttpResponseMessage resp;
            try
            {
                resp = await client.PostAsync(url, content, ct);
            }
            catch (Exception ex) when (attempt < 3 && (ex is HttpRequestException || ex is System.IO.IOException || ex is TimeoutException || ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                BotLogger.Warn($"[TG Bot API] Outbound call transient failure (attempt {attempt}/3): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                BotLogger.Warn($"[TG Bot API] Outbound call failed after retries: {ex.Message}");
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"{{\"ok\":false,\"description\":\"Network failure: {ex.Message}\"}}", Encoding.UTF8, "application/json")
                };
            }

            if ((int)resp.StatusCode == 429)
            {
                int delaySec = 5;
                if (resp.Headers.RetryAfter?.Delta.HasValue == true)
                {
                    delaySec = Math.Max(1, (int)resp.Headers.RetryAfter.Delta.Value.TotalSeconds);
                }
                else
                {
                    try
                    {
                        if (resp.Content != null)
                        {
                            var errJson = await resp.Content.ReadAsStringAsync(ct);
                            if (!string.IsNullOrWhiteSpace(errJson))
                            {
                                using var doc = JsonDocument.Parse(errJson);
                                if (doc.RootElement.TryGetProperty("parameters", out var p) &&
                                    p.TryGetProperty("retry_after", out var ra))
                                {
                                    delaySec = Math.Max(1, ra.GetInt32());
                                }
                            }
                        }
                    }
                    catch { }
                }
                resp.Dispose();
                RegisterTgBlock(delaySec);
                BotLogger.Warn($"[TG Bot API] 429 Rate Limit on outbound call. Backing off for {delaySec}s (Attempt {attempt}/3)");
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySec, 120)), ct);
                    continue;
                }
                
                // FIX: Всегда инициализируем StringContent во избежание NullReferenceException у вызывающего кода
                return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"ok\":false,\"description\":\"Too Many Requests: retry limit exceeded\"}", Encoding.UTF8, "application/json")
                };
            }

            // Гарантируем, что Content всегда инициализирован во избежание NRE у вызывающего кода
            resp.Content ??= new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json");
            return resp;
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"ok\":false,\"description\":\"Too Many Requests: retry limit exceeded\"}", Encoding.UTF8, "application/json")
        };
    }

    internal static async Task<bool> SendMessage(string token, long chatId, string text)
    {
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await ThrottleOutboundAsync(chatId);
            try
            {
                await client.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html);
                return true;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
            {
                int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
                RegisterTgBlock(retrySec);
                BotLogger.Warn($"[TG Bot] sendMessage 429 rate-limited (attempt {attempt}/3). Retrying after {retrySec}s...");
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retrySec));
                    continue;
                }
                return false;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && apiEx.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                BotLogger.Warn($"[TG Bot] sendMessage HTML entity parse failure on chat {chatId}. Falling back to plain text delivery: {apiEx.Message}");
                try
                {
                    await ThrottleOutboundAsync(chatId);
                    await client.SendTextMessageAsync(chatId, text, parseMode: null);
                    return true;
                }
                catch (Exception plainEx)
                {
                    Console.WriteLine($"[TG Bot] sendMessage plain text fallback exception: {plainEx.Message}");
                    return false;
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 403)
            {
                BotLogger.Warn($"[TG Bot] Chat {chatId} blocked the bot (403 Forbidden).");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG Bot] sendMessage SDK exception (attempt {attempt}/3): {ex.Message}");
                if (attempt < 3)
                {
                    await Task.Delay(1000 * attempt);
                    continue;
                }
                return false;
            }
        }
        return false;
    }

    internal static async Task<bool> SendMessageWithKeyboard(string token, long chatId, string text, object keyboard)
    {
        try
        {
            await ThrottleOutboundAsync(chatId);
            var payload = new { chat_id = chatId, text, parse_mode = "HTML", reply_markup = keyboard };
            var json = JsonSerializer.Serialize(payload);
            using var resp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/sendMessage", json);
            if (resp.IsSuccessStatusCode)
            {
                return true;
            }

            var err = await resp.Content.ReadAsStringAsync();
            if (err.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                BotLogger.Warn($"[TG Bot] SendMessageWithKeyboard HTML parse error on chat {chatId}. Retrying as plain text.");
                await ThrottleOutboundAsync(chatId);
                var fallbackPayload = new { chat_id = chatId, text, reply_markup = keyboard };
                var fallbackJson = JsonSerializer.Serialize(fallbackPayload);
                using var fbResp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/sendMessage", fallbackJson);
                return fbResp.IsSuccessStatusCode;
            }
            Console.WriteLine($"[TG Bot] sendMessage error: {err}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] sendMessage exception: {ex.Message}");
            return false;
        }
    }

    internal static async Task<bool> EditMessageText(string token, long chatId, int messageId, string text)
    {
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await ThrottleOutboundAsync(chatId);
            try
            {
                await client.EditMessageTextAsync(chatId, messageId, text, parseMode: ParseMode.Html);
                return true;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
            {
                int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
                RegisterTgBlock(retrySec);
                BotLogger.Warn($"[TG Bot] editMessageText 429 rate-limited (attempt {attempt}/3). Retrying after {retrySec}s...");
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retrySec));
                    continue;
                }
                return false;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 400 && apiEx.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                BotLogger.Warn($"[TG Bot] editMessageText HTML parse failure on chat {chatId}. Falling back to plain text delivery.");
                try
                {
                    await ThrottleOutboundAsync(chatId);
                    await client.EditMessageTextAsync(chatId, messageId, text, parseMode: null);
                    return true;
                }
                catch (Exception plainEx)
                {
                    Console.WriteLine($"[TG Bot] editMessageText plain text fallback exception: {plainEx.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG Bot] editMessageText SDK exception (attempt {attempt}/3): {ex.Message}");
                if (attempt < 3)
                {
                    await Task.Delay(1000 * attempt);
                    continue;
                }
                return false;
            }
        }
        return false;
    }

    internal static async Task<bool> EditMessageTextWithKeyboard(string token, long chatId, int messageId, string text, object keyboard)
    {
        try
        {
            await ThrottleOutboundAsync(chatId);
            var payload = new { chat_id = chatId, message_id = messageId, text, parse_mode = "HTML", reply_markup = keyboard };
            var json = JsonSerializer.Serialize(payload);
            using var resp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/editMessageText", json);
            if (resp.IsSuccessStatusCode)
            {
                return true;
            }

            var err = await resp.Content.ReadAsStringAsync();
            if (err.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                BotLogger.Warn($"[TG Bot] editMessageTextWithKeyboard HTML parse error on chat {chatId}. Retrying as plain text.");
                await ThrottleOutboundAsync(chatId);
                var fallbackPayload = new { chat_id = chatId, message_id = messageId, text, reply_markup = keyboard };
                var fallbackJson = JsonSerializer.Serialize(fallbackPayload);
                using var fbResp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/editMessageText", fallbackJson);
                return fbResp.IsSuccessStatusCode;
            }
            Console.WriteLine($"[TG Bot] editMessageTextWithKeyboard error: {err}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] editMessageTextWithKeyboard exception: {ex.Message}");
            return false;
        }
    }

    private static async Task AnswerCallbackQuery(string token, string callbackQueryId, string text)
    {
        await ThrottleOutboundAsync(0);
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        try
        {
            await client.AnswerCallbackQueryAsync(callbackQueryId, text);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
        {
            int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
            RegisterTgBlock(retrySec);
            BotLogger.Warn($"[TG Bot] answerCallbackQuery 429 rate-limited. Global gate engaged for {retrySec}s.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] answerCallbackQuery SDK exception: {ex.Message}");
        }
    }

    private static async Task ResetChatMenuButton(string token, long chatId)
    {
        try
        {
            await ThrottleOutboundAsync(chatId);
            var payload = new
            {
                chat_id = chatId,
                menu_button = new { type = "default" }
            };
            var json = JsonSerializer.Serialize(payload);
            using var resp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/setChatMenuButton", json);
        }
        catch { }
    }

    private static async Task SendDatabaseFile(string token, long chatId, string filePath, string caption)
    {
        if (!File.Exists(filePath))
        {
            await SendMessage(token, chatId, $"❌ Файл {Path.GetFileName(filePath)} еще не создан.");
            return;
        }

        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        try
        {
            await ThrottleOutboundAsync(chatId);
            await using var stream = File.OpenRead(filePath);
            await client.SendDocumentAsync(
                chatId: chatId,
                document: InputFile.FromStream(stream, Path.GetFileName(filePath)),
                caption: caption
            );
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
        {
            int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
            RegisterTgBlock(retrySec);
            BotLogger.Warn($"[TG Bot] sendDocument 429 rate-limited. Global gate engaged for {retrySec}s.");
        }
        catch (Exception ex)
        {
            await SendMessage(token, chatId, $"❌ Ошибка отправки файла: {ex.Message}");
        }
    }
}
