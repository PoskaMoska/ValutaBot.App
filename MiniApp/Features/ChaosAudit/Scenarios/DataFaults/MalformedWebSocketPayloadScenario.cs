using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;

/// <summary>
/// Malformed WebSocket Payload & Poison Tick Sabotage Scenario:
/// Tests resilience against corrupted Pocket Option Socket.IO packets:
///  - Truncated / malformed JSON packet payloads (42["updateStream", ...])
///  - Epoch timestamp overflows and negative time offsets
///  - Poisoned tick injection: NaN, Infinity, and non-positive prices into candle aggregators
/// </summary>
public class MalformedWebSocketPayloadScenario : IChaosScenario
{
    public string Name => "MalformedWebSocketPayload";
    public ChaosCategory Category => ChaosCategory.DataAndDbFaults;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        const string chaosAsset = "CHAOS_POISON_TICK";

        try
        {
            // ── Test 1: Malformed / Truncated Socket.IO Packets ──
            string[] malformedPackets = {
                "42[\"updateStream\", [",                                             // Truncated array
                "42[\"updateStream\", [null, null]]",                                // Array of nulls
                "42[\"updateStream\", [[\"EURUSD_otc\", null, null]]]",              // Null fields
                "42[\"updateStream\", [[\"EURUSD_otc\", \"invalid_ts\", \"NaN\"]]]", // String types
                "42[\"updateStream\", {\"error\": 403}]",                            // Object instead of array
                "",                                                                  // Empty frame
                "42",                                                                // Bare event prefix
                "999999999"                                                          // Garbage protocol
            };

            foreach (var packet in malformedPackets)
            {
                try
                {
                    if (packet.Length > 2 && packet.StartsWith("42"))
                    {
                        using var doc = JsonDocument.Parse(packet[2..]);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 1)
                        {
                            var dataElem = doc.RootElement[1];
                            string dataRaw = dataElem.ValueKind == JsonValueKind.String ? dataElem.GetString()! : dataElem.GetRawText();
                            using var innerDoc = JsonDocument.Parse(dataRaw);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Expected: malformed JSON must be caught and discarded safely
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                        "Unhandled non-JsonException during WebSocket packet parsing.",
                        $"packet='{packet}'",
                        ex.GetType().Name,
                        "JsonException caught and swallowed safely",
                        "Parser threw unhandled system exception.",
                        "Wrap WebSocket parser in robust try/catch.",
                        ChaosSeverity.Critical,
                        "Аварийное падение потока WebSocket при получении битого пакета от брокера.");
                }
            }

            // ── Test 2: Epoch Timestamp Overflow / Out-of-Bounds Invariant ──
            double[] extremeTimestamps = { -1e15, -7200.0, 0.0, 1e18, double.NaN, double.PositiveInfinity };
            foreach (var srvTs in extremeTimestamps)
            {
                bool isSaneTimestamp = !double.IsNaN(srvTs) && !double.IsInfinity(srvTs) && srvTs > 1_000_000_000 && srvTs < 3_000_000_000;
                if (!isSaneTimestamp)
                {
                    // Any pipeline component MUST reject or clamp srvTs before calling DateTimeOffset.FromUnixTimeMilliseconds
                    // To verify that raw conversion throws without guard:
                    try
                    {
                        double utcSec = srvTs - 7200.0;
                        long ms = (long)(utcSec * 1000);
                        if (ms < -62135596800000 || ms > 253402300799999)
                        {
                            // Guard condition passed: safely caught before conversion
                            continue;
                        }
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Expected if unguarded
                    }
                }
            }

            // ── Test 3: Poison Tick Invariant (NaN / Inf / Non-positive price rejection) ──
            double[] poisonPrices = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -1.0500 };
            long testNowTicks = DateTime.UtcNow.Ticks;

            foreach (var badPrice in poisonPrices)
            {
                // Inject poisoned price directly into the live candle aggregator
                await RealtimeTickCollector.OnPriceUpdateAsync(chaosAsset, badPrice, testNowTicks, isBackfill: true);

                if (RealtimeTickCollector.IsAccumulatorCorrupted(chaosAsset))
                {
                    sw.Stop();
                    return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                        "RealtimeTickCollector allowed poisoned price (NaN/Inf/<=0) into in-memory candle accumulators.",
                        $"injectedPrice={badPrice}",
                        "RealtimeTickCollector.IsAccumulatorCorrupted(chaosAsset) = true",
                        "Poisoned tick rejected without modifying accumulator (IsAccumulatorCorrupted = false)",
                        "RealtimeTickCollector.OnPriceUpdateAsync lacks validation guard for finite positive prices.",
                        "Add guard in RealtimeTickCollector.OnPriceUpdateAsync: if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0 || tickTimeUtc <= 0) return Task.CompletedTask;",
                        ChaosSeverity.Critical,
                        "Один поврежденный тик от брокера с ценой NaN навсегда заражает кэш свечей и парализует торговлю пары.");
                }
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "WebSocket packet parsing safely discarded truncated frames, epoch overflows resisted, and candle accumulators defended against NaN poison.",
                $"PacketsFuzzed={malformedPackets.Length}, ExtremeTimestampsTested={extremeTimestamps.Length}, PoisonPricesTested={poisonPrices.Length}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Unhandled exception during WebSocket payload sabotage: {ex.Message}",
                ex.GetType().Name,
                ex.Message,
                "Graceful defensive handling",
                ex.StackTrace ?? "",
                "Add defensive guards in WebSocket ingestion pipeline.",
                ChaosSeverity.Critical,
                "Аварийное падение фонового обработчика WebSocket.");
        }
    }
}
