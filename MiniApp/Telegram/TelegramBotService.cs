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

    internal static void RegisterTgBlock(int seconds)
    {
        int clampedSec = Math.Clamp(seconds, 1, 120);
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
        if (string.IsNullOrEmpty(token)) return;

        var notifiedIds = new System.Collections.Generic.HashSet<long>();

        try
        {
            var dbAdmins = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.GetAdminChatIdsAsync();
            foreach (var adminId in dbAdmins)
            {
                await SendMessage(token, adminId, text);
                notifiedIds.Add(adminId);
            }
        }
        catch { }

        string envAdmin = Environment.GetEnvironmentVariable("ADMIN_CHAT_ID") ?? Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "";
        foreach (var part in envAdmin.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out long adminId) && !notifiedIds.Contains(adminId))
            {
                await SendMessage(token, adminId, text);
                notifiedIds.Add(adminId);
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

        Console.WriteLine($"[TG Bot] Service started. WebApp URL: {_webAppUrl}");

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

        long offset = 0;
        var inFlightHandlers = new ConcurrentDictionary<Task, byte>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string url = $"https://api.telegram.org/bot{token}/getUpdates?offset={offset}&timeout=30";
                var response = await MiniAppController.HttpFactory!.CreateClient("Telegram").GetAsync(url, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    int backoffSeconds = 5;
                    if ((int)response.StatusCode == 429)
                    {
                        try
                        {
                            var errJson = await response.Content.ReadAsStringAsync(stoppingToken);
                            using var errDoc = JsonDocument.Parse(errJson);
                            if (errDoc.RootElement.TryGetProperty("parameters", out var param) &&
                                param.TryGetProperty("retry_after", out var retryAfter))
                            {
                                backoffSeconds = Math.Max(retryAfter.GetInt32(), 5);
                            }
                            else
                            {
                                backoffSeconds = 30;
                            }
                        }
                        catch { backoffSeconds = 30; }

                        BotLogger.Warn($"[TG Bot] Rate Limited (429). Telegram instructed backoff of {backoffSeconds}s.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                    continue;
                }

                var jsonStr = await response.Content.ReadAsStringAsync(stoppingToken);
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("result", out var resultArr) && resultArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var update in resultArr.EnumerateArray())
                    {
                        long updateId = update.GetProperty("update_id").GetInt64();
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
                await Task.Delay(5000, stoppingToken);
            }
        }

        if (!inFlightHandlers.IsEmpty)
        {
            BotLogger.Info($"[TG Bot] Graceful Shutdown: waiting up to 5s for {inFlightHandlers.Count} in-flight Telegram handlers...");
            await Task.WhenAny(Task.WhenAll(inFlightHandlers.Keys), Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    internal static async Task<HttpResponseMessage> PostTelegramJsonWithRetryAsync(string url, string jsonPayload, CancellationToken ct = default)
    {
        var client = MiniAppController.HttpFactory!.CreateClient("Telegram");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(url, content, ct);
            if ((int)resp.StatusCode == 429 && attempt < 3)
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
                        var errJson = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(errJson);
                        if (doc.RootElement.TryGetProperty("parameters", out var p) &&
                            p.TryGetProperty("retry_after", out var ra))
                        {
                            delaySec = Math.Max(1, ra.GetInt32());
                        }
                    }
                    catch { }
                }
                resp.Dispose();
                RegisterTgBlock(delaySec);
                BotLogger.Warn($"[TG Bot API] 429 Rate Limit on outbound call. Backing off for {delaySec}s (Attempt {attempt}/3)");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySec, 120)), ct);
                continue;
            }
            return resp;
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
    }

    internal static async Task SendMessage(string token, long chatId, string text)
    {
        await WaitTgGateAsync();
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await client.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html);
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429 && attempt == 1)
            {
                int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
                RegisterTgBlock(retrySec);
                BotLogger.Warn($"[TG Bot] sendMessage 429 rate-limited. Retrying after {retrySec}s...");
                await Task.Delay(TimeSpan.FromSeconds(retrySec));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG Bot] sendMessage SDK exception: {ex.Message}");
                return;
            }
        }
    }

    internal static async Task SendMessageWithKeyboard(string token, long chatId, string text, object keyboard)
    {
        try
        {
            var payload = new { chat_id = chatId, text, parse_mode = "HTML", reply_markup = keyboard };
            var json = JsonSerializer.Serialize(payload);
            using var resp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/sendMessage", json);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[TG Bot] sendMessage error: {err}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] sendMessage exception: {ex.Message}");
        }
    }

    internal static async Task EditMessageText(string token, long chatId, int messageId, string text)
    {
        await WaitTgGateAsync();
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await client.EditMessageTextAsync(chatId, messageId, text, parseMode: ParseMode.Html);
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429 && attempt == 1)
            {
                int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
                RegisterTgBlock(retrySec);
                BotLogger.Warn($"[TG Bot] editMessageText 429 rate-limited. Retrying after {retrySec}s...");
                await Task.Delay(TimeSpan.FromSeconds(retrySec));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG Bot] editMessageText SDK exception: {ex.Message}");
                return;
            }
        }
    }

    internal static async Task EditMessageTextWithKeyboard(string token, long chatId, int messageId, string text, object keyboard)
    {
        try
        {
            var payload = new { chat_id = chatId, message_id = messageId, text, parse_mode = "HTML", reply_markup = keyboard };
            var json = JsonSerializer.Serialize(payload);
            using var resp = await PostTelegramJsonWithRetryAsync($"https://api.telegram.org/bot{token}/editMessageText", json);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[TG Bot] editMessageTextWithKeyboard error: {err}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] editMessageTextWithKeyboard exception: {ex.Message}");
        }
    }

    private static async Task AnswerCallbackQuery(string token, string callbackQueryId, string text)
    {
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        try
        {
            await client.AnswerCallbackQueryAsync(callbackQueryId, text);
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
            var payload = new
            {
                chat_id = chatId,
                menu_button = new { type = "default" }
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await MiniAppController.HttpFactory!.CreateClient("Telegram").PostAsync($"https://api.telegram.org/bot{token}/setChatMenuButton", content);
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
            await using var stream = File.OpenRead(filePath);
            await client.SendDocumentAsync(
                chatId: chatId,
                document: InputFile.FromStream(stream, Path.GetFileName(filePath)),
                caption: caption
            );
        }
        catch (Exception ex)
        {
            await SendMessage(token, chatId, $"❌ Ошибка отправки файла: {ex.Message}");
        }
    }
}
