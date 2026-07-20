-- PosApp Cloud schema v4.
-- Keep a financial header private until its declared immutable composition is
-- complete. This lets a large sale or purchase span bounded sync batches
-- without exposing a half-uploaded transaction to another device.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS sync_transaction_compositions (
    tenant_id TEXT NOT NULL,
    store_id TEXT NOT NULL,
    entity_type TEXT NOT NULL CHECK (entity_type IN ('sales', 'purchases')),
    record_id TEXT NOT NULL,
    expected_item_count INTEGER NOT NULL CHECK (expected_item_count >= 1),
    expected_payment_count INTEGER NOT NULL DEFAULT 0 CHECK (expected_payment_count >= 0),
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'complete')),
    created_at_utc TEXT NOT NULL,
    completed_at_utc TEXT,
    completion_cursor INTEGER,
    PRIMARY KEY (tenant_id, entity_type, record_id)
);

CREATE INDEX IF NOT EXISTS ix_sync_compositions_tenant_store_status
    ON sync_transaction_compositions (tenant_id, store_id, status, created_at_utc);

INSERT OR IGNORE INTO schema_migrations(version, name, applied_at_utc)
VALUES (4, 'financial-composition-staging', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
