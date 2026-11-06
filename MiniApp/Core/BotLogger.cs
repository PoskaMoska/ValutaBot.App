using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Telegram.Bot;

namespace ValutaBot.MiniApp;

/// <summary>
/// High-performance non-blocking logger using System.Threading.Channels.
/// Writes to the console immediately, queues file I/O, and asynchronously sends ERRORS to Telegram.
/// </summary>
public static class BotLogger
{
    private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "valuta_bot.log");
    private const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10 MB per log segment (20 MB total with .old)
    
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

    private static long _adminChatId = 0;

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

        // Attempt to parse ADMIN_CHAT_ID for Telegram routing
        var adminIdStr = Environment.GetEnvironmentVariable("ADMIN_CHAT_ID") ?? Environment.GetEnvironmentVariable("TG_ADMIN_ID");
        if (long.TryParse(adminIdStr, out long parsedId))
        {
            _adminChatId = parsedId;
        }

        // Start background worker threads
        Task.Factory.StartNew(ProcessLogsAsync, TaskCreationOptions.LongRunning);
        Task.Factory.StartNew(ProcessTelegramAlertsAsync, TaskCreationOptions.LongRunning);
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
                if (botClient != null && _adminChatId != 0)
                {
                    try
                    {
                        await TelegramBotService.WaitTgGateAsync();
                        // Sanitize length for Telegram message limits (max 4096)
                        string msg = alert.Length > 4000 ? alert.Substring(0, 4000) + "..." : alert;
                        await botClient.SendTextMessageAsync(chatId: _adminChatId, text: $"⚠️ *SYSTEM ALERT*\n```\n{msg}\n```", parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
                    }
                    catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
                    {
                        int retrySec = Math.Clamp(apiEx.Parameters?.RetryAfter ?? 5, 1, 120);
                        TelegramBotService.RegisterTgBlock(retrySec);
                    }
                    catch { /* Ignore telegram send errors (network drop, blocked bot) */ }
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
        
        // Queue to telegram dispatcher
        _tgAlertChannel.Writer.TryWrite(fullMessage);
    }

    public static void NotifyAdmin(string message)
    {
        Log("NOTIFY", message);
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
