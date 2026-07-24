PRAGMA foreign_keys = ON;

-- Apply once only when AUTO_INITIALIZE_SCHEMA is disabled. The Worker performs
-- these checks automatically when automatic initialization is enabled.
ALTER TABLE snapshots ADD COLUMN backup_set_id TEXT NOT NULL DEFAULT '';
ALTER TABLE snapshots ADD COLUMN captured_at TEXT NOT NULL DEFAULT '';
ALTER TABLE sync_changes ADD COLUMN operation_id TEXT NOT NULL DEFAULT '';
ALTER TABLE sync_idempotency ADD COLUMN operation_id TEXT NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS ix_snapshots_backup_set
    ON snapshots(owner_id, backup_set_id, captured_at DESC);
