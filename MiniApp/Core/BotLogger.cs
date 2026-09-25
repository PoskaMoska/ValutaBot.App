using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// High-performance non-blocking logger using System.Threading.Channels.
/// Writes to the console immediately, but queues file I/O to a background worker to avoid blocking the calling thread.
/// </summary>
public static class BotLogger
{
    private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "valuta_bot.log");
    
    // Unbounded channel for high-performance non-blocking fire-and-forget logging
    private static readonly Channel<string> _logChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(10000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

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

        // Start background writer thread
        Task.Factory.StartNew(ProcessLogsAsync, TaskCreationOptions.LongRunning);
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
                    File.AppendAllText(LogFilePath, buffer.ToString(), Encoding.UTF8);
                }
                catch { /* Ignore I/O errors so we don't crash the background loop */ }
                
                buffer.Clear();
            }
        }
        catch { /* Process failure safety */ }
    }

    public static void Info(string message) => Log("INFO", message);
    
    public static void Warn(string message, Exception? ex = null) => 
        Log("WARN", ex != null ? $"{message} | Exception: {ex.Message}" : message);

    public static void Error(string message, Exception? ex = null) => 
        Log("ERR", ex != null ? $"{message} | Details: {ex.Message}\n{ex.StackTrace}" : message);

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
