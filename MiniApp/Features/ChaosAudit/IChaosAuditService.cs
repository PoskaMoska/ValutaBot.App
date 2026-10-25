using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit;

public interface IChaosAuditService
{
    Task<ChaosAuditReport> RunFullAuditAsync(CancellationToken ct = default);
    Task<ChaosAuditReport> RunCategoryAuditAsync(ChaosCategory category, CancellationToken ct = default);
    Task<ChaosScenarioResult?> RunSingleSabotageRoundAsync(CancellationToken ct = default);
    SabotageStatsDto GetSaboteurStats();
    ChaosAuditReport? GetLatestReport();
    void ResetAlertQuota();
    int GetActiveAlertCount();
}
