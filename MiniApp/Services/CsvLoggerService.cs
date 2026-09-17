using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ValutaBot.MiniApp.Services
{
    public interface ICsvLoggerService
    {
        void LogCsvAsync(string filePath, string header, string line);
    }

    /// <summary>
    /// Thread-safe asynchronous queue for writing to CSV files without blocking.
    /// Replaces scattered SemaphoreSlim usages with a single dedicated background worker.
    /// </summary>
    public class CsvLoggerService : BackgroundService, ICsvLoggerService
    {
        private record CsvLogMessage(string FilePath, string Header, string Line);

        private readonly Channel<CsvLogMessage> _channel = Channel.CreateUnbounded<CsvLogMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        public void LogCsvAsync(string filePath, string header, string line)
        {
            _channel.Writer.TryWrite(new CsvLogMessage(filePath, header, line));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Batch writes by grouping by FilePath
            var buffer = new ConcurrentDictionary<string, StringBuilder>();
            var headers = new ConcurrentDictionary<string, string>();

            try
            {
                while (await _channel.Reader.WaitToReadAsync(stoppingToken))
                {
                    while (_channel.Reader.TryRead(out var msg))
                    {
                        if (!buffer.ContainsKey(msg.FilePath))
                        {
                            buffer[msg.FilePath] = new StringBuilder();
                        }
                        if (!headers.ContainsKey(msg.FilePath))
                        {
                            headers[msg.FilePath] = msg.Header;
                        }
                        buffer[msg.FilePath].AppendLine(msg.Line);
                    }

                    // Flush all buffers to disk
                    foreach (var kvp in buffer)
                    {
                        string filePath = kvp.Key;
                        string contents = kvp.Value.ToString();
                        
                        try
                        {
                            string? dir = Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            bool writeHeader = !File.Exists(filePath);
                            using var writer = new StreamWriter(filePath, append: true, Encoding.UTF8);
                            if (writeHeader)
                            {
                                await writer.WriteLineAsync(headers[filePath]);
                            }
                            await writer.WriteAsync(contents);
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Error($"[CsvLoggerService] Failed to write to {filePath}", ex);
                        }
                    }

                    buffer.Clear();
                    headers.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }
    }
}
