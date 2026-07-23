const CLOUD_SCHEMA_STATEMENTS = [
  "CREATE TABLE IF NOT EXISTS owners (\n    id TEXT PRIMARY KEY,\n    email TEXT NOT NULL COLLATE NOCASE UNIQUE,\n    display_name TEXT NOT NULL,\n    password_hash TEXT NOT NULL,\n    password_salt TEXT NOT NULL,\n    password_iterations INTEGER NOT NULL,\n    created_at TEXT NOT NULL,\n    updated_at TEXT NOT NULL\n)",
  "CREATE TABLE IF NOT EXISTS devices (\n    id TEXT PRIMARY KEY,\n    owner_id TEXT NOT NULL,\n    device_key TEXT NOT NULL,\n    name TEXT NOT NULL,\n    platform TEXT NOT NULL,\n    app_version TEXT NOT NULL,\n    created_at TEXT NOT NULL,\n    last_seen_at TEXT NOT NULL,\n    revoked_at TEXT,\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    UNIQUE (owner_id, device_key)\n)",
  "CREATE INDEX IF NOT EXISTS ix_devices_owner ON devices(owner_id)",
  "CREATE TABLE IF NOT EXISTS refresh_tokens (\n    id TEXT PRIMARY KEY,\n    owner_id TEXT NOT NULL,\n    device_id TEXT NOT NULL,\n    token_hash TEXT NOT NULL UNIQUE,\n    expires_at TEXT NOT NULL,\n    created_at TEXT NOT NULL,\n    revoked_at TEXT,\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE\n)",
  "CREATE INDEX IF NOT EXISTS ix_refresh_tokens_owner_device ON refresh_tokens(owner_id, device_id)",
  "CREATE INDEX IF NOT EXISTS ix_refresh_tokens_expiry ON refresh_tokens(expires_at)",
  "CREATE TABLE IF NOT EXISTS stores (\n    sync_id TEXT NOT NULL,\n    owner_id TEXT NOT NULL,\n    code TEXT NOT NULL,\n    name TEXT NOT NULL,\n    address TEXT,\n    phone TEXT,\n    is_active INTEGER NOT NULL DEFAULT 1,\n    created_at TEXT NOT NULL,\n    updated_at TEXT NOT NULL,\n    PRIMARY KEY (owner_id, sync_id),\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    UNIQUE (owner_id, code)\n)",
  "CREATE TABLE IF NOT EXISTS snapshots (\n    id TEXT PRIMARY KEY,\n    owner_id TEXT NOT NULL,\n    store_sync_id TEXT NOT NULL,\n    device_id TEXT NOT NULL,\n    version INTEGER NOT NULL,\n    schema_version INTEGER NOT NULL,\n    app_version TEXT NOT NULL,\n    row_count INTEGER NOT NULL,\n    sha256 TEXT NOT NULL,\n    payload_json TEXT NOT NULL,\n    sync_cursor INTEGER NOT NULL DEFAULT 0,\n    created_at TEXT NOT NULL,\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE RESTRICT,\n    UNIQUE (owner_id, store_sync_id, version)\n)",
  "CREATE INDEX IF NOT EXISTS ix_snapshots_latest ON snapshots(owner_id, store_sync_id, version DESC)",
  "CREATE TABLE IF NOT EXISTS sync_cursors (\n    owner_id TEXT NOT NULL,\n    store_sync_id TEXT NOT NULL,\n    device_id TEXT NOT NULL,\n    initial_snapshot_version INTEGER NOT NULL DEFAULT 0,\n    pull_cursor INTEGER NOT NULL DEFAULT 0,\n    updated_at TEXT NOT NULL,\n    PRIMARY KEY (owner_id, store_sync_id, device_id),\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE\n)",
  "CREATE TABLE IF NOT EXISTS sync_records (\n    owner_id TEXT NOT NULL,\n    store_sync_id TEXT NOT NULL,\n    entity_type TEXT NOT NULL,\n    entity_sync_id TEXT NOT NULL,\n    cloud_version INTEGER NOT NULL,\n    entity_version INTEGER NOT NULL,\n    operation TEXT NOT NULL,\n    payload_json TEXT NOT NULL,\n    origin_device_id TEXT NOT NULL,\n    updated_at TEXT NOT NULL,\n    PRIMARY KEY (owner_id, store_sync_id, entity_type, entity_sync_id),\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    FOREIGN KEY (origin_device_id) REFERENCES devices(id) ON DELETE RESTRICT\n)",
  "CREATE INDEX IF NOT EXISTS ix_sync_records_store ON sync_records(owner_id, store_sync_id)",
  "CREATE TABLE IF NOT EXISTS sync_changes (\n    cursor INTEGER PRIMARY KEY AUTOINCREMENT,\n    owner_id TEXT NOT NULL,\n    store_sync_id TEXT NOT NULL,\n    change_id TEXT NOT NULL,\n    entity_type TEXT NOT NULL,\n    entity_sync_id TEXT NOT NULL,\n    cloud_version INTEGER NOT NULL,\n    entity_version INTEGER NOT NULL,\n    operation TEXT NOT NULL,\n    payload_json TEXT NOT NULL,\n    origin_device_id TEXT NOT NULL,\n    created_at TEXT NOT NULL,\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE,\n    FOREIGN KEY (origin_device_id) REFERENCES devices(id) ON DELETE RESTRICT,\n    UNIQUE (owner_id, change_id)\n)",
  "CREATE INDEX IF NOT EXISTS ix_sync_changes_pull ON sync_changes(owner_id, store_sync_id, cursor)",
  "CREATE TABLE IF NOT EXISTS sync_idempotency (\n    owner_id TEXT NOT NULL,\n    change_id TEXT NOT NULL,\n    store_sync_id TEXT NOT NULL,\n    entity_type TEXT NOT NULL,\n    entity_sync_id TEXT NOT NULL,\n    cloud_version INTEGER NOT NULL,\n    cursor INTEGER NOT NULL,\n    created_at TEXT NOT NULL,\n    PRIMARY KEY (owner_id, change_id),\n    FOREIGN KEY (owner_id) REFERENCES owners(id) ON DELETE CASCADE\n)",
  "CREATE INDEX IF NOT EXISTS ix_sync_idempotency_created ON sync_idempotency(created_at)",
  "CREATE TABLE IF NOT EXISTS auth_rate_limits (\n    rate_key TEXT NOT NULL,\n    window_start INTEGER NOT NULL,\n    attempts INTEGER NOT NULL,\n    updated_at TEXT NOT NULL,\n    PRIMARY KEY (rate_key, window_start)\n)",
  "CREATE INDEX IF NOT EXISTS ix_auth_rate_limits_updated ON auth_rate_limits(updated_at)"
];
let schemaInitializationPromise;

const ACCESS_TOKEN_SECONDS = 15 * 60;
const REFRESH_TOKEN_SECONDS = 30 * 24 * 60 * 60;
const PASSWORD_ITERATIONS = 120_000;
const MAX_SNAPSHOT_BYTES = 15_000_000;
const AUTH_WINDOW_SECONDS = 15 * 60;
const AUTH_ATTEMPT_LIMIT = 10;
const MAX_SYNC_BATCH = 100;
const MAX_PULL_LIMIT = 2000;
const ALLOWED_ENTITY_TYPES = new Set([
  "Store", "Category", "Customer", "Discount", "Product", "Supplier", "Tax", "User",
  "CashSession", "PurchaseDocument", "Sale", "PurchaseItem", "SaleItem", "SalePayment",
  "CashMovement", "StockTransaction", "StockTransfer", "StockTransferItem", "Setting",
]);

export default {
  async fetch(request, env) {
    try {
      const runtimeEnv = resolveEnvironment(env);
      validateEnvironment(runtimeEnv);
      if (isEnabled(runtimeEnv.AUTO_INITIALIZE_SCHEMA)) await ensureCloudSchema(runtimeEnv);
      return await route(request, runtimeEnv);
    } catch (error) {
      if (error instanceof HttpError) return json({ error: error.message }, error.status);
      console.error(error);
      return json({ error: "Internal server error." }, 500);
    }
  },
};

async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, "") || "/";

  if (request.method === "GET" && path === "/v1/health") {
    await query(env, "SELECT 1 AS ok");
    return json({ ok: true, service: "posapp-cloud", version: "1.9.5" });
  }

  if (request.method === "POST" && path === "/v1/auth/signup") {
    return signup(request, env);
  }
  if (request.method === "POST" && path === "/v1/auth/login") {
    return login(request, env);
  }
  if (request.method === "POST" && path === "/v1/auth/refresh") {
    return refresh(request, env);
  }

  const claims = await requireAccessToken(request, env);
  if (request.method === "GET" && path === "/v1/account") {
    return account(claims, env);
  }
  if (request.method === "POST" && path === "/v1/devices/register") {
    return registerDevice(request, claims, env);
  }
  if (request.method === "GET" && path === "/v1/devices") {
    return listDevices(claims, env);
  }
  if (request.method === "GET" && path === "/v1/stores") {
    return listStores(claims, env);
  }
  if (request.method === "POST" && path === "/v1/sync/snapshot/upload") {
    return uploadSnapshot(request, claims, env);
  }
  if (request.method === "GET" && path === "/v1/sync/snapshot/download") {
    return downloadSnapshot(url, claims, env);
  }
  if (request.method === "POST" && path === "/v1/sync/push") {
    return pushChanges(request, claims, env);
  }
  if (request.method === "GET" && path === "/v1/sync/pull") {
    return pullChanges(url, claims, env);
  }

  throw new HttpError(404, "Endpoint not found.");
}

async function signup(request, env) {
  const body = await readJson(request);
  const email = normalizeEmail(body.email);
  const password = validatePassword(body.password);
  const displayName = requiredText(body.displayName, "Display name", 100);
  const registrationKey = requiredText(body.registrationKey, "Registration key", 256);
  await enforceAuthRateLimit(request, email, env);

  if (!(await constantTimeTextEqual(registrationKey, env.REGISTRATION_KEY))) {
    throw new HttpError(403, "Registration key is invalid.");
  }
  const existing = await queryOne(env, "SELECT id FROM owners WHERE email = ?", [email]);
  if (existing) throw new HttpError(409, "An account with that email already exists.");

  const ownerId = crypto.randomUUID();
  const device = normalizeDevice(body);
  const deviceId = crypto.randomUUID();
  const salt = randomBase64Url(16);
  const passwordHash = await hashPassword(password, salt, PASSWORD_ITERATIONS);
  const now = new Date().toISOString();
  const refreshToken = randomBase64Url(32);
  const refreshId = crypto.randomUUID();
  const refreshHash = await sha256Base64Url(refreshToken);
  const refreshExpiresAt = new Date(Date.now() + REFRESH_TOKEN_SECONDS * 1000).toISOString();

  await transaction(env, [
    statement(
      `INSERT INTO owners (id, email, display_name, password_hash, password_salt, password_iterations, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [ownerId, email, displayName, passwordHash, salt, PASSWORD_ITERATIONS, now, now],
    ),
    statement(
      `INSERT INTO devices (id, owner_id, device_key, name, platform, app_version, created_at, last_seen_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [deviceId, ownerId, device.deviceKey, device.deviceName, device.platform, device.appVersion, now, now],
    ),
    statement(
      `INSERT INTO refresh_tokens (id, owner_id, device_id, token_hash, expires_at, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [refreshId, ownerId, deviceId, refreshHash, refreshExpiresAt, now],
    ),
  ]);
  await clearAuthRateLimit(request, email, env);
  return authResponse(env, { id: ownerId, email, display_name: displayName },
    { id: deviceId, name: device.deviceName }, refreshToken);
}

async function login(request, env) {
  const body = await readJson(request);
  const email = normalizeEmail(body.email);
  const password = validatePassword(body.password);
  await enforceAuthRateLimit(request, email, env);

  const owner = await queryOne(env,
    `SELECT id, email, display_name, password_hash, password_salt, password_iterations
     FROM owners WHERE email = ?`, [email]);
  if (!owner || !(await verifyPassword(password, owner.password_salt, Number(owner.password_iterations), owner.password_hash))) {
    throw new HttpError(401, "Email or password is incorrect.");
  }

  const device = normalizeDevice(body);
  const existingDevice = await queryOne(env,
    "SELECT id FROM devices WHERE owner_id = ? AND device_key = ?", [owner.id, device.deviceKey]);
  const deviceId = existingDevice?.id || crypto.randomUUID();
  const now = new Date().toISOString();
  const refreshToken = randomBase64Url(32);
  const refreshHash = await sha256Base64Url(refreshToken);
  const refreshExpiresAt = new Date(Date.now() + REFRESH_TOKEN_SECONDS * 1000).toISOString();

  const deviceStatement = existingDevice
    ? statement(
        `UPDATE devices SET name = ?, platform = ?, app_version = ?, last_seen_at = ?, revoked_at = NULL
         WHERE id = ? AND owner_id = ?`,
        [device.deviceName, device.platform, device.appVersion, now, deviceId, owner.id],
      )
    : statement(
        `INSERT INTO devices (id, owner_id, device_key, name, platform, app_version, created_at, last_seen_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
        [deviceId, owner.id, device.deviceKey, device.deviceName, device.platform, device.appVersion, now, now],
      );

  await transaction(env, [
    deviceStatement,
    statement(
      `INSERT INTO refresh_tokens (id, owner_id, device_id, token_hash, expires_at, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [crypto.randomUUID(), owner.id, deviceId, refreshHash, refreshExpiresAt, now],
    ),
  ]);
  await clearAuthRateLimit(request, email, env);
  return authResponse(env, owner, { id: deviceId, name: device.deviceName }, refreshToken);
}

async function refresh(request, env) {
  const body = await readJson(request);
  const refreshToken = requiredText(body.refreshToken, "Refresh token", 1024);
  const deviceKey = requiredText(body.deviceKey, "Device key", 128);
  const tokenHash = await sha256Base64Url(refreshToken);
  const row = await queryOne(env,
    `SELECT rt.id AS refresh_id, rt.owner_id, rt.device_id, rt.expires_at,
            o.email, o.display_name, d.name, d.device_key, d.revoked_at
     FROM refresh_tokens rt
     JOIN owners o ON o.id = rt.owner_id
     JOIN devices d ON d.id = rt.device_id
     WHERE rt.token_hash = ? AND rt.revoked_at IS NULL`, [tokenHash]);

  if (!row || row.device_key !== deviceKey || row.revoked_at || Date.parse(row.expires_at) <= Date.now()) {
    throw new HttpError(401, "Refresh token is invalid or expired.");
  }

  const newRefreshToken = randomBase64Url(32);
  const newRefreshHash = await sha256Base64Url(newRefreshToken);
  const now = new Date().toISOString();
  const expiresAt = new Date(Date.now() + REFRESH_TOKEN_SECONDS * 1000).toISOString();
  await transaction(env, [
    statement("UPDATE refresh_tokens SET revoked_at = ? WHERE id = ?", [now, row.refresh_id]),
    statement(
      `INSERT INTO refresh_tokens (id, owner_id, device_id, token_hash, expires_at, created_at)
       VALUES (?, ?, ?, ?, ?, ?)`,
      [crypto.randomUUID(), row.owner_id, row.device_id, newRefreshHash, expiresAt, now],
    ),
    statement("UPDATE devices SET last_seen_at = ? WHERE id = ?", [now, row.device_id]),
  ]);

  return authResponse(env,
    { id: row.owner_id, email: row.email, display_name: row.display_name },
    { id: row.device_id, name: row.name }, newRefreshToken);
}

async function account(claims, env) {
  const row = await queryOne(env,
    `SELECT o.id, o.email, o.display_name, d.id AS device_id, d.name AS device_name
     FROM owners o JOIN devices d ON d.owner_id = o.id
     WHERE o.id = ? AND d.id = ? AND d.revoked_at IS NULL`, [claims.sub, claims.did]);
  if (!row) throw new HttpError(401, "Account or device is no longer active.");
  return json({
    owner: { id: row.id, email: row.email, displayName: row.display_name },
    device: { id: row.device_id, name: row.device_name },
  });
}

async function registerDevice(request, claims, env) {
  const body = await readJson(request);
  const device = normalizeDevice(body);
  const existing = await queryOne(env,
    "SELECT id FROM devices WHERE owner_id = ? AND device_key = ?", [claims.sub, device.deviceKey]);
  const id = existing?.id || crypto.randomUUID();
  const now = new Date().toISOString();
  if (existing) {
    await execute(env,
      `UPDATE devices SET name = ?, platform = ?, app_version = ?, last_seen_at = ?, revoked_at = NULL
       WHERE id = ? AND owner_id = ?`,
      [device.deviceName, device.platform, device.appVersion, now, id, claims.sub]);
  } else {
    await execute(env,
      `INSERT INTO devices (id, owner_id, device_key, name, platform, app_version, created_at, last_seen_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [id, claims.sub, device.deviceKey, device.deviceName, device.platform, device.appVersion, now, now]);
  }
  return json({ device: { id, name: device.deviceName } });
}

async function listDevices(claims, env) {
  const rows = await query(env,
    `SELECT d.id, d.name, d.platform, d.app_version, d.created_at, d.last_seen_at,
            d.revoked_at, COUNT(sc.store_sync_id) AS store_cursor_count
     FROM devices d
     LEFT JOIN sync_cursors sc ON sc.owner_id = d.owner_id AND sc.device_id = d.id
     WHERE d.owner_id = ?
     GROUP BY d.id, d.name, d.platform, d.app_version, d.created_at, d.last_seen_at, d.revoked_at
     ORDER BY d.last_seen_at DESC`, [claims.sub]);
  return json({ devices: rows.map((row) => ({
    id: row.id,
    name: row.name,
    platform: row.platform,
    appVersion: row.app_version,
    createdAt: row.created_at,
    lastSeenAt: row.last_seen_at,
    isCurrent: row.id === claims.did,
    isRevoked: Boolean(row.revoked_at),
    storeCursorCount: Number(row.store_cursor_count || 0),
  })) });
}

async function listStores(claims, env) {
  const rows = await query(env,
    `SELECT sync_id, code, name, address, phone, is_active, updated_at
     FROM stores WHERE owner_id = ? ORDER BY name`, [claims.sub]);
  return json({ stores: rows.map((row) => ({
    syncId: row.sync_id,
    code: row.code,
    name: row.name,
    address: row.address,
    phone: row.phone,
    isActive: Number(row.is_active) === 1,
    updatedAt: row.updated_at,
  })) });
}

async function uploadSnapshot(request, claims, env) {
  const body = await readJson(request, MAX_SNAPSHOT_BYTES + 1_000_000);
  if (!body.store || typeof body.payload !== "object" || body.payload === null) {
    throw new HttpError(400, "Store metadata and snapshot payload are required.");
  }
  const storeSyncId = requiredText(body.store.syncId, "Store sync ID", 64);
  const code = requiredText(body.store.code, "Store code", 24);
  const name = requiredText(body.store.name, "Store name", 100);
  const payloadJson = JSON.stringify(body.payload);
  const payloadBytes = new TextEncoder().encode(payloadJson).byteLength;
  if (payloadBytes > MAX_SNAPSHOT_BYTES) throw new HttpError(413, "Snapshot exceeds the 15 MB per-store limit.");
  const rowCount = integerInRange(body.rowCount, "Row count", 1, 100_000_000);
  const schemaVersion = integerInRange(body.schemaVersion, "Schema version", 1, 1000);
  const appVersion = requiredText(body.appVersion, "App version", 40);
  const syncCursor = integerInRange(body.syncCursor ?? 0, "Sync cursor", 0, Number.MAX_SAFE_INTEGER);
  const digest = await sha256Base64Url(payloadJson);
  const latest = await queryOne(env,
    "SELECT COALESCE(MAX(version), 0) AS version FROM snapshots WHERE owner_id = ? AND store_sync_id = ?",
    [claims.sub, storeSyncId]);
  const version = Number(latest?.version || 0) + 1;
  const snapshotId = crypto.randomUUID();
  const now = new Date().toISOString();

  await transaction(env, [
    statement(
      `INSERT INTO stores (sync_id, owner_id, code, name, address, phone, is_active, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(owner_id, sync_id) DO UPDATE SET
         code = excluded.code, name = excluded.name, address = excluded.address,
         phone = excluded.phone, is_active = excluded.is_active, updated_at = excluded.updated_at`,
      [storeSyncId, claims.sub, code, name, optionalText(body.store.address, 500),
       optionalText(body.store.phone, 30), body.store.isActive === false ? 0 : 1,
       body.store.createdAt || now, body.store.updatedAt || now],
    ),
    statement(
      `INSERT INTO snapshots
       (id, owner_id, store_sync_id, device_id, version, schema_version, app_version, row_count, sha256, payload_json, sync_cursor, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [snapshotId, claims.sub, storeSyncId, claims.did, version, schemaVersion,
       appVersion, rowCount, digest, payloadJson, syncCursor, now],
    ),
    statement(
      `INSERT INTO sync_cursors (owner_id, store_sync_id, device_id, initial_snapshot_version, pull_cursor, updated_at)
       VALUES (?, ?, ?, ?, ?, ?)
       ON CONFLICT(owner_id, store_sync_id, device_id) DO UPDATE SET
         initial_snapshot_version = excluded.initial_snapshot_version,
         pull_cursor = MAX(sync_cursors.pull_cursor, excluded.pull_cursor),
         updated_at = excluded.updated_at`,
      [claims.sub, storeSyncId, claims.did, version, syncCursor, now],
    ),
    statement(
      `DELETE FROM snapshots
       WHERE owner_id = ? AND store_sync_id = ? AND version < ?`,
      [claims.sub, storeSyncId, Math.max(1, version - 2)],
    ),
  ]);

  return json({ snapshotId, version, syncCursor, sha256: digest, rowCount, createdAt: now }, 201);
}

async function downloadSnapshot(url, claims, env) {
  const storeSyncId = requiredText(url.searchParams.get("storeSyncId"), "Store sync ID", 64);
  const row = await queryOne(env,
    `SELECT id, version, schema_version, app_version, row_count, sha256, payload_json, sync_cursor, created_at
     FROM snapshots WHERE owner_id = ? AND store_sync_id = ?
     ORDER BY version DESC LIMIT 1`, [claims.sub, storeSyncId]);
  if (!row) throw new HttpError(404, "No snapshot exists for that store.");
  return json({
    snapshotId: row.id,
    version: Number(row.version),
    schemaVersion: Number(row.schema_version),
    appVersion: row.app_version,
    rowCount: Number(row.row_count),
    sha256: row.sha256,
    syncCursor: Number(row.sync_cursor || 0),
    createdAt: row.created_at,
    payload: JSON.parse(row.payload_json),
  });
}

async function pushChanges(request, claims, env) {
  const body = await readJson(request, 3_000_000);
  const storeSyncId = requiredText(body.storeSyncId, "Store sync ID", 64);
  if (!Array.isArray(body.changes) || body.changes.length === 0 || body.changes.length > MAX_SYNC_BATCH) {
    throw new HttpError(400, `Changes must contain 1 to ${MAX_SYNC_BATCH} items.`);
  }

  const results = [];
  for (const raw of body.changes) {
    const change = normalizeSyncChange(raw);
    if (change.entityType !== "Store") {
      const store = await queryOne(env,
        "SELECT sync_id FROM stores WHERE owner_id = ? AND sync_id = ?", [claims.sub, storeSyncId]);
      if (!store) throw new HttpError(404, "Store is not registered. Upload a snapshot or sync the store record first.");
    } else if (change.entitySyncId !== storeSyncId) {
      throw new HttpError(400, "A Store change must use the requested store sync ID.");
    }
    results.push(await applyPushChange(env, claims, storeSyncId, change));
  }

  return json({ storeSyncId, results });
}

function normalizeSyncChange(raw) {
  if (!raw || typeof raw !== "object") throw new HttpError(400, "Each change must be an object.");
  const changeId = requiredText(raw.changeId, "Change ID", 64);
  const entityType = requiredText(raw.entityType, "Entity type", 80);
  if (!ALLOWED_ENTITY_TYPES.has(entityType)) throw new HttpError(400, `Unsupported entity type: ${entityType}.`);
  const entitySyncId = requiredText(raw.entitySyncId, "Entity sync ID", 64);
  const operation = raw.operation === "delete" ? "delete" : raw.operation === "upsert" ? "upsert" : null;
  if (!operation) throw new HttpError(400, "Operation must be upsert or delete.");
  const entityVersion = integerInRange(raw.entityVersion, "Entity version", 1, Number.MAX_SAFE_INTEGER);
  const baseCloudVersion = integerInRange(raw.baseCloudVersion ?? 0, "Base cloud version", 0, Number.MAX_SAFE_INTEGER);
  const payload = operation === "upsert" ? raw.payload : {};
  if (operation === "upsert" && (!payload || typeof payload !== "object" || Array.isArray(payload))) {
    throw new HttpError(400, "Upsert payload must be an object.");
  }
  const payloadJson = JSON.stringify(payload || {});
  if (new TextEncoder().encode(payloadJson).byteLength > 500_000) {
    throw new HttpError(413, "A single sync record exceeds the 500 KB limit.");
  }
  return { changeId, entityType, entitySyncId, operation, entityVersion, baseCloudVersion, payload, payloadJson };
}

async function applyPushChange(env, claims, storeSyncId, change) {
  return withTransaction(env, async (tx) => {
    const prior = await tx.queryOne(
      `SELECT cloud_version, cursor FROM sync_idempotency
       WHERE owner_id = ? AND change_id = ?`, [claims.sub, change.changeId]);
    if (prior) {
      return {
        changeId: change.changeId,
        status: "accepted",
        cloudVersion: Number(prior.cloud_version),
        cursor: Number(prior.cursor),
        duplicate: true,
      };
    }

    const current = await tx.queryOne(
      `SELECT cloud_version, operation, payload_json FROM sync_records
       WHERE owner_id = ? AND store_sync_id = ? AND entity_type = ? AND entity_sync_id = ?`,
      [claims.sub, storeSyncId, change.entityType, change.entitySyncId]);
    const currentVersion = Number(current?.cloud_version || 0);
    if (change.baseCloudVersion !== currentVersion) {
      return {
        changeId: change.changeId,
        status: "conflict",
        cloudVersion: currentVersion,
        operation: current?.operation || "missing",
        payload: current?.payload_json ? JSON.parse(current.payload_json) : {},
        message: "The cloud record changed on another device.",
      };
    }

    const cloudVersion = currentVersion + 1;
    const now = new Date().toISOString();
    await tx.execute(
      `INSERT INTO sync_records
       (owner_id, store_sync_id, entity_type, entity_sync_id, cloud_version, entity_version,
        operation, payload_json, origin_device_id, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(owner_id, store_sync_id, entity_type, entity_sync_id) DO UPDATE SET
         cloud_version = excluded.cloud_version, entity_version = excluded.entity_version,
         operation = excluded.operation, payload_json = excluded.payload_json,
         origin_device_id = excluded.origin_device_id, updated_at = excluded.updated_at`,
      [claims.sub, storeSyncId, change.entityType, change.entitySyncId, cloudVersion,
       change.entityVersion, change.operation, change.payloadJson, claims.did, now]);
    await tx.execute(
      `INSERT INTO sync_changes
       (owner_id, store_sync_id, change_id, entity_type, entity_sync_id, cloud_version,
        entity_version, operation, payload_json, origin_device_id, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [claims.sub, storeSyncId, change.changeId, change.entityType, change.entitySyncId,
       cloudVersion, change.entityVersion, change.operation, change.payloadJson, claims.did, now]);
    const cursorRow = await tx.queryOne("SELECT last_insert_rowid() AS cursor");
    const cursor = Number(cursorRow?.cursor || 0);
    await tx.execute(
      `INSERT INTO sync_idempotency
       (owner_id, change_id, store_sync_id, entity_type, entity_sync_id, cloud_version, cursor, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      [claims.sub, change.changeId, storeSyncId, change.entityType, change.entitySyncId,
       cloudVersion, cursor, now]);

    if (change.entityType === "Store") {
      if (change.operation === "delete") {
        await tx.execute(
          "UPDATE stores SET is_active = 0, updated_at = ? WHERE owner_id = ? AND sync_id = ?",
          [now, claims.sub, storeSyncId]);
      } else {
        const p = change.payload;
        const code = requiredText(p.Code ?? p.code, "Store code", 24);
        const name = requiredText(p.Name ?? p.name, "Store name", 100);
        await tx.execute(
          `INSERT INTO stores (sync_id, owner_id, code, name, address, phone, is_active, created_at, updated_at)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
           ON CONFLICT(owner_id, sync_id) DO UPDATE SET
             code = excluded.code, name = excluded.name, address = excluded.address,
             phone = excluded.phone, is_active = excluded.is_active, updated_at = excluded.updated_at`,
          [storeSyncId, claims.sub, code, name, optionalText(p.Address ?? p.address, 500),
           optionalText(p.Phone ?? p.phone, 30), (p.IsActive ?? p.isActive) === false ? 0 : 1,
           p.CreatedAt ?? p.createdAt ?? now, p.UpdatedAt ?? p.updatedAt ?? now]);
      }
    }

    return { changeId: change.changeId, status: "accepted", cloudVersion, cursor, duplicate: false };
  });
}

async function pullChanges(url, claims, env) {
  const storeSyncId = requiredText(url.searchParams.get("storeSyncId"), "Store sync ID", 64);
  const after = integerInRange(Number(url.searchParams.get("after") || 0), "After cursor", 0, Number.MAX_SAFE_INTEGER);
  const limit = integerInRange(Number(url.searchParams.get("limit") || 500), "Limit", 1, MAX_PULL_LIMIT);
  const store = await queryOne(env,
    "SELECT sync_id FROM stores WHERE owner_id = ? AND sync_id = ?", [claims.sub, storeSyncId]);
  if (!store) throw new HttpError(404, "Store is not registered.");

  const rows = await query(env,
    `SELECT cursor, change_id, entity_type, entity_sync_id, cloud_version, entity_version,
            operation, payload_json, origin_device_id, created_at
     FROM sync_changes
     WHERE owner_id = ? AND store_sync_id = ? AND cursor > ?
     ORDER BY cursor LIMIT ?`, [claims.sub, storeSyncId, after, limit]);
  const changes = rows.map((row) => ({
    cursor: Number(row.cursor),
    changeId: row.change_id,
    entityType: row.entity_type,
    entitySyncId: row.entity_sync_id,
    cloudVersion: Number(row.cloud_version),
    entityVersion: Number(row.entity_version),
    operation: row.operation,
    payload: JSON.parse(row.payload_json || "{}"),
    originDeviceId: row.origin_device_id,
    createdAt: row.created_at,
  }));
  const nextCursor = changes.length ? changes[changes.length - 1].cursor : after;
  const hasMore = changes.length === limit;
  const now = new Date().toISOString();
  await execute(env,
    `INSERT INTO sync_cursors (owner_id, store_sync_id, device_id, pull_cursor, updated_at)
     VALUES (?, ?, ?, ?, ?)
     ON CONFLICT(owner_id, store_sync_id, device_id) DO UPDATE SET
       pull_cursor = MAX(sync_cursors.pull_cursor, excluded.pull_cursor), updated_at = excluded.updated_at`,
    [claims.sub, storeSyncId, claims.did, nextCursor, now]);
  return json({ storeSyncId, after, nextCursor, hasMore, changes });
}

async function authResponse(env, owner, device, refreshToken) {
  const expiresAt = new Date(Date.now() + ACCESS_TOKEN_SECONDS * 1000);
  const accessToken = await createAccessToken(env, {
    sub: owner.id,
    did: device.id,
    email: owner.email,
  }, expiresAt);
  return json({
    owner: { id: owner.id, email: owner.email, displayName: owner.display_name },
    device: { id: device.id, name: device.name },
    tokens: { accessToken, refreshToken, expiresAt: expiresAt.toISOString() },
  });
}

async function requireAccessToken(request, env) {
  const header = request.headers.get("Authorization") || "";
  if (!header.startsWith("Bearer ")) throw new HttpError(401, "Bearer token is required.");
  return verifyAccessToken(env, header.slice(7));
}

async function createAccessToken(env, claims, expiresAt) {
  const now = Math.floor(Date.now() / 1000);
  const header = base64UrlJson({ alg: "HS256", typ: "JWT" });
  const payload = base64UrlJson({
    ...claims,
    iss: "posapp-cloud",
    aud: "posapp-desktop",
    iat: now,
    exp: Math.floor(expiresAt.getTime() / 1000),
  });
  const input = `${header}.${payload}`;
  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(env.JWT_SECRET),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"],
  );
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(input));
  return `${input}.${base64UrlBytes(new Uint8Array(signature))}`;
}

async function verifyAccessToken(env, token) {
  const parts = token.split(".");
  if (parts.length !== 3) throw new HttpError(401, "Access token is invalid.");
  const input = `${parts[0]}.${parts[1]}`;
  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(env.JWT_SECRET),
    { name: "HMAC", hash: "SHA-256" }, false, ["verify"],
  );
  const valid = await crypto.subtle.verify(
    "HMAC", key, base64UrlToBytes(parts[2]), new TextEncoder().encode(input),
  );
  if (!valid) throw new HttpError(401, "Access token is invalid.");
  let claims;
  try { claims = JSON.parse(new TextDecoder().decode(base64UrlToBytes(parts[1]))); }
  catch { throw new HttpError(401, "Access token is invalid."); }
  const now = Math.floor(Date.now() / 1000);
  if (claims.iss !== "posapp-cloud" || claims.aud !== "posapp-desktop" ||
      !claims.sub || !claims.did || Number(claims.exp) <= now) {
    throw new HttpError(401, "Access token is expired or invalid.");
  }
  return claims;
}

async function hashPassword(password, salt, iterations) {
  const material = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"],
  );
  const bits = await crypto.subtle.deriveBits({
    name: "PBKDF2",
    hash: "SHA-256",
    salt: base64UrlToBytes(salt),
    iterations,
  }, material, 256);
  return base64UrlBytes(new Uint8Array(bits));
}

async function verifyPassword(password, salt, iterations, expected) {
  const actual = await hashPassword(password, salt, iterations);
  return constantTimeTextEqual(actual, expected);
}

async function enforceAuthRateLimit(request, email, env) {
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";
  const key = await sha256Base64Url(`${email}|${ip}|${env.JWT_SECRET}`);
  const windowStart = Math.floor(Date.now() / 1000 / AUTH_WINDOW_SECONDS) * AUTH_WINDOW_SECONDS;
  const now = new Date().toISOString();
  await execute(env,
    `INSERT INTO auth_rate_limits (rate_key, window_start, attempts, updated_at)
     VALUES (?, ?, 1, ?)
     ON CONFLICT(rate_key, window_start) DO UPDATE SET attempts = attempts + 1, updated_at = excluded.updated_at`,
    [key, windowStart, now]);
  const row = await queryOne(env,
    "SELECT attempts FROM auth_rate_limits WHERE rate_key = ? AND window_start = ?", [key, windowStart]);
  if (Number(row?.attempts || 0) > AUTH_ATTEMPT_LIMIT) {
    throw new HttpError(429, "Too many sign-in attempts. Try again later.");
  }
}

async function clearAuthRateLimit(request, email, env) {
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";
  const key = await sha256Base64Url(`${email}|${ip}|${env.JWT_SECRET}`);
  await execute(env, "DELETE FROM auth_rate_limits WHERE rate_key = ?", [key]);
}

function normalizeDevice(body) {
  return {
    deviceKey: requiredText(body.deviceKey, "Device key", 128),
    deviceName: requiredText(body.deviceName || "Windows POS", "Device name", 100),
    platform: requiredText(body.platform || "Windows", "Platform", 40),
    appVersion: requiredText(body.appVersion || "unknown", "App version", 40),
  };
}

function normalizeEmail(value) {
  const email = String(value || "").trim().toLowerCase();
  if (email.length < 3 || email.length > 254 || !email.includes("@")) {
    throw new HttpError(400, "A valid email address is required.");
  }
  return email;
}

function validatePassword(value) {
  const password = typeof value === "string" ? value : "";
  if (password.length < 10 || password.length > 128) {
    throw new HttpError(400, "Password must contain 10 to 128 characters.");
  }
  return password;
}

function requiredText(value, field, maximum) {
  const text = String(value || "").trim();
  if (!text) throw new HttpError(400, `${field} is required.`);
  if (text.length > maximum) throw new HttpError(400, `${field} is too long.`);
  return text;
}

function optionalText(value, maximum) {
  if (value === null || value === undefined) return null;
  const text = String(value).trim();
  if (text.length > maximum) throw new HttpError(400, "A text field is too long.");
  return text || null;
}

function integerInRange(value, field, minimum, maximum) {
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number < minimum || number > maximum) {
    throw new HttpError(400, `${field} is invalid.`);
  }
  return number;
}

async function readJson(request, maximumBytes = 1_000_000) {
  const contentLength = Number(request.headers.get("Content-Length") || 0);
  if (contentLength > maximumBytes) throw new HttpError(413, "Request body is too large.");
  const text = await request.text();
  if (new TextEncoder().encode(text).byteLength > maximumBytes) {
    throw new HttpError(413, "Request body is too large.");
  }
  try { return JSON.parse(text); }
  catch { throw new HttpError(400, "Request body must be valid JSON."); }
}

function resolveEnvironment(env) {
  let config = {};
  const combined = env.POSAPP_CLOUD_CONFIG;
  if (combined) {
    try {
      config = typeof combined === "string" ? JSON.parse(combined) : combined;
    } catch {
      throw new Error("POSAPP_CLOUD_CONFIG must be valid JSON.");
    }
    if (!config || typeof config !== "object" || Array.isArray(config)) {
      throw new Error("POSAPP_CLOUD_CONFIG must be a JSON object.");
    }
  }

  return {
    ...env,
    TURSO_DATABASE_URL: firstValue(env.TURSO_DATABASE_URL, config.TURSO_DATABASE_URL, config.tursoDatabaseUrl),
    TURSO_AUTH_TOKEN: firstValue(env.TURSO_AUTH_TOKEN, config.TURSO_AUTH_TOKEN, config.tursoAuthToken),
    JWT_SECRET: firstValue(env.JWT_SECRET, config.JWT_SECRET, config.jwtSecret),
    REGISTRATION_KEY: firstValue(env.REGISTRATION_KEY, config.REGISTRATION_KEY, config.registrationKey),
    AUTO_INITIALIZE_SCHEMA: firstValue(
      env.AUTO_INITIALIZE_SCHEMA, config.AUTO_INITIALIZE_SCHEMA, config.autoInitializeSchema, "false"),
  };
}

function firstValue(...values) {
  return values.find((value) => value !== undefined && value !== null && String(value).trim() !== "");
}

function isEnabled(value) {
  return value === true || ["1", "true", "yes", "on"].includes(String(value || "").trim().toLowerCase());
}

async function ensureCloudSchema(env) {
  if (!schemaInitializationPromise) {
    schemaInitializationPromise = transaction(
      env, CLOUD_SCHEMA_STATEMENTS.map((sql) => statement(sql)),
    ).catch((error) => {
      schemaInitializationPromise = undefined;
      throw error;
    });
  }
  await schemaInitializationPromise;
}

function validateEnvironment(env) {
  for (const name of ["TURSO_DATABASE_URL", "TURSO_AUTH_TOKEN", "JWT_SECRET", "REGISTRATION_KEY"]) {
    if (!env[name] || String(env[name]).length < (name === "JWT_SECRET" ? 32 : 1)) {
      throw new Error(`Missing or invalid Worker variable/secret: ${name}`);
    }
  }
}

async function query(env, sql, args = []) {
  const [result] = await pipeline(env, [statement(sql, args)]);
  return rowsFromResult(result);
}

async function queryOne(env, sql, args = []) {
  const rows = await query(env, sql, args);
  return rows[0] || null;
}

async function execute(env, sql, args = []) {
  const [result] = await pipeline(env, [statement(sql, args)]);
  return Number(result.affected_row_count || 0);
}

async function withTransaction(env, action) {
  const begin = await pipelineRaw(env, [{ type: "execute", stmt: { sql: "BEGIN IMMEDIATE" } }]);
  let baton = begin.data.baton;
  let baseUrl = begin.data.base_url;
  if (!baton) throw new Error("Turso did not return a transaction baton.");

  const run = async (sql, args = []) => {
    const response = await pipelineRaw(env, [toRequest(statement(sql, args))], baton, baseUrl);
    baton = response.data.baton || baton;
    baseUrl = response.data.base_url || baseUrl;
    return response.results[0].response.result;
  };
  const tx = {
    execute: async (sql, args = []) => Number((await run(sql, args)).affected_row_count || 0),
    query: async (sql, args = []) => rowsFromResult(await run(sql, args)),
    queryOne: async (sql, args = []) => (await tx.query(sql, args))[0] || null,
  };

  try {
    const value = await action(tx);
    await pipelineRaw(env,
      [{ type: "execute", stmt: { sql: "COMMIT" } }, { type: "close" }], baton, baseUrl);
    return value;
  } catch (error) {
    try {
      await pipelineRaw(env,
        [{ type: "execute", stmt: { sql: "ROLLBACK" } }, { type: "close" }], baton, baseUrl);
    } catch { /* preserve original failure */ }
    throw error;
  }
}

function rowsFromResult(result) {
  const columns = result.cols || [];
  return (result.rows || []).map((row) => Object.fromEntries(
    columns.map((column, index) => [column.name, decodeCell(row[index])]),
  ));
}

async function transaction(env, statements) {
  const begin = await pipelineRaw(env, [{ type: "execute", stmt: { sql: "BEGIN IMMEDIATE" } }]);
  const baton = begin.data.baton;
  if (!baton) throw new Error("Turso did not return a transaction baton.");
  try {
    const requests = statements.map(toRequest);
    requests.push({ type: "execute", stmt: { sql: "COMMIT" } }, { type: "close" });
    await pipelineRaw(env, requests, baton, begin.data.base_url);
  } catch (error) {
    try {
      await pipelineRaw(env,
        [{ type: "execute", stmt: { sql: "ROLLBACK" } }, { type: "close" }],
        baton, begin.data.base_url);
    } catch { /* preserve original failure */ }
    throw error;
  }
}

async function pipeline(env, statements) {
  const requests = statements.map(toRequest);
  requests.push({ type: "close" });
  const { results } = await pipelineRaw(env, requests);
  return results.slice(0, statements.length).map((item) => item.response.result);
}

async function pipelineRaw(env, requests, baton = undefined, baseUrl = undefined) {
  const root = normalizeTursoUrl(baseUrl || env.TURSO_DATABASE_URL);
  const response = await fetch(`${root}/v2/pipeline`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${env.TURSO_AUTH_TOKEN}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ ...(baton ? { baton } : {}), requests }),
  });
  if (!response.ok) throw new Error(`Turso request failed (${response.status}).`);
  const data = await response.json();
  for (const item of data.results || []) {
    if (item.type !== "ok") {
      const message = item.error?.message || item.error || "Turso query failed.";
      throw new Error(String(message));
    }
  }
  return { data, results: data.results || [] };
}

function statement(sql, args = []) { return { sql, args }; }
function toRequest(item) {
  return {
    type: "execute",
    stmt: {
      sql: item.sql,
      ...(item.args?.length ? { args: item.args.map(encodeArg) } : {}),
    },
  };
}

function encodeArg(value) {
  if (value === null || value === undefined) return { type: "null" };
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("SQL argument is not finite.");
    return { type: Number.isInteger(value) ? "integer" : "float", value: String(value) };
  }
  if (typeof value === "boolean") return { type: "integer", value: value ? "1" : "0" };
  return { type: "text", value: String(value) };
}

function decodeCell(cell) {
  if (!cell || cell.type === "null") return null;
  if (cell.type === "integer" || cell.type === "float") return Number(cell.value);
  if (cell.type === "blob") return cell.base64;
  return cell.value;
}

function normalizeTursoUrl(value) {
  let url = String(value || "").trim().replace(/\/+$/, "");
  if (url.startsWith("libsql://")) url = `https://${url.slice("libsql://".length)}`;
  if (!url.startsWith("https://")) throw new Error("TURSO_DATABASE_URL must use libsql:// or https://.");
  return url;
}

async function sha256Base64Url(value) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return base64UrlBytes(new Uint8Array(digest));
}

async function constantTimeTextEqual(left, right) {
  const a = new TextEncoder().encode(String(left));
  const b = new TextEncoder().encode(String(right));
  const length = Math.max(a.length, b.length);
  let difference = a.length ^ b.length;
  for (let i = 0; i < length; i += 1) difference |= (a[i % a.length] || 0) ^ (b[i % b.length] || 0);
  return difference === 0;
}

function randomBase64Url(length) {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  return base64UrlBytes(bytes);
}

function base64UrlJson(value) {
  return base64UrlBytes(new TextEncoder().encode(JSON.stringify(value)));
}

function base64UrlBytes(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function base64UrlToBytes(value) {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - normalized.length % 4) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
    },
  });
}

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}
