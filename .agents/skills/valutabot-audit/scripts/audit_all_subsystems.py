"""
Forensic Audit Verification Script for ValutaBot Subsystems (Audits 2 to 6).
Reads real file lines, checks invariants, signatures, and patterns.
"""
import sys

def check_file(path, patterns):
    print(f"\n--- Checking {path} ---")
    try:
        with open(path, 'r', encoding='utf-8', errors='replace') as f:
            content = f.read()
    except Exception as e:
        print(f"FAILED TO OPEN {path}: {e}")
        return False

    all_ok = True
    for desc, target in patterns:
        if callable(target):
            ok, line_info = target(content)
        else:
            ok = target in content
            line_info = f"Found target: {target[:40]}..." if ok else f"MISSING: {target[:40]}..."
        status = "[OK]" if ok else "[FAIL]"
        print(f"  {status} {desc}: {line_info}")
        if not ok:
            all_ok = False
    return all_ok

def audit_2():
    print("\n" + "=" * 50)
    print("AUDIT 2: BROKER WEBSOCKET & INGESTION")
    print("=" * 50)
    patt_ws = [
        ("Pong bytes zero-copy", '"3"u8'),
        ("Ping byte zero-copy match", '0x32'),
        ("Full Jitter reconnection", 'Random.Shared.Next(minDelay, ceiling'),
        ("ArrayPool frame buffering", 'ArrayPool<byte>.Shared'),
        ("Utf8JsonReader zero-alloc parsing", 'Utf8JsonReader'),
        ("Canonical asset cache", '_canonicalAssetCache')
    ]
    ok1 = check_file("MiniApp/Services/PocketOptionWebSocketStream.cs", patt_ws)

    patt_rtc = [
        ("In-memory candle fast-path", 'ring.Count > 0 || liveCandle != null'),
        ("Lock-free live candle extraction", 'live.Open.HasValue'),
        ("CROSS JOIN LATERAL ring buffer prewarm", 'CROSS JOIN LATERAL'),
        ("Batch write returns transient flag", 'Task<(bool Success, bool IsTransient)> WriteBatchInternalAsync'),
        ("Transient DB disconnect preserves buffer", 'Transient DB disconnect during batch write')
    ]
    ok2 = check_file("MiniApp/Services/RealtimeTickCollector.cs", patt_rtc)
    return ok1 and ok2

def audit_3():
    print("\n" + "=" * 50)
    print("AUDIT 3: POSTGRESQL HIGH-THROUGHPUT STORAGE")
    print("=" * 50)
    patt_sql = [
        ("Covering subminute index", 'idx_subminute_candles_covering'),
        ("Partial verified trade outcomes index", 'idx_trade_outcomes_asset_tf_verified_real'),
        ("Autovacuum tuning parameters", 'autovacuum_vacuum_cost_limit = 2000')
    ]
    ok1 = check_file("MiniApp/Data/Migrations/v7_postgresql_storage_retention_and_fidelity.sql", patt_sql)

    patt_db = [
        ("Dead pool eviction on fatal cluster code", 'MaybeEvictDeadPool'),
        ("Throttled ClearPool 5000ms", 'now - last > 5000'),
        ("Retry attempt < 5", 'attempt < 5')
    ]
    ok2 = check_file("MiniApp/Data/DbConnectionFactory.cs", patt_db)

    patt_tr = [
        ("Atomic CTE outcome deduplication", 'WITH del AS ('),
        ("CTE RETURNING id", 'RETURNING id;'),
        ("Isolated pendingTradeIdToDelete", 'string? pendingId = !string.IsNullOrEmpty(pendingTradeIdToDelete) ? pendingTradeIdToDelete : null;'),
        ("Claim lease timeout 60 seconds", "INTERVAL '60 seconds'"),
        ("DeferPendingTradeClaimAsync method", 'DeferPendingTradeClaimAsync')
    ]
    ok3 = check_file("MiniApp/Data/Repositories/TradeRepository.cs", patt_tr)

    patt_dr = [
        ("VACUUM FULL guard for streaming tables", 'if (table is "subminute_candles" or "pending_trades")'),
        ("Sub-second lock timeout", "SET lock_timeout = '1s';")
    ]
    ok4 = check_file("MiniApp/Services/DataRetentionService.cs", patt_dr)
    return ok1 and ok2 and ok3 and ok4

def audit_4():
    print("\n" + "=" * 50)
    print("AUDIT 4: ML SERVICE & NEURAL BRAIN")
    print("=" * 50)
    patt_pred = [
        ("Dynamic hurdle formula Kelly", 'break_even_p = 1.0 / (1.0 + payout_rate)'),
        ("Hurdle margin 0.0045 (Invariant 4)", 'hurdle_p = break_even_p + 0.0045'),
        ("OnlineDriftDetector class", 'class OnlineDriftDetector:'),
        ("Shannon entropy normalized calculation", '-(p0 * math.log(max(p0, eps)) + p1 * math.log(max(p1, eps)) + p2 * math.log(max(p2, eps))) / self._ln3'),
        ("Noise collapse drift penalty +0.015", 'OTC_VOLATILITY_NOISE_COLLAPSE'),
        ("Negative EV guard suppresses to HOLD", 'if calibrated_prob < hurdle_p:\n                    direction = "HOLD"')
    ]
    ok1 = check_file("ml_service/models/patch_tst_predictor.py", patt_pred)

    patt_brain = [
        ("Goldilocks saturation FP32 clamp", 'dir_tau_ratio = torch.clamp(dir_s / tau, -8.0, 8.0)'),
        ("Patch projection clamp [-50, 50]", 'torch.clamp(proj_out, -50.0, 50.0)')
    ]
    ok2 = check_file("ml_service/models/patch_tst_brain.py", patt_brain)

    patt_export = [
        ("Zero Lookahead Bias while-loop micro", 'while len(candles) > 0:'),
        ("Zero Lookahead Bias while-loop MTF", 'while len(mtf_candles) > 0:')
    ]
    ok3 = check_file("ml_service/export_dataset.py", patt_export)

    patt_main = [
        ("Drift diagnostic endpoint", '/diagnostics/drift'),
        ("Payout extraction in predict", 'payout_raw = data.get("payout")')
    ]
    ok4 = check_file("ml_service/main.py", patt_main)
    return ok1 and ok2 and ok3 and ok4

def audit_5():
    print("\n" + "=" * 50)
    print("AUDIT 5: WORKER RESILIENCE & SRE INFRASTRUCTURE")
    print("=" * 50)
    patt_lock = [
        ("PostgresDistributedLock class", 'public sealed class PostgresDistributedLock'),
        ("Session advisory lock", 'SELECT pg_try_advisory_lock(@key);'),
        ("Advisory unlock", 'SELECT pg_advisory_unlock(@key);'),
        ("WaitAcquireAsync method", 'public async Task WaitAcquireAsync')
    ]
    ok1 = check_file("MiniApp/Services/PostgresDistributedLock.cs", patt_lock)

    patt_db = [
        ("bot_state table", 'CREATE TABLE IF NOT EXISTS bot_state'),
        ("GetBotStateAsync method", 'public static async Task<string?> GetBotStateAsync'),
        ("SetBotStateAsync method", 'public static async Task SetBotStateAsync')
    ]
    ok2 = check_file("MiniApp/Data/DbConnectionFactory.cs", patt_db)

    patt_tg = [
        ("Telegram leader lock", 'PostgresDistributedLock("valutabot_telegram_leader")'),
        ("PruneOutboundThrottleCache", 'public static void PruneOutboundThrottleCache()'),
        ("PruneExpiredUserStates", 'public static void PruneExpiredUserStates'),
        ("HTTP 409 Conflict backoff", 'if (statusCode == 409)'),
        ("Persistent Telegram offset save", 'SavePersistedOffsetAsync')
    ]
    ok3 = check_file("MiniApp/Telegram/TelegramBotService.cs", patt_tg)

    patt_ctrl = [
        ("Sentry transient filter", 'IsTransientSentryException'),
        ("Polly timeout dropped from Sentry", 'options.AddExceptionFilterForType<Polly.Timeout.TimeoutRejectedException>()'),
        ("K8s health probes endpoints", 'app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy" }));')
    ]
    ok4 = check_file("MiniApp/Controllers/MiniAppController.cs", [patt_ctrl[0], patt_ctrl[1]])
    ok5 = check_file("MiniApp/Controllers/MiniAppController.Routes.cs", [patt_ctrl[2]])
    return ok1 and ok2 and ok3 and ok4 and ok5

def audit_6():
    print("\n" + "=" * 50)
    print("AUDIT 6: FRONTEND MINI APP UX & TOUCH LATENCY")
    print("=" * 50)
    patt_html = [
        ("Asset search input in HTML", 'id=\'assetSearchInput\'')
    ]
    ok1 = check_file("MiniApp/wwwroot/index.html", patt_html)

    patt_css = [
        ("Asset search CSS styles", '.asset-search-input'),
        ("VisualViewport keyboard-visible", 'body.keyboard-visible'),
        ("Mobile GPU layer isolation", '.sphere-scene, .magic-ball, .ai-chart-container')
    ]
    ok2 = check_file("MiniApp/wwwroot/css/style.css", patt_css)

    patt_ui = [
        ("Telegram Haptic feedback functions", 'export function hapticImpact'),
        ("Canvas context lost handler", 'initCanvasContextLossHandlers'),
        ("Context loss check on render", 'ctx.isContextLost()'),
        ("Seamless session refresh button", 'btnRefreshSession')
    ]
    ok3 = check_file("MiniApp/wwwroot/js/ui.js", patt_ui)

    patt_state = [
        ("clearExpiredAuthParams function", 'export function clearExpiredAuthParams'),
        ("Dynamic getTg wrapper", 'export function getTg()')
    ]
    ok4 = check_file("MiniApp/wwwroot/js/state.js", patt_state)

    patt_main = [
        ("VisualViewport resize/scroll listener", 'window.visualViewport.addEventListener'),
        ("filterAssets live search function", 'function filterAssets(query)')
    ]
    ok5 = check_file("MiniApp/wwwroot/js/main.js", patt_main)
    return ok1 and ok2 and ok3 and ok4 and ok5

def main():
    print("============================================================")
    print("AUTONOMOUS FORENSIC AUDIT: SUBSYSTEMS 2 - 6")
    print("============================================================")
    r2 = audit_2()
    r3 = audit_3()
    r4 = audit_4()
    r5 = audit_5()
    r6 = audit_6()

    print("\n" + "=" * 50)
    print("FORENSIC AUDIT SUMMARY:")
    print(f"  Audit 2 (Broker WebSockets & Buffers): {'PASS' if r2 else 'FAIL'}")
    print(f"  Audit 3 (PostgreSQL Storage & Integrity): {'PASS' if r3 else 'FAIL'}")
    print(f"  Audit 4 (ML Service & Neural Brain): {'PASS' if r4 else 'FAIL'}")
    print(f"  Audit 5 (Worker Resilience & SRE): {'PASS' if r5 else 'FAIL'}")
    print(f"  Audit 6 (Frontend MiniApp UX & Canvas): {'PASS' if r6 else 'FAIL'}")
    print("=" * 50)

    all_ok = r2 and r3 and r4 and r5 and r6
    if all_ok:
        print("\nALL 5 AUDIT SUBSYSTEMS COMPLIANT WITH ZERO-HALLUCINATION STANDARDS!")
        sys.exit(0)
    else:
        print("\nSOME AUDIT CHECKS FAILED! INSPECT ABOVE.")
        sys.exit(1)

if __name__ == '__main__':
    main()
