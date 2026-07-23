PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS owners (
    id TEXT PRIMARY KEY,
    email TEXT NOT NULL COLLATE NOCASE UNIQUE,
    display_name TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    password_salt TEXT NOT NULL,
    password_iterations INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    device_key TEXT NOT NULL,
    name TEXT NOT NULL,
    platform TEXT NOT NULL,
    app_version TEXT NOT NULL,
    created_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    revoked_at TEXT,
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    UNIQUE (owner_id, device_key)
);
CREATE INDEX IF NOT EXISTS ix_devices_owner ON devices(owner_id);

CREATE TABLE IF NOT EXISTS refresh_tokens (
    id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    token_hash TEXT NOT NULL UNIQUE,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    revoked_at TEXT,
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_owner_device ON refresh_tokens(owner_id, device_id);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_expiry ON refresh_tokens(expires_at);

CREATE TABLE IF NOT EXISTS stores (
    sync_id TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (owner_id, sync_id),
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    UNIQUE (owner_id, code)
);

CREATE TABLE IF NOT EXISTS snapshots (
    id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    store_sync_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    schema_version INTEGER NOT NULL,
    app_version TEXT NOT NULL,
    row_count INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    sync_cursor INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE RESTRICT,
    UNIQUE (owner_id, store_sync_id, version)
);
CREATE INDEX IF NOT EXISTS ix_snapshots_latest ON snapshots(owner_id, store_sync_id, version DESC);

CREATE TABLE IF NOT EXISTS sync_cursors (
    owner_id TEXT NOT NULL,
    store_sync_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    initial_snapshot_version INTEGER NOT NULL DEFAULT 0,
    pull_cursor INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (owner_id, store_sync_id, device_id),
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);


CREATE TABLE IF NOT EXISTS sync_records (
    owner_id TEXT NOT NULL,
    store_sync_id TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_sync_id TEXT NOT NULL,
    cloud_version INTEGER NOT NULL,
    entity_version INTEGER NOT NULL,
    operation TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    origin_device_id TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (owner_id, store_sync_id, entity_type, entity_sync_id),
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (origin_device_id) REFERENCES devices(id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_sync_records_store ON sync_records(owner_id, store_sync_id);

CREATE TABLE IF NOT EXISTS sync_changes (
    cursor INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id TEXT NOT NULL,
    store_sync_id TEXT NOT NULL,
    change_id TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_sync_id TEXT NOT NULL,
    cloud_version INTEGER NOT NULL,
    entity_version INTEGER NOT NULL,
    operation TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    origin_device_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,
    FOREIGN KEY (origin_device_id) REFERENCES devices(id) ON DELETE RESTRICT,
    UNIQUE (owner_id, change_id)
);
CREATE INDEX IF NOT EXISTS ix_sync_changes_pull ON sync_changes(owner_id, store_sync_id, cursor);

CREATE TABLE IF NOT EXISTS sync_idempotency (
    owner_id TEXT NOT NULL,
    change_id TEXT NOT NULL,
    store_sync_id TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_sync_id TEXT NOT NULL,
    cloud_version INTEGER NOT NULL,
    cursor INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (owner_id, change_id),
    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_sync_idempotency_created ON sync_idempotency(created_at);

CREATE TABLE IF NOT EXISTS auth_rate_limits (
    rate_key TEXT NOT NULL,
    window_start INTEGER NOT NULL,
    attempts INTEGER NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (rate_key, window_start)
);
CREATE INDEX IF NOT EXISTS ix_auth_rate_limits_updated ON auth_rate_limits(updated_at);
