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

            // NOTE: the former "dummy Npgsql connection to a dead port" test was removed: it only verified the Npgsql
            // library, not bot code. Live connectivity/schema is covered by DatabaseSchemaContractScenario.

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Database resilience verified: repository returns default settings for an unknown chat instead of null.",
                $"FallbackSettingsNotNull=true, EnableMl={settings.EnableMl}"
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
