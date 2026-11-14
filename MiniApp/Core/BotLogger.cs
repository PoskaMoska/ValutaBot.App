using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Telegram.Bot;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp;

/// <summary>
/// High-performance non-blocking logger using System.Threading.Channels.
/// Writes to the console immediately, queues file I/O, and asynchronously sends ERRORS to Telegram.
/// </summary>
public static class BotLogger
{
    private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "valuta_bot.log");
    private const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10 MB per log segment (20 MB total with .old)
    private static readonly char[] AdminIdSeparators = { ',', ';', ' ', '\t', '\r', '\n' };
    
    // Unbounded channel for high-performance non-blocking fire-and-forget logging
    private static readonly Channel<string> _logChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(10000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    // Separate channel for Telegram alerts (smaller buffer, drops oldest if spamming)
    private static readonly Channel<string> _tgAlertChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private static long[] _dbAdminCache = Array.Empty<long>();
    private static DateTime _dbAdminCacheUtc = DateTime.MinValue;
    private static int _warnedMissingTargets = 0;

    internal static Action<string>? AlertObserverForTests { get; set; }

    static BotLogger()
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { /* Fallback to console only if filesystem restricts directory creation */ }

        // Start background worker threads
        Task.Factory.StartNew(ProcessLogsAsync, TaskCreationOptions.LongRunning);
        Task.Factory.StartNew(ProcessTelegramAlertsAsync, TaskCreationOptions.LongRunning);
    }

    internal static long[] ParseAdminChatIds(params string?[] rawValues)
    {
        var ids = new HashSet<long>();
        foreach (string? raw in rawValues)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (string part in raw.Split(AdminIdSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(part, out long id) && id > 0)
                {
                    ids.Add(id);
                }
            }
        }
        return ids.ToArray();
    }

    internal static string SanitizeMarkdownCodeBlock(string message)
    {
        string sanitized = (message ?? string.Empty).Replace("```", "'''", StringComparison.Ordinal);
        return sanitized.Length > 4000 ? sanitized.Substring(0, 4000) + "..." : sanitized;
    }

    private static async Task<long[]> ResolveAdminChatIdsAsync()
    {
        var merged = new HashSet<long>(ParseAdminChatIds(
            Environment.GetEnvironmentVariable("ADMIN_CHAT_ID"),
            Environment.GetEnvironmentVariable("ADMIN_CHAT_IDS"),
            Environment.GetEnvironmentVariable("ADMIN_IDS"),
            Environment.GetEnvironmentVariable("TG_ADMIN_ID")));

        if (!string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _dbAdminCacheUtc).TotalSeconds >= 60)
            {
                try
                {
                    var dbIds = await UserRepository.GetAdminChatIdsAsync();
                    _dbAdminCache = dbIds.Where(id => id > 0).ToArray();
                    _dbAdminCacheUtc = now;
                }
                catch
                {
                    _dbAdminCacheUtc = now.AddSeconds(-45);
                }
            }

            foreach (long id in _dbAdminCache)
            {
                merged.Add(id);
            }
        }

        return merged.ToArray();
    }

    private static async Task ProcessLogsAsync()
    {
        // Batch writes for performance
        var buffer = new StringBuilder();
        
        try
        {
            await foreach (var logLine in _logChannel.Reader.ReadAllAsync())
            {
                buffer.AppendLine(logLine);
                
                // If there are more items currently available, buffer them before hitting disk
                int batchLimit = 1000;
                while (batchLimit-- > 0 && _logChannel.Reader.TryRead(out var extraLine))
                {
                    buffer.AppendLine(extraLine);
                }

                try
                {
                    // SRE-FIX: Automatic file rotation to prevent container disk exhaustion (max 10MB + .old)
                    var fi = new FileInfo(LogFilePath);
                    if (fi.Exists && fi.Length > MaxLogFileSizeBytes)
                    {
                        string oldLogPath = LogFilePath + ".old";
                        File.Move(LogFilePath, oldLogPath, overwrite: true);
                    }
                    File.AppendAllText(LogFilePath, buffer.ToString(), Encoding.UTF8);
                }
                catch { /* Ignore I/O errors so we don't crash the background loop */ }
                
                buffer.Clear();
            }
        }
        catch { /* Process failure safety */ }
    }

    private static async Task ProcessTelegramAlertsAsync()
    {
        try
        {
            await foreach (var alert in _tgAlertChannel.Reader.ReadAllAsync())
            {
                var botClient = TelegramNotifier.GetBotClient();
                long[] targetIds = botClient != null ? await ResolveAdminChatIdsAsync() : Array.Empty<long>();
                if (botClient != null && targetIds.Length == 0 && Interlocked.CompareExchange(ref _warnedMissingTargets, 1, 0) == 0)
                {
                    Warn("[BotLogger] Telegram system alert skipped: no admin chat IDs found in ADMIN_CHAT_ID/ADMIN_CHAT_IDS or admins table.");
                }

                if (botClient != null && targetIds.Length > 0)
                {
                    string msg = SanitizeMarkdownCodeBlock(alert);
                    foreach (long targetId in targetIds)
                    {
                        try
                        {
                            await TelegramBotService.WaitTgGateAsync();
                            await botClient.SendTextMessageAsync(
                                chatId: targetId,
                                text: $"⚠️ *SYSTEM ALERT*\n```\n{msg}\n```",
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
                        }
                        catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
                        {
                            int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
                            TelegramBotService.RegisterTgBlock(retrySec);
                        }
                        catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 400)
                        {
                            // Retry without Markdown parse mode so formatting issues never drop a critical alert
                            try
                            {
                                await TelegramBotService.WaitTgGateAsync();
                                await botClient.SendTextMessageAsync(chatId: targetId, text: $"⚠️ SYSTEM ALERT\n{msg}");
                            }
                            catch { }
                        }
                        catch { /* Ignore telegram send errors (network drop, blocked bot) */ }
                    }
                }
                
                // Rate limit telegram messages to avoid API bans (max ~1 per sec for notifications)
                await Task.Delay(1000);
            }
        }
        catch { /* Process failure safety */ }
    }

    public static void Info(string message) => Log("INFO", message);
    
    public static void Warn(string message, Exception? ex = null) => 
        Log("WARN", ex != null ? $"{message} | Exception: {ex.Message}" : message);

    public static void Error(string message, Exception? ex = null)
    {
        string fullMessage = ex != null ? $"{message} | Details: {ex.Message}\n{ex.StackTrace}" : message;
        Log("ERR", fullMessage);
        
        AlertObserverForTests?.Invoke(fullMessage);
        // Queue to telegram dispatcher
        _tgAlertChannel.Writer.TryWrite(fullMessage);
    }

    public static void NotifyAdmin(string message)
    {
        Log("NOTIFY", message);
        AlertObserverForTests?.Invoke(message);
        _tgAlertChannel.Writer.TryWrite(message);
    }

    private static void Log(string level, string message)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string logLine = $"[{timestamp}] [{level}] {message}";

        // Console write is synchronous but usually fast/buffered
        Console.WriteLine(logLine);

        // Fire-and-forget enqueue for disk I/O
        _logChannel.Writer.TryWrite(logLine);
    }
}
