import assert from "node:assert/strict";
import worker from "../src/index.js";

const env = {
  TURSO_DATABASE_URL: "https://example.turso.io",
  TURSO_AUTH_TOKEN: "test-token",
  JWT_SECRET: "0123456789abcdef0123456789abcdef",
  REGISTRATION_KEY: "private-registration-key",
};

let record = null;
const idempotency = new Map();
const changes = [];
const originalFetch = globalThis.fetch;
globalThis.fetch = async (_url, options) => {
  const body = JSON.parse(options.body);
  const results = body.requests.map((request) => {
    if (request.type === "close") return { type: "ok", response: { type: "close" } };
    const sql = request.stmt.sql.replace(/\s+/g, " ").trim();
    const args = (request.stmt.args || []).map(decodeArg);

    if (sql === "BEGIN IMMEDIATE" || sql === "COMMIT" || sql === "ROLLBACK") {
      return executeResult([], []);
    }
    if (sql.startsWith("SELECT attempts FROM auth_rate_limits")) {
      return executeResult(["attempts"], [[integerCell(1)]]);
    }
    if (sql.startsWith("SELECT id FROM owners")) return executeResult([], []);
    if (sql.startsWith("SELECT COALESCE(MAX(version)")) {
      return executeResult(["version"], [[integerCell(0)]]);
    }
    if (sql.startsWith("SELECT sync_id FROM stores")) {
      return executeResult(["sync_id"], [[textCell("store-sync-id")]]);
    }
    if (sql.startsWith("SELECT d.id, d.name, d.platform")) {
      return executeResult(
        ["id", "name", "platform", "app_version", "created_at", "last_seen_at", "revoked_at", "store_cursor_count"],
        [
          [textCell("device-one"), textCell("Front POS"), textCell("Windows"), textCell("1.9.0"),
            textCell("2026-07-22T00:00:00.000Z"), textCell("2026-07-22T01:00:00.000Z"), nullCell(), integerCell(1)],
          [textCell("device-two"), textCell("Back POS"), textCell("Windows"), textCell("1.9.0"),
            textCell("2026-07-22T00:10:00.000Z"), textCell("2026-07-22T01:05:00.000Z"), nullCell(), integerCell(1)],
        ],
      );
    }
    if (sql.startsWith("SELECT cloud_version, cursor FROM sync_idempotency")) {
      const prior = idempotency.get(args[1]);
      return prior
        ? executeResult(["cloud_version", "cursor"], [[integerCell(prior.cloudVersion), integerCell(prior.cursor)]])
        : executeResult([], []);
    }
    if (sql.startsWith("SELECT cloud_version, operation, payload_json FROM sync_records")) {
      return record
        ? executeResult(
            ["cloud_version", "operation", "payload_json"],
            [[integerCell(record.cloudVersion), textCell(record.operation), textCell(record.payloadJson)]],
          )
        : executeResult([], []);
    }
    if (sql.startsWith("INSERT INTO sync_records")) {
      record = {
        cloudVersion: Number(args[4]),
        operation: args[6],
        payloadJson: args[7],
      };
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO sync_changes")) {
      changes.push({
        cursor: changes.length + 1,
        changeId: args[2],
        entityType: args[3],
        entitySyncId: args[4],
        cloudVersion: Number(args[5]),
        entityVersion: Number(args[6]),
        operation: args[7],
        payloadJson: args[8],
        originDeviceId: args[9],
        createdAt: args[10],
      });
      return executeResult([], [], 1);
    }
    if (sql.startsWith("INSERT INTO sync_idempotency")) {
      idempotency.set(args[1], { cloudVersion: Number(args[5]), cursor: Number(args[6]) });
      return executeResult([], [], 1);
    }
    if (sql.startsWith("SELECT last_insert_rowid() AS cursor")) {
      return executeResult(["cursor"], [[integerCell(changes.length)]]);
    }
    if (sql.startsWith("SELECT cursor, change_id, entity_type")) {
      const after = Number(args[2]);
      const limit = Number(args[3]);
      const rows = changes.filter((x) => x.cursor > after).slice(0, limit).map((x) => [
        integerCell(x.cursor), textCell(x.changeId), textCell(x.entityType), textCell(x.entitySyncId),
        integerCell(x.cloudVersion), integerCell(x.entityVersion), textCell(x.operation), textCell(x.payloadJson),
        textCell(x.originDeviceId), textCell(x.createdAt),
      ]);
      return executeResult(
        ["cursor", "change_id", "entity_type", "entity_sync_id", "cloud_version",
          "entity_version", "operation", "payload_json", "origin_device_id", "created_at"],
        rows,
      );
    }
    return executeResult([], [], sql.startsWith("SELECT") ? 0 : 1);
  });

  const startingTransaction = body.requests.some((x) => x.stmt?.sql === "BEGIN IMMEDIATE");
  return new Response(JSON.stringify({
    baton: startingTransaction ? "test-baton" : (body.baton || null),
    base_url: null,
    results,
  }), { status: 200, headers: { "Content-Type": "application/json" } });
};

try {
  const health = await worker.fetch(new Request("https://worker.test/v1/health"), env);
  assert.equal(health.status, 200);
  assert.equal((await health.json()).version, "1.9.0");

  const signup = await worker.fetch(new Request("https://worker.test/v1/auth/signup", {
    method: "POST",
    headers: { "Content-Type": "application/json", "CF-Connecting-IP": "127.0.0.1" },
    body: JSON.stringify({
      email: "owner@example.com",
      password: "correct-horse-battery",
      displayName: "Owner",
      registrationKey: env.REGISTRATION_KEY,
      deviceKey: "device-key-1234567890",
      deviceName: "Test POS",
      platform: "Windows",
      appVersion: "1.9.0",
    }),
  }), env);
  assert.equal(signup.status, 200);
  const auth = await signup.json();
  assert.ok(auth.tokens.accessToken);
  assert.ok(auth.tokens.refreshToken);

  const claims = JSON.parse(Buffer.from(auth.tokens.accessToken.split(".")[1], "base64url").toString("utf8"));
  const deviceOneToken = await createTestToken({ ...claims, did: "device-one" });
  const deviceTwoToken = await createTestToken({ ...claims, did: "device-two" });
  const deviceOneAuth = { Authorization: `Bearer ${deviceOneToken}` };
  const deviceTwoAuth = { Authorization: `Bearer ${deviceTwoToken}` };

  const upload = await worker.fetch(new Request("https://worker.test/v1/sync/snapshot/upload", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...deviceOneAuth },
    body: JSON.stringify({
      store: { syncId: "store-sync-id", code: "MAIN", name: "Main Store", isActive: true },
      schemaVersion: 4,
      appVersion: "1.9.0",
      syncCursor: 0,
      rowCount: 1,
      payload: { schemaVersion: 4, store: { SyncId: "store-sync-id" }, entities: {} },
    }),
  }), env);
  assert.equal(upload.status, 201);

  const firstPushBody = {
    storeSyncId: "store-sync-id",
    changes: [{
      changeId: "change-1",
      entityType: "Product",
      entitySyncId: "product-1",
      operation: "upsert",
      entityVersion: 2,
      baseCloudVersion: 0,
      payload: { Name: "Tea" },
    }],
  };
  const firstPush = await push(firstPushBody, deviceOneAuth);
  assert.equal(firstPush.results[0].status, "accepted");
  assert.equal(firstPush.results[0].cloudVersion, 1);

  const duplicate = await push(firstPushBody, deviceOneAuth);
  assert.equal(duplicate.results[0].duplicate, true);

  const secondPush = await push({
    ...firstPushBody,
    changes: [{ ...firstPushBody.changes[0], changeId: "change-2", baseCloudVersion: 1, payload: { Name: "Coffee" } }],
  }, deviceTwoAuth);
  assert.equal(secondPush.results[0].status, "accepted");
  assert.equal(secondPush.results[0].cloudVersion, 2);

  const conflict = await push({
    ...firstPushBody,
    changes: [{ ...firstPushBody.changes[0], changeId: "change-3", baseCloudVersion: 1, payload: { Name: "Juice" } }],
  }, deviceOneAuth);
  assert.equal(conflict.results[0].status, "conflict");
  assert.equal(conflict.results[0].cloudVersion, 2);
  assert.equal(conflict.results[0].operation, "upsert");
  assert.equal(conflict.results[0].payload.Name, "Coffee");

  const pull = await worker.fetch(new Request(
    "https://worker.test/v1/sync/pull?storeSyncId=store-sync-id&after=1&limit=100",
    { headers: deviceOneAuth },
  ), env);
  const pulled = await pull.json();
  assert.equal(pulled.nextCursor, 2);
  assert.equal(pulled.changes[0].payload.Name, "Coffee");
  assert.equal(pulled.changes[0].originDeviceId, "device-two");

  const transferPush = await push({
    storeSyncId: "store-sync-id",
    changes: [{
      changeId: "change-transfer",
      entityType: "StockTransfer",
      entitySyncId: "transfer-1",
      operation: "upsert",
      entityVersion: 1,
      baseCloudVersion: 2,
      payload: { TransferNumber: "TR-MAIN-20260722-0001", DestinationStoreSyncId: "store-two", Status: 1 },
    }],
  }, deviceOneAuth);
  assert.equal(transferPush.results[0].status, "accepted");

  const transferItemPush = await push({
    storeSyncId: "store-sync-id",
    changes: [{
      changeId: "change-transfer-item",
      entityType: "StockTransferItem",
      entitySyncId: "transfer-item-1",
      operation: "upsert",
      entityVersion: 1,
      baseCloudVersion: 3,
      payload: { StockTransferSyncId: "transfer-1", ProductSyncId: "product-1", Quantity: 5 },
    }],
  }, deviceOneAuth);
  assert.equal(transferItemPush.results[0].status, "accepted");

  const transferPull = await worker.fetch(new Request(
    "https://worker.test/v1/sync/pull?storeSyncId=store-sync-id&after=2&limit=100",
    { headers: deviceTwoAuth },
  ), env);
  const transferChanges = (await transferPull.json()).changes;
  assert.deepEqual(transferChanges.map((x) => x.entityType), ["StockTransfer", "StockTransferItem"]);
  assert.ok(transferChanges.every((x) => !("ImagePath" in x.payload)));

  const devices = await worker.fetch(new Request("https://worker.test/v1/devices", { headers: deviceOneAuth }), env);
  assert.equal(devices.status, 200);
  const deviceRows = (await devices.json()).devices;
  assert.equal(deviceRows.length, 2);
  assert.equal(deviceRows.filter((x) => x.isCurrent).length, 1);

  console.log("PosApp cloud Worker multi-device smoke test passed.");
} finally {
  globalThis.fetch = originalFetch;
}

async function push(body, authorization) {
  const response = await worker.fetch(new Request("https://worker.test/v1/sync/push", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authorization },
    body: JSON.stringify(body),
  }), env);
  assert.equal(response.status, 200);
  return response.json();
}

async function createTestToken(claims) {
  const header = encodeBase64Url(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const payload = encodeBase64Url(JSON.stringify(claims));
  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(env.JWT_SECRET), { name: "HMAC", hash: "SHA-256" }, false, ["sign"],
  );
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${header}.${payload}`));
  return `${header}.${payload}.${encodeBase64Url(new Uint8Array(signature))}`;
}

function encodeBase64Url(value) {
  const buffer = typeof value === "string" ? Buffer.from(value) : Buffer.from(value);
  return buffer.toString("base64url");
}

function executeResult(columns, rows, affected = 0) {
  return {
    type: "ok",
    response: {
      type: "execute",
      result: {
        cols: columns.map((name) => ({ name, decltype: null })),
        rows,
        affected_row_count: affected,
        last_insert_rowid: null,
      },
    },
  };
}

function decodeArg(cell) {
  if (!cell || cell.type === "null") return null;
  if (cell.type === "integer" || cell.type === "float") return Number(cell.value);
  return cell.value;
}
function integerCell(value) { return { type: "integer", value: String(value) }; }
function textCell(value) { return { type: "text", value: String(value) }; }
function nullCell() { return { type: "null", value: null }; }
