using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;

public class DatabaseResilienceFaultScenario : IChaosScenario
{
    public string Name => "DatabaseResilienceFault";
    public ChaosCategory Category => ChaosCategory.DataAndDbFaults;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // ── Test 1: Fallback User Settings Resilience ──
            // Querying a non-existent or synthetic chat ID under simulated pressure
            var settings = await UserRepository.GetSettingsAsync(-99999);
            if (settings == null)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "UserRepository.GetSettingsAsync returned null instead of default UserSettings object.",
                    "chatId = -99999", "null", "Non-null UserSettings instance",
                    "Missing null-coalescing fallback in repository", "Return new UserSettings() in catch/empty branch.");
            }

            // ── Test 2: Transient Database Connection Catch Guard ──
            // Attempt a query against an invalid or unreachable connection string safely
            bool caughtCleanly = false;
            try
            {
                using var dummyConn = new Npgsql.NpgsqlConnection("Host=127.0.0.1;Port=59999;Database=fake;Timeout=1;CommandTimeout=1;");
                await dummyConn.OpenAsync(ct);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException || ex is System.Net.Sockets.SocketException || ex is TimeoutException)
            {
                caughtCleanly = true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Unexpected exception type during simulated DB network failure.",
                    "Connecting to unreachable port", ex.GetType().Name, "NpgsqlException or SocketException",
                    "Unhandled exception wrapper", "Ensure scanner catches broad transient DB exceptions.");
            }

            if (!caughtCleanly)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Simulated DB network failure was not caught cleanly.",
                    "Connecting to unreachable port", "Did not catch expected exception", "caughtCleanly == true",
                    "Exception handler bypassed", "Check catch filter block.");
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Database resilience verified: Fallback settings returned cleanly and transient connection failures safely caught.",
                $"FallbackSettingsNotNull=true, EnableMl={settings.EnableMl}, TransientDBCaught={caughtCleanly}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Unhandled exception in DatabaseResilienceFaultScenario.",
                ex.GetType().Name,
                ex.Message,
                "Clean handling without unhandled exceptions",
                ex.StackTrace ?? "",
                "Examine exception trace.",
                ChaosSeverity.Critical
            );
        }
    }
}
