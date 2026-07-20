-- PosApp Cloud schema v1. Apply with the Turso CLI before deploying the Worker.
-- All identifiers are client-generated UUIDs; all business rows are tenant-scoped.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS organizations (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    schema_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS stores (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES organizations(id),
    name TEXT NOT NULL,
    code TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (tenant_id, code)
);

CREATE TABLE IF NOT EXISTS users (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES organizations(id),
    default_store_id TEXT NOT NULL REFERENCES stores(id),
    username TEXT NOT NULL,
    username_normalized TEXT NOT NULL,
    email TEXT NOT NULL,
    email_normalized TEXT NOT NULL,
    full_name TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    password_version INTEGER NOT NULL DEFAULT 1,
    role TEXT NOT NULL CHECK (role IN ('cashier', 'manager', 'admin')),
    permissions_json TEXT NOT NULL DEFAULT '[]',
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    deleted_at_utc TEXT,
    version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (username_normalized),
    UNIQUE (email_normalized)
);

CREATE TABLE IF NOT EXISTS user_store_assignments (
    tenant_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    store_id TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    PRIMARY KEY (tenant_id, user_id, store_id),
    FOREIGN KEY (user_id) REFERENCES users(id),
    FOREIGN KEY (store_id) REFERENCES stores(id)
);

CREATE TABLE IF NOT EXISTS registered_devices (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES organizations(id),
    registered_by_user_id TEXT NOT NULL REFERENCES users(id),
    name TEXT NOT NULL,
    operating_system TEXT NOT NULL,
    machine_name TEXT,
    status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked', 'disabled')),
    first_registered_at_utc TEXT NOT NULL,
    last_login_at_utc TEXT,
    last_sync_at_utc TEXT,
    updated_at_utc TEXT NOT NULL,
    revoked_at_utc TEXT,
    revoked_by_user_id TEXT
);

CREATE TABLE IF NOT EXISTS login_sessions (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL REFERENCES organizations(id),
    user_id TEXT NOT NULL REFERENCES users(id),
    device_id TEXT NOT NULL REFERENCES registered_devices(id),
    created_at_utc TEXT NOT NULL,
    last_login_at_utc TEXT NOT NULL,
    last_seen_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    revoked_at_utc TEXT,
    revoke_reason TEXT
);

CREATE TABLE IF NOT EXISTS refresh_tokens (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES login_sessions(id),
    family_id TEXT NOT NULL,
    parent_token_id TEXT,
    token_hash TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    used_at_utc TEXT,
    revoked_at_utc TEXT
);

CREATE TABLE IF NOT EXISTS security_events (
    id TEXT PRIMARY KEY,
    timestamp_utc TEXT NOT NULL,
    action TEXT NOT NULL,
    identifier_hash TEXT,
    device_id TEXT,
    request_id TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS login_attempts (
    key_hash TEXT PRIMARY KEY,
    attempt_count INTEGER NOT NULL,
    first_attempt_at_utc TEXT NOT NULL,
    blocked_until_utc TEXT,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_logs (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    store_id TEXT,
    user_id TEXT,
    device_id TEXT,
    timestamp_utc TEXT NOT NULL,
    action TEXT NOT NULL,
    affected_type TEXT NOT NULL,
    affected_id TEXT,
    request_id TEXT NOT NULL,
    metadata_json TEXT NOT NULL DEFAULT '{}'
);

-- Synchronized record tables deliberately share one envelope. Domain payloads
-- remain versioned JSON, while tenant/store/version/tombstone fields are always
-- server-controlled columns and can never be forged through the payload.
CREATE TABLE IF NOT EXISTS categories (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS product_units (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS products (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS customers (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS suppliers (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS user_sync_records (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS discounts (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS promotions (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS taxes (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS application_settings (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS sales (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS sale_items (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS payments (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS refunds (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS voided_sales (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS open_sales (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS purchases (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS purchase_items (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS inventory_adjustments (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS inventory_movements (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS expenses (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS register_sessions (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS cash_movements (
    id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, store_id TEXT, payload_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, deleted_at_utc TEXT,
    version INTEGER NOT NULL, created_by_user_id TEXT NOT NULL, updated_by_user_id TEXT NOT NULL,
    last_modified_device_id TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sync_operations (
    tenant_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    device_id TEXT NOT NULL,
    store_id TEXT,
    entity_type TEXT NOT NULL,
    record_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK (operation IN ('upsert', 'delete')),
    base_version INTEGER NOT NULL,
    result_version INTEGER NOT NULL,
    status TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL,
    PRIMARY KEY (tenant_id, operation_id),
    UNIQUE (tenant_id, idempotency_key)
);

CREATE TABLE IF NOT EXISTS sync_changes (
    cursor INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id TEXT NOT NULL,
    store_id TEXT,
    entity_type TEXT NOT NULL,
    record_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    updated_at_utc TEXT NOT NULL,
    deleted_at_utc TEXT,
    last_modified_device_id TEXT NOT NULL,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_sync_cursors (
    tenant_id TEXT NOT NULL,
    store_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    cursor INTEGER NOT NULL DEFAULT 0,
    last_pull_at_utc TEXT,
    PRIMARY KEY (tenant_id, store_id, device_id)
);

INSERT OR IGNORE INTO schema_migrations (version, name, applied_at_utc)
VALUES (1, 'initial multi-tenant offline-first schema', CURRENT_TIMESTAMP);
