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

    public static async Task<bool> IsUserAllowed(long chatId)
    {
        return await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsAdminAsync(chatId) || await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.IsUserAllowedAsync(chatId);
    }

    public static async Task SendMessageToAdmins(string text)
    {
        string? token = TelegramNotifier.GetToken();
        if (string.IsNullOrEmpty(token)) return;

        long[] coreAdmins = { 1103551505, 901492845 };

        foreach (long adminId in coreAdmins)
        {
            await SendMessage(token, adminId, text);
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

        await ValutaBot.App.MiniApp.Data.DbConnectionFactory.InitializeAsync();

        // Auto-seed admin IDs 1103551505, 901492845 and any env ADMIN_CHAT_ID / ADMIN_IDS
        await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAdminAsync(1103551505);
        await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAdminAsync(901492845);

        string envAdmin = Environment.GetEnvironmentVariable("ADMIN_CHAT_ID") ?? Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "";
        foreach (var part in envAdmin.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out long parsedEnvAdmin))
            {
                await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.AddAdminAsync(parsedEnvAdmin);
            }
        }

        long offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string url = $"https://api.telegram.org/bot{token}/getUpdates?offset={offset}&timeout=30";
                var response = await MiniAppController.HttpFactory!.CreateClient("Telegram").GetAsync(url, stoppingToken);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(5000, stoppingToken);
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

                            _ = Task.Run(async () =>
                            {
                                try { await HandleMessage(token, chatId, text, username, _webAppUrl); }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TG Bot] HandleMessage error ({chatId}): {ex.Message}");
                                }
                            }, stoppingToken);
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
                            if (UserLastActivity.TryGetValue(chatId, out _))
                            {
                                BotLogger.Warn($"[Anti-Spam 🛡️] Dropped flood callback from {chatId}");
                                continue;
                            }
                            UserLastActivity.Set(chatId, true, TimeSpan.FromSeconds(1));

                            _ = Task.Run(async () =>
                            {
                                try { await HandleCallback(token, queryId, chatId, data, messageId, username, _webAppUrl); }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TG Bot] HandleCallback error ({chatId}): {ex.Message}");
                                }
                            }, stoppingToken);
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
                BotLogger.Error($"[TG Bot] Error in polling loop: {ex.Message}", ex);
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    internal static async Task SendMessage(string token, long chatId, string text)
    {
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        try
        {
            await client.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] sendMessage SDK exception: {ex.Message}");
        }
    }

    internal static async Task SendMessageWithKeyboard(string token, long chatId, string text, object keyboard)
    {
        try
        {
            var payload = new { chat_id = chatId, text, parse_mode = "HTML", reply_markup = keyboard };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await MiniAppController.HttpFactory!.CreateClient("Telegram").PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
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
        var client = TelegramNotifier.GetBotClient() ?? new TelegramBotClient(token);
        try
        {
            await client.EditMessageTextAsync(chatId, messageId, text, parseMode: ParseMode.Html);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG Bot] editMessageText SDK exception: {ex.Message}");
        }
    }

    internal static async Task EditMessageTextWithKeyboard(string token, long chatId, int messageId, string text, object keyboard)
    {
        try
        {
            var payload = new { chat_id = chatId, message_id = messageId, text, parse_mode = "HTML", reply_markup = keyboard };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await MiniAppController.HttpFactory!.CreateClient("Telegram").PostAsync($"https://api.telegram.org/bot{token}/editMessageText", content);
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

