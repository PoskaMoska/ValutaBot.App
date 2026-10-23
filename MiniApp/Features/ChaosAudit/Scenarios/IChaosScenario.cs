using System;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios;

public interface IChaosScenario
{
    string Name { get; }
    ChaosCategory Category { get; }
    Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default);
}
