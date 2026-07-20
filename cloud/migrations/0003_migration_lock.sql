-- Server-owned initial-migration lease. It closes the race between a cloud-empty
-- preview and the first legacy batch and authorizes historical price snapshots
-- only for the administrator device that acquired the lease.
ALTER TABLE registered_devices ADD COLUMN assigned_store_id TEXT;

CREATE INDEX IF NOT EXISTS ix_devices_assigned_store
ON registered_devices (tenant_id, assigned_store_id, status);

CREATE TABLE IF NOT EXISTS migration_sessions (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    store_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('active', 'completed', 'expired')),
    started_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    completed_at_utc TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_migration_active_store
ON migration_sessions (tenant_id, store_id)
WHERE status = 'active';

CREATE INDEX IF NOT EXISTS ix_migration_owner
ON migration_sessions (tenant_id, store_id, user_id, device_id, status, expires_at_utc);

INSERT OR IGNORE INTO schema_migrations (version, name, applied_at_utc)
VALUES (3, 'initial migration lock and legacy snapshot authorization', CURRENT_TIMESTAMP);
