# ValutaBot Full Audit Prompts Library (v8.1)

This reference file contains the standardized forensic audit prompts for all 6 subsystems of ValutaBot.

---

## Audit 1: Mathematical Core, Numerics, Indicators & Signal Consensus
Target files:
- `MiniApp/Engines/TechnicalAnalysisEngine.cs`
- `MiniApp/Engines/Indicators/StatefulIndicators.cs`
- `MiniApp/Engines/Indicators/StatefulSmc.cs`
- `MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs`
- `MiniApp/Features/MarketAnalysis/Engines/ContinuousStateEngine.cs`
- `MiniApp/Engines/IndicatorCache.cs`

Focus:
1. Sterbenz Lemma factorization in Savitzky-Golay SG1/SG2 derivative smoothing (IEEE 754 precision).
2. Division-by-zero, epsilon guards (`Math.Max(denom, 1e-8)`), BB Squeeze bounds.
3. SIMD vectorization contracts (`Vector<double>(ReadOnlySpan<double>)`).
4. SMC State Machine: BOS, CHoCH, OB, FVG detection and memory bounds.
5. Confluence Matrix: Conflict resolution and Invariant 4 positive EV hurdle (54.5%).

---

## Audit 2: High-Throughput Broker WebSockets & RingBuffer Pipeline
Target files:
- `MiniApp/Services/PocketOptionWebSocketStream.cs`
- `MiniApp/Services/RealtimeTickCollector.cs`
- `MiniApp/Engines/CandleRingBuffer.cs`

Focus:
1. Socket.IO heartbeat ping/pong zero-copy `Span<byte>` matching.
2. AWS Full Jitter exponential backoff: `Random(minDelay, ceiling)` in [1000, 5000] ms.
3. Allocation-free Utf8JsonReader parsing and canonical asset caching.
4. Non-blocking in-memory candle fast-path retrieval without PostgreSQL stalls.
5. Ring buffer prewarming via `CROSS JOIN LATERAL`.

---

## Audit 3: PostgreSQL High-Throughput Persistence & Data Governance
Target files:
- `MiniApp/Data/DbConnectionFactory.cs`
- `MiniApp/Data/Repositories/TradeRepository.cs`
- `MiniApp/Services/PendingTradeVerificationService.cs`
- `MiniApp/Services/TradeOutcomeTracker.cs`
- `MiniApp/Services/DataRetentionService.cs`
- `MiniApp/Services/RealtimeTickCollector.cs`
- `MiniApp/Data/Migrations/v7_postgresql_storage_retention_and_fidelity.sql`

Focus:
1. Covering index-only scans (`idx_subminute_candles_covering`, `idx_trade_outcomes_asset_tf_verified_real`).
2. Throttled dead connection pool eviction on fatal cluster codes.
3. Atomic CTE outcome insertion and pending trade deletion without dropping direct signals.
4. Claim lease synchronization: 60s timeout matching deferral intervals.
5. Strict `VACUUM FULL` prohibition on high-frequency tables.
6. Transient error distinction in batch tick writes preserving buffer ticks.

---

## Audit 4: ML Service, DualStream Cross-Attention & Drift Detection
Target files:
- `ml_service/models/patch_tst_brain.py`
- `ml_service/models/patch_tst_predictor.py`
- `ml_service/export_dataset.py`
- `ml_service/main.py`

Focus:
1. Dynamic break-even hurdle adaptation: $p_{hurdle}(R) = \frac{1}{1+R} + 0.0045$.
2. Rolling Shannon entropy drift detector ($W=100$, noise collapse $\ge 0.92$, mode collapse $\le 0.25$).
3. Zero Lookahead Bias anti-leakage while-loop trimming of micro and MTF candles.
4. FP32 numerical stability: Goldilocks sweet spot saturation clamp and patch projection bounds.

---

## Audit 5: Worker Resilience, Distributed Lock & Infrastructure Observability
Target files:
- `MiniApp/Services/PostgresDistributedLock.cs`
- `MiniApp/Data/DbConnectionFactory.cs`
- `MiniApp/Data/Repositories/UserRepository.cs`
- `MiniApp/Services/PendingTradeVerificationService.cs`
- `MiniApp/Telegram/TelegramBotService.cs`
- `MiniApp/Controllers/MiniAppController.cs`
- `MiniApp/Controllers/MiniAppController.Routes.cs`
- `Tests/ValutaBot.Tests/WorkerAndTelegramResilienceTests.cs`

Focus:
1. PostgreSQL session-level advisory locks for single-instance leader election during rolling updates.
2. Persistent bot state table (`bot_state`) for Telegram update offsets.
3. Memory leak prevention: periodic pruning of FSM user states and settings caches.
4. HTTP 409 Conflict handling and graceful shutdown.
5. Sentry event filtering for transient network jitter.
6. Kubernetes/Railway health probes (`/healthz`, `/ready`, `/live`, `/api/health`).

---

## Audit 6: Telegram MiniApp Frontend, UX, Touch Latency & Canvas
Target files:
- `MiniApp/wwwroot/index.html`
- `MiniApp/wwwroot/css/style.css`
- `MiniApp/wwwroot/js/state.js`
- `MiniApp/wwwroot/js/ui.js`
- `MiniApp/wwwroot/js/api.js`
- `MiniApp/wwwroot/js/main.js`
- `MiniApp/wwwroot/js/tests/test_audit_7_fixes.js`

Focus:
1. Dynamic live asset search and filtering.
2. VisualViewport mobile virtual keyboard handling and layout adaptation.
3. GPU layer isolation (`contain: paint; transform: translateZ(0)`).
4. Tactical haptic feedback integration via Telegram WebApp API.
5. Seamless auth session recovery on HTTP 401.
6. Canvas WebGL/2D context loss and restoration handling.
