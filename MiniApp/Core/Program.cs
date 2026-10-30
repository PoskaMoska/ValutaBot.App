using System.Text.Json;

namespace ValutaBot.MiniApp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--backtest")
        {
#if DEBUG
            await ValutaBot.App.MiniApp.Backtesting.BacktestEntryPoint.RunAsync(args);
#else
            Console.WriteLine("[Backtest] Not available in production build. Use Debug configuration.");
#endif
            return;
        }

        if (args.Length >= 2 && args[0] == "--replay-test")
        {
            string corpusPath = args[1];
            bool success = await ValutaBot.MiniApp.Core.ReplayTestEngine.RunAllAsync(corpusPath);
            Environment.Exit(success ? 0 : 1);
            return;
        }

        if (args.Length >= 4 && args[0] == "--export-csv")
        {
            // Usage: dotnet run -- --export-csv EUR/USD 1m output.csv
            string symbol = args[1];
            string interval = args[2];
            string outPath = args[3];
            await ValutaBot.MiniApp.Core.ReplayTestEngine.ExportCsvAsync(symbol, interval, outPath);
            return;
        }

        if (args.Length >= 1 && args[0] == "--diag")
        {
            await DiagRunner.RunAsync();
            return;
        }

        try { Console.Title = "TradeBE Smart Terminal Core"; } catch { /* not a TTY (Docker/Linux) */ }

        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5000;

        // ── Fast In-Process Resuscitation Supervisor ────────────────────────
        // Replaces blunt process abort with an autonomous crash recovery loop.
        // Instead of a 15-30s container cold reboot on Railway, transient crashes
        // are caught, flushed, and resuscitated within 2-3s in the same container.
        int consecutiveCrashes = 0;
        const int maxInProcessRestarts = 5;

        AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
        {
            var ex = eventArgs.ExceptionObject as Exception;
            Console.WriteLine($"[CRITICAL] AppDomain UnhandledException (IsTerminating={eventArgs.IsTerminating}): {ex?.Message}\n{ex?.StackTrace}");
            try { RealtimeTickCollector.FlushAsync().GetAwaiter().GetResult(); } catch { }
        };

        TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
        {
            Console.WriteLine($"[WARN] UnobservedTaskException suppressed: {eventArgs.Exception?.Message}");
            eventArgs.SetObserved();
        };

        while (true)
        {
            var runStartTime = DateTime.UtcNow;
            try
            {
                await MiniAppController.StartAsync(args, port);

                // If StartAsync returned cleanly without exception:
                // could be a clean shutdown or an emergency self-recycle (Tier 3 RAM Guard).
                var runDuration = DateTime.UtcNow - runStartTime;
                Console.WriteLine($"[Host Supervisor] Application host stopped cleanly after {runDuration.TotalMinutes:F1}m.");

                // SRE-FIX: Explicit contract between RAM Guard SelfRecycle and SIGTERM
                if (MiniAppController.IsSelfRecycleRequested)
                {
                    MiniAppController.IsSelfRecycleRequested = false;
                    consecutiveCrashes = 0;
                    Console.WriteLine("[Host Supervisor] RAM Guard self-recycle triggered. Resuscitating host in-process in 2s...");
                    RealtimeTickCollector.PrepareForInProcessRestart();
                    await Task.Delay(2000);
                    continue;
                }

                // Clean container shutdown / SIGTERM acknowledged
                Console.WriteLine("[Host Supervisor] Container shutdown / SIGTERM acknowledged. Exiting host process cleanly.");
                break;
            }
            catch (Exception ex)
            {
                var runDuration = DateTime.UtcNow - runStartTime;
                if (runDuration.TotalMinutes >= 3)
                {
                    // Ran stably for at least 3 minutes -> reset consecutive crash counter
                    consecutiveCrashes = 0;
                }

                consecutiveCrashes++;
                Console.WriteLine($"[Host Supervisor] Crash #{consecutiveCrashes}/{maxInProcessRestarts} (Uptime: {runDuration.TotalSeconds:F0}s): {ex.Message}\n{ex.StackTrace}");

                // 1. Emergency tick flush to prevent data loss
                try { await RealtimeTickCollector.FlushAsync(); } catch { }

                // 2. Stop any dangling background stream loops before restart
                try { PocketOptionWebSocketStream.StopStream(); } catch { }
                try { TiingoWebSocketStream.StopStream(); } catch { }

                // 3. Re-initialize tick channel & collector tokens for clean in-process restart
                RealtimeTickCollector.PrepareForInProcessRestart();

                if (consecutiveCrashes >= maxInProcessRestarts)
                {
                    Console.WriteLine($"[Host Supervisor] Threshold reached ({consecutiveCrashes} consecutive crashes). Escalating to Railway container cold restart.");
                    try
                    {
                        using var alertCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        string safeMsg = System.Net.WebUtility.HtmlEncode(ex.Message);
                        await TelegramBotService.SendMessageToAdmins(
                            $"🚨 <b>[Host Supervisor] Превышен лимит внутрипроцессных перезапусков ({consecutiveCrashes}/{maxInProcessRestarts})</b>\nОшибка: <code>{safeMsg}</code>\nВыполняется холодный перезапуск контейнера Railway.").WaitAsync(alertCts.Token);
                    }
                    catch { }

                    throw; // Delegate to Docker / Railway orchestrator
                }

                int backoffSec = Math.Min(consecutiveCrashes * 2, 8);
                try
                {
                    using var alertCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    string safeMsg = System.Net.WebUtility.HtmlEncode(ex.Message);
                    await TelegramBotService.SendMessageToAdmins(
                        $"⚠️ <b>[Host Supervisor] Сбой хоста C#</b>\nПерехвачено: <code>{safeMsg}</code>\nМгновенная внутрипроцессная реанимация в том же контейнере через {backoffSec}с (попытка {consecutiveCrashes}/{maxInProcessRestarts}).").WaitAsync(alertCts.Token);
                }
                catch { }

                Console.WriteLine($"[Host Supervisor] Resuscitating host in-process in {backoffSec}s...");
                await Task.Delay(TimeSpan.FromSeconds(backoffSec));
            }
        }
    }
}
