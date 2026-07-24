import assert from "node:assert/strict";
import worker from "../src/index.js";

const registrationKey = "private-registration-key";
const jwtSecret = "0123456789abcdef0123456789abcdef";
const env = {
  POSAPP_CLOUD_CONFIG: JSON.stringify({
    tursoDatabaseUrl: "https://example.turso.io",
    tursoAuthToken: "test-token",
    jwtSecret,
    registrationKey,
    autoInitializeSchema: true,
  }),
};

const records = new Map();
const idempotency = new Map();
const changes = [];
const stores = new Set();
const snapshots = [];
let capturedOwnerPasswordIterations = null;
const originalFetch = globalThis.fetch;
globalThis.fetch = async (_url, options) => {
  const body = JSON.parse(options.body);
  const results = body.requests.map((request) => {
    if (request.type === "close") return { type: "ok", response: { type: "close" } };
    const sql = request.stmt.sql.replace(/\s+/g, " ").trim();
    const args = (request.stmt.args || []).map(decodeArg);
    if (["BEGIN IMMEDIATE", "COMMIT", "ROLLBACK"].includes(sql)) return executeResult([], []);
    if (sql.startsWith("PRAGMA table_info(")) {
      const table = sql.match(/PRAGMA table_info\(([^)]+)\)/i)?.[1];
      const columns = {
        snapshots: ["backup_set_id", "captured_at"],
        sync_changes: ["operation_id"],
        sync_idempotency: ["operation_id"],
      }[table] || [];
      return executeResult(["name"], columns.map((x) => [textCell(x)]));
    }
    if (sql.startsWith("SELECT attempts FROM auth_rate_limits")) return executeResult(["attempts"], [[integerCell(1)]]);
    if (sql.startsWith("SELECT id FROM owners")) return executeResult([], []);
    if (sql.startsWith("SELECT id FROM devices WHERE id =")) return executeResult(["id"], [[textCell(args[0])]]);
    if (sql.startsWith("SELECT sync_id FROM stores")) {
      return stores.has(args[1]) ? executeResult(["sync_id"], [[textCell(args[1])]]) : executeResult([], []);
    }
    if (sql.startsWith("SELECT COALESCE(MAX(version)")) {
      const versions = snapshots.filter((x) => x.ownerId === args[0] && x.storeSyncId === args[1]).map((x) => x.version);
      return executeResult(["version"], [[integerCell(versions.length ? Math.max(...versions) : 0)]]);
    }
    if (sql.startsWith("SELECT backup_set_id, captured_at, COUNT")) {
      const bySet = new Map();
      for (const snap of snapshots.filter((x) => x.ownerId === args[0])) {
        if (!bySet.has(snap.backupSetId)) bySet.set(snap.backupSetId, { capturedAt: snap.capturedAt, stores: new Set() });
        bySet.get(snap.backupSetId).stores.add(snap.storeSyncId);
      }
      const best = [...bySet.entries()].filter(([, x]) => x.stores.size === stores.size)
        .sort((a, b) => b[1].capturedAt.localeCompare(a[1].capturedAt))[0];
      return best ? executeResult(["backup_set_id", "captured_at", "store_count"],
        [[textCell(best[0]), textCell(best[1].capturedAt), integerCell(best[1].stores.size)]]) : executeResult([], []);
    }
    if (sql.startsWith("SELECT id, backup_set_id, captured_at, version")) {
      let rows;
      if (sql.includes("backup_set_id = ?")) rows = snapshots.filter((x) => x.ownerId === args[0] && x.backupSetId === args[1]);
      else rows = snapshots.filter((x) => x.ownerId === args[0] && x.storeSyncId === args[1]).sort((a,b) => b.version-a.version).slice(0,1);
      return executeResult(
        ["id", "backup_set_id", "captured_at", "version", "schema_version", "app_version", "row_count", "sha256", "payload_json", "sync_cursor", "created_at"],
        rows.map((x) => [textCell(x.id), textCell(x.backupSetId), textCell(x.capturedAt), integerCell(x.version),
          integerCell(x.schemaVersion), textCell(x.appVersion), integerCell(x.rowCount), textCell(x.sha256),
          textCell(x.payloadJson), integerCell(x.syncCursor), textCell(x.createdAt)]));
    }
    if (sql.startsWith("SELECT d.id, d.name, d.platform")) {
      return executeResult(
        ["id", "name", "platform", "app_version", "created_at", "last_seen_at", "revoked_at", "store_cursor_count"],
        [
          [textCell("device-one"), textCell("Front POS"), textCell("Windows"), textCell("1.10.6"), textCell("2026-07-24T00:00:00.000Z"), textCell("2026-07-24T01:00:00.000Z"), nullCell(), integerCell(1)],
          [textCell("device-two"), textCell("Back POS"), textCell("Windows"), textCell("1.10.6"), textCell("2026-07-24T00:10:00.000Z"), textCell("2026-07-24T01:05:00.000Z"), nullCell(), integerCell(1)],
        ]);
    }
    if (sql.startsWith("SELECT cloud_version, cursor, operation_id FROM sync_idempotency")) {
      const prior = idempotency.get(args[1]);
      return prior ? executeResult(["cloud_version", "cursor", "operation_id"],
        [[integerCell(prior.cloudVersion), integerCell(prior.cursor), textCell(prior.operationId)]]) : executeResult([], []);
    }
    if (sql.startsWith("SELECT cloud_version, operation, payload_json FROM sync_records")) {
      const rec = records.get(recordKey(args[1], args[2], args[3]));
      return rec ? executeResult(["cloud_version", "operation", "payload_json"],
        [[integerCell(rec.cloudVersion), textCell(rec.operation), textCell(rec.payloadJson)]]) : executeResult([], []);
    }
    if (sql.startsWith("SELECT cloud_version, entity_version, operation, payload_json")) {
      const rec = records.get(recordKey(args[1], args[2], args[3]));
      return rec ? executeResult(["cloud_version", "entity_version", "operation", "payload_json", "origin_device_id", "updated_at"],
        [[integerCell(rec.cloudVersion), integerCell(rec.entityVersion), textCell(rec.operation), textCell(rec.payloadJson), textCell(rec.originDeviceId), textCell(rec.updatedAt)]]) : executeResult([], []);
    }
    if (sql.startsWith("INSERT INTO owners")) {
      capturedOwnerPasswordIterations = Number(args[5]);
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO stores")) {
      stores.add(args[0]);
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO snapshots")) {
      snapshots.push({ id: args[0], ownerId: args[1], storeSyncId: args[2], version: Number(args[4]),
        schemaVersion: Number(args[5]), appVersion: args[6], rowCount: Number(args[7]), sha256: args[8],
        payloadJson: args[9], syncCursor: Number(args[10]), backupSetId: args[11], capturedAt: args[12], createdAt: args[13] });
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO sync_records")) {
      records.set(recordKey(args[1], args[2], args[3]), { cloudVersion: Number(args[4]), entityVersion: Number(args[5]),
        operation: args[6], payloadJson: args[7], originDeviceId: args[8], updatedAt: args[9] });
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO sync_changes")) {
      changes.push({ cursor: changes.length + 1, changeId: args[2], operationId: args[3], entityType: args[4],
        entitySyncId: args[5], cloudVersion: Number(args[6]), entityVersion: Number(args[7]), operation: args[8],
        payloadJson: args[9], originDeviceId: args[10], createdAt: args[11], storeSyncId: args[1] });
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO sync_idempotency")) {
      idempotency.set(args[1], { operationId: args[2], cloudVersion: Number(args[6]), cursor: Number(args[7]) });
      return executeResult([], [], 1);
    }
    if (sql.startsWith("SELECT last_insert_rowid() AS cursor")) return executeResult(["cursor"], [[integerCell(changes.length)]]);
    if (sql.startsWith("SELECT cursor, change_id, operation_id")) {
      const after = Number(args[2]);
      const limit = Number(args[3]);
      const rows = changes.filter((x) => x.storeSyncId === args[1] && x.cursor > after).slice(0, limit).map((x) => [
        integerCell(x.cursor), textCell(x.changeId), textCell(x.operationId), textCell(x.entityType), textCell(x.entitySyncId),
        integerCell(x.cloudVersion), integerCell(x.entityVersion), textCell(x.operation), textCell(x.payloadJson),
        textCell(x.originDeviceId), textCell(x.createdAt),
      ]);
      return executeResult(["cursor", "change_id", "operation_id", "entity_type", "entity_sync_id", "cloud_version",
        "entity_version", "operation", "payload_json", "origin_device_id", "created_at"], rows);
    }
    return executeResult([], [], sql.startsWith("SELECT") ? 0 : 1);
  });
  const startingTransaction = body.requests.some((x) => x.stmt?.sql === "BEGIN IMMEDIATE");
  return new Response(JSON.stringify({ baton: startingTransaction ? "test-baton" : (body.baton || null), base_url: null, results }),
    { status: 200, headers: { "Content-Type": "application/json" } });
};

try {
  const health = await worker.fetch(new Request("https://worker.test/v1/health"), env);
  assert.equal(health.status, 200);
  assert.equal((await health.json()).version, "1.10.6");
  const unknown = await worker.fetch(new Request("https://worker.test/"), env);
  assert.equal(unknown.status, 404);
  const malformed = await worker.fetch(new Request("https://worker.test/v1/account", { headers: { Authorization: "Bearer broken" } }), env);
  assert.equal(malformed.status, 401);

  const signup = await worker.fetch(new Request("https://worker.test/v1/auth/signup", {
    method: "POST", headers: { "Content-Type": "application/json", "CF-Connecting-IP": "127.0.0.1" },
    body: JSON.stringify({ email: "owner@example.com", password: "correct-horse-battery", displayName: "Owner",
      registrationKey, deviceKey: "device-key-1234567890", deviceName: "Test POS", platform: "Windows", appVersion: "1.10.6" }),
  }), env);
  assert.equal(signup.status, 200);
  assert.equal(capturedOwnerPasswordIterations, 100000);
  const auth = await signup.json();
  const claims = JSON.parse(Buffer.from(auth.tokens.accessToken.split(".")[1], "base64url").toString("utf8"));
  const deviceOneAuth = { Authorization: `Bearer ${await createTestToken({ ...claims, did: "device-one" })}` };
  const deviceTwoAuth = { Authorization: `Bearer ${await createTestToken({ ...claims, did: "device-two" })}` };

  const payload = { schemaVersion: 5, appVersion: "1.10.6", store: { SyncId: "store-sync-id" }, entities: {} };
  const upload = await worker.fetch(new Request("https://worker.test/v1/sync/snapshot/upload", {
    method: "POST", headers: { "Content-Type": "application/json", ...deviceOneAuth }, body: JSON.stringify({
      backupSetId: "backup-set-1", capturedAt: "2026-07-24T01:00:00Z",
      store: { syncId: "store-sync-id", code: "MAIN", name: "Main Store", isActive: true },
      schemaVersion: 5, appVersion: "1.10.6", syncCursor: 0, rowCount: 1, payload,
    }),
  }), env);
  assert.equal(upload.status, 201);
  const latestSet = await worker.fetch(new Request("https://worker.test/v1/sync/snapshot/set/latest", { headers: deviceOneAuth }), env);
  assert.equal(latestSet.status, 200);
  assert.equal((await latestSet.json()).snapshots.length, 1);

  const firstPushBody = { storeSyncId: "store-sync-id", operationId: "operation-1", changes: [{
    changeId: "change-1", operationId: "operation-1", entityType: "Product", entitySyncId: "product-1",
    operation: "upsert", entityVersion: 2, baseCloudVersion: 0, payload: { Name: "Tea" },
  }] };
  const firstPush = await push(firstPushBody, deviceOneAuth);
  assert.equal(firstPush.committed, true);
  assert.equal(firstPush.results[0].cloudVersion, 1);
  assert.equal((await push(firstPushBody, deviceOneAuth)).results[0].duplicate, true);

  const secondPush = await push({ ...firstPushBody, operationId: "operation-2", changes: [{ ...firstPushBody.changes[0],
    operationId: "operation-2", changeId: "change-2", baseCloudVersion: 1, payload: { Name: "Coffee" } }] }, deviceTwoAuth);
  assert.equal(secondPush.committed, true);
  assert.equal(secondPush.results[0].cloudVersion, 2);

  const atomicConflict = await push({ storeSyncId: "store-sync-id", operationId: "operation-3", changes: [
    { ...firstPushBody.changes[0], operationId: "operation-3", changeId: "change-3", baseCloudVersion: 1, payload: { Name: "Juice" } },
    { changeId: "change-4", operationId: "operation-3", entityType: "Customer", entitySyncId: "customer-1",
      operation: "upsert", entityVersion: 1, baseCloudVersion: 0, payload: { Name: "Customer" } },
  ] }, deviceOneAuth);
  assert.equal(atomicConflict.committed, false);
  assert.equal(atomicConflict.results[0].status, "conflict");
  assert.equal(atomicConflict.results[1].status, "blocked");
  assert.equal(records.has(recordKey("store-sync-id", "Customer", "customer-1")), false);

  const pull = await worker.fetch(new Request("https://worker.test/v1/sync/pull?storeSyncId=store-sync-id&after=1&limit=100",
    { headers: deviceOneAuth }), env);
  const pulled = await pull.json();
  assert.equal(pulled.nextCursor, 2);
  assert.equal(pulled.changes[0].operationId, "operation-2");
  assert.equal(pulled.changes[0].payload.Name, "Coffee");

  const recordResponse = await worker.fetch(new Request(
    "https://worker.test/v1/sync/record?storeSyncId=store-sync-id&entityType=Product&entitySyncId=product-1",
    { headers: deviceOneAuth }), env);
  assert.equal((await recordResponse.json()).cloudVersion, 2);

  const devices = await worker.fetch(new Request("https://worker.test/v1/devices", { headers: deviceOneAuth }), env);
  assert.equal(devices.status, 200);
  assert.equal((await devices.json()).devices.length, 2);
  const logout = await worker.fetch(new Request("https://worker.test/v1/auth/logout", {
    method: "POST", headers: { "Content-Type": "application/json", ...deviceOneAuth },
    body: JSON.stringify({ refreshToken: auth.tokens.refreshToken }),
  }), env);
  assert.equal(logout.status, 200);

  console.log("PosApp cloud Worker v1.10.6 atomic-sync smoke test passed.");
} finally {
  globalThis.fetch = originalFetch;
}

async function push(body, authorization) {
  const response = await worker.fetch(new Request("https://worker.test/v1/sync/push", {
    method: "POST", headers: { "Content-Type": "application/json", ...authorization }, body: JSON.stringify(body),
  }), env);
  assert.equal(response.status, 200, JSON.stringify(await response.clone().json()));
  return response.json();
}
async function createTestToken(claims) {
  const header = encodeBase64Url(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const payload = encodeBase64Url(JSON.stringify(claims));
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(jwtSecret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${header}.${payload}`));
  return `${header}.${payload}.${encodeBase64Url(new Uint8Array(signature))}`;
}
function recordKey(store, type, id) { return `${store}|${type}|${id}`; }
function encodeBase64Url(value) { return Buffer.from(value).toString("base64url"); }
function executeResult(columns, rows, affected = 0) {
  return { type: "ok", response: { type: "execute", result: {
    cols: columns.map((name) => ({ name, decltype: null })), rows, affected_row_count: affected, last_insert_rowid: null,
  } } };
}
function decodeArg(cell) {
  if (!cell || cell.type === "null") return null;
  if (cell.type === "integer" || cell.type === "float") return Number(cell.value);
  return cell.value;
}
function integerCell(value) { return { type: "integer", value: String(value) }; }
function textCell(value) { return { type: "text", value: String(value) }; }
function nullCell() { return { type: "null", value: null }; }
