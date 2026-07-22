PRAGMA foreign_keys = ON;

ALTER TABLE snapshots ADD COLUMN sync_cursor INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS sync_records (
    owner_id TEXT NOT NULL, store_sync_id TEXT NOT NULL, entity_type TEXT NOT NULL,
    entity_sync_id TEXT NOT NULL, cloud_version INTEGER NOT NULL, entity_version INTEGER NOT NULL,
    operation TEXT NOT NULL, payload_json TEXT NOT NULL, origin_device_id TEXT NOT NULL, updated_at TEXT NOT NULL,
    PRIMARY KEY (owner_id, store_sync_id, entity_type, entity_sync_id),
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (origin_device_id) REFERENCES devices(id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_sync_records_store ON sync_records(owner_id, store_sync_id);

CREATE TABLE IF NOT EXISTS sync_changes (
    cursor INTEGER PRIMARY KEY AUTOINCREMENT, owner_id TEXT NOT NULL, store_sync_id TEXT NOT NULL,
    change_id TEXT NOT NULL, entity_type TEXT NOT NULL, entity_sync_id TEXT NOT NULL,
    cloud_version INTEGER NOT NULL, entity_version INTEGER NOT NULL, operation TEXT NOT NULL,
    payload_json TEXT NOT NULL, origin_device_id TEXT NOT NULL, created_at TEXT NOT NULL,
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (origin_device_id) REFERENCES devices(id) ON DELETE RESTRICT,
    UNIQUE (owner_id, change_id)
);
CREATE INDEX IF NOT EXISTS ix_sync_changes_pull ON sync_changes(owner_id, store_sync_id, cursor);

CREATE TABLE IF NOT EXISTS sync_idempotency (
    owner_id TEXT NOT NULL, change_id TEXT NOT NULL, store_sync_id TEXT NOT NULL,
    entity_type TEXT NOT NULL, entity_sync_id TEXT NOT NULL, cloud_version INTEGER NOT NULL,
    cursor INTEGER NOT NULL, created_at TEXT NOT NULL,
    PRIMARY KEY (owner_id, change_id),
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_sync_idempotency_created ON sync_idempotency(created_at);
