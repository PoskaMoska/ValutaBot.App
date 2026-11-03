-- ============================================================================
-- AUDIT 3 (v7): POSTGRESQL STORAGE RETENTION, INDEXING & TRANSACTION FIDELITY
-- Subsystem: ValutaBot High-Throughput Persistence & Data Governance
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. INDEX-ONLY SCAN COVERING INDEX FOR SUBMINUTE CANDLES
-- Prevents query plan degeneration to Seq Scan when fetching depth-250 candles.
-- By including all OHLCV columns in the index payload, queries bypass heap pages entirely.
-- ----------------------------------------------------------------------------
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_subminute_candles_covering
ON subminute_candles (asset, interval, open_time DESC)
INCLUDE (open_price, high_price, low_price, close_price, volume)
WITH (fillfactor = 90);

-- ----------------------------------------------------------------------------
-- 2. PARTIAL COVERING INDEX FOR RECENT VERIFIED TRADE OUTCOMES
-- Eliminates Seq Scan on heavy TOAST/JSONB fields in trade_outcomes.
-- Filters strictly on directional trades ('BUY', 'PUT') with labeled was_win.
-- ----------------------------------------------------------------------------
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_trade_outcomes_asset_tf_verified_real
ON trade_outcomes (asset, timeframe, verified_at DESC)
INCLUDE (was_win)
WHERE direction IN ('BUY', 'PUT') AND was_win IS NOT NULL
WITH (fillfactor = 90);

-- ----------------------------------------------------------------------------
-- 3. HOT-TUNING RETENTION PARAMETERS FOR ACTIVE TABLES
-- Preserves heap-only tuple updates without index page bloat.
-- ----------------------------------------------------------------------------
ALTER TABLE pending_trades SET (
    fillfactor = 70,
    autovacuum_vacuum_threshold = 20,
    autovacuum_vacuum_scale_factor = 0.05,
    autovacuum_vacuum_cost_limit = 500,
    autovacuum_vacuum_cost_delay = 2
);

ALTER TABLE trade_outcomes SET (
    fillfactor = 85,
    autovacuum_vacuum_scale_factor = 0.03,
    autovacuum_analyze_scale_factor = 0.02,
    autovacuum_vacuum_cost_limit = 2000,
    autovacuum_vacuum_cost_delay = 2,
    toast.autovacuum_vacuum_scale_factor = 0.03,
    toast.autovacuum_vacuum_cost_limit = 2000
);

ALTER TABLE subminute_candles SET (
    fillfactor = 80,
    autovacuum_vacuum_scale_factor = 0.02,
    autovacuum_analyze_scale_factor = 0.01,
    autovacuum_vacuum_cost_limit = 2000,
    autovacuum_vacuum_cost_delay = 2
);

-- ----------------------------------------------------------------------------
-- 4. PARTITION ROTATION ARCHITECTURE (BLUEPRINT FOR RANGE PARTITIONING)
-- For zero-downtime high-frequency candle ingestion (>100M rows), use range
-- partitioning by open_time. Pruning detached partitions takes ShareUpdateExclusiveLock
-- on parent instead of AccessExclusiveLock, preventing any freeze of NpgsqlBatch inserts.
--
-- Example partition drop pattern:
--   ALTER TABLE subminute_candles DETACH PARTITION subminute_candles_2026_10_25 CONCURRENTLY;
--   DROP TABLE subminute_candles_2026_10_25;
-- ----------------------------------------------------------------------------
