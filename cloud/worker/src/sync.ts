import type { Client, Row, Transaction } from "@libsql/client/web";
import { assertStore, insertAudit } from "./auth";
import { ApiError, jsonResponse } from "./errors";
import { database, integer, nowIso, nullableText, text } from "./db";
import { hasPermission, requireEntityWrite, requirePermission } from "./permissions";
import { validateRecordPayload, validateSyncPush, validateUuid } from "./validation";
import { assertMigrationWriteAccess } from "./migrations";
import type {
  AuthContext,
  Env,
  ServerRecord,
  SyncOperation,
  SyncOperationResult,
} from "./types";

export const ENTITY_TABLES: Readonly<Record<string, string>> = {
  categories: "categories",
  product_units: "product_units",
  products: "products",
  customers: "customers",
  suppliers: "suppliers",
  users: "user_sync_records",
  discounts: "discounts",
  promotions: "promotions",
  taxes: "taxes",
  settings: "application_settings",
  sales: "sales",
  sale_items: "sale_items",
  payments: "payments",
  refunds: "refunds",
  voided_sales: "voided_sales",
  open_sales: "open_sales",
  purchases: "purchases",
  purchase_items: "purchase_items",
  inventory_adjustments: "inventory_adjustments",
  inventory_movements: "inventory_movements",
  expenses: "expenses",
  register_sessions: "register_sessions",
  cash_movements: "cash_movements",
};

const GLOBAL_ENTITIES = new Set(["users"]);
const IMMUTABLE_ENTITIES = new Set([
  "sale_items", "payments", "refunds", "voided_sales", "purchase_items",
  "inventory_adjustments", "inventory_movements", "expenses", "cash_movements",
]);
const COUNT_ENTITIES = Object.keys(ENTITY_TABLES);
const BUSINESS_COUNT_ENTITIES = COUNT_ENTITIES.filter((entityType) => entityType !== "users");
type CompositionEntity = "sales" | "purchases";

interface CompositionCompletion {
  entityType: CompositionEntity;
  recordId: string;
  version: number;
  cursor: number;
}

export async function push(context: AuthContext, env: Env, body: unknown): Promise<Response> {
  requirePermission(context.claims, "sync.use");
  const pushBody = validateSyncPush(body, env);
  assertSchemaCompatibility(pushBody.clientSchemaVersion, env);
  if (pushBody.deviceId !== context.claims.did)
    throw new ApiError(403, "DEVICE_MISMATCH", "The device identity does not match this session.");

  const client = database(env);
  let transaction: Transaction | undefined;
  try {
    await assertStore(client, context.claims.tid, pushBody.storeId,
      context.claims.sub, context.claims.role === "admin");
    transaction = await client.transaction("write");
    const migrationAuthorized = await assertMigrationWriteAccess(
      transaction, context, pushBody.storeId,
    );
    const results: SyncOperationResult[] = [];
    // A suspended sale is deliberately mutable until it is saved again or
    // completed. Keep that authorization scoped to this atomic push batch so
    // replacing its draft lines cannot be used to rewrite an older completed
    // financial transaction.
    const editableSaleIds = new Set<string>();
    const editablePurchaseIds = new Set<string>();
    let lastCursor = 0;

    for (let operationIndex = 0; operationIndex < pushBody.operations.length; operationIndex++) {
      const operation = pushBody.operations[operationIndex]!;
      // A batch may legitimately contain one rejected record and several valid
      // records. Isolate every operation with a SQLite savepoint so a late
      // constraint/audit failure cannot leave that rejected record, its change
      // cursor, or its audit entry partially applied in Turso.
      const savepoint = `sync_operation_${operationIndex}`;
      await transaction.execute(`SAVEPOINT ${savepoint}`);
      try {
        const result = await applyOperation(
          transaction, context, operation, pushBody.storeId, pushBody.deviceId,
          editableSaleIds, editablePurchaseIds, migrationAuthorized,
        );
        await transaction.execute(`RELEASE SAVEPOINT ${savepoint}`);
        results.push(result.result);
        lastCursor = Math.max(lastCursor, result.cursor);
      } catch (error) {
        // libSQL's web client sends a pipeline for each batch. Keep recovery in
        // one external subrequest so rejected records also remain inexpensive
        // beneath the Workers Free subrequest ceiling.
        await transaction.batch([
          `ROLLBACK TO SAVEPOINT ${savepoint}`,
          `RELEASE SAVEPOINT ${savepoint}`,
        ]);
        const apiError = error instanceof ApiError
          ? error
          : error instanceof Error && /unique|constraint/i.test(error.message)
            ? new ApiError(409, "DUPLICATE_BUSINESS_RECORD", "A record with that immutable business identifier already exists.")
            : null;
        if (!apiError) throw error;
        results.push({
          operationId: operation.operationId,
          recordId: operation.recordId,
          accepted: false,
          duplicate: false,
          serverVersion: 0,
          errorCode: apiError.code,
          message: apiError.message,
        });
      }
    }

    await transaction.batch([
      {
        sql: `UPDATE registered_devices SET last_sync_at_utc = ?, updated_at_utc = ?
              WHERE id = ? AND tenant_id = ?`,
        args: [nowIso(), nowIso(), context.claims.did, context.claims.tid],
      },
      {
        sql: "UPDATE login_sessions SET last_seen_at_utc = ? WHERE id = ? AND tenant_id = ?",
        args: [nowIso(), context.claims.sid, context.claims.tid],
      },
    ]);
    await transaction.commit();
    return jsonResponse({ results, serverCursor: lastCursor, requestId: context.requestId }, 200, context.requestId);
  } catch (error) {
    if (transaction) {
      try { await transaction.rollback(); } catch { /* original error wins */ }
    }
    throw error;
  } finally {
    transaction?.close();
    client.close();
  }
}

export async function pull(context: AuthContext, env: Env, url: URL): Promise<Response> {
  requirePermission(context.claims, "sync.use");
  const cursor = Number(url.searchParams.get("cursor") ?? "0");
  const storeId = validateUuid(url.searchParams.get("storeId"), "storeId");
  const limit = Math.min(200, Math.max(1, Number(url.searchParams.get("limit") ?? "100")));
  if (!Number.isSafeInteger(cursor) || cursor < 0 || !Number.isInteger(limit))
    throw new ApiError(400, "INVALID_SYNC_CURSOR", "The synchronization cursor is invalid.");

  const client = database(env);
  try {
    await assertStore(client, context.claims.tid, storeId,
      context.claims.sub, context.claims.role === "admin");
    const result = await client.execute({
      sql: `SELECT change.cursor, change.entity_type, change.record_id, change.store_id,
                   change.version, change.updated_at_utc, change.deleted_at_utc,
                   change.last_modified_device_id, change.payload_json
            FROM sync_changes AS change
            WHERE change.tenant_id = ? AND change.cursor > ?
              AND (change.store_id IS NULL OR change.store_id = ?)
              AND NOT EXISTS (
                SELECT 1 FROM sync_transaction_compositions AS composition
                WHERE composition.tenant_id = change.tenant_id
                  AND composition.store_id = change.store_id
                  AND (
                    (change.entity_type = composition.entity_type
                      AND change.record_id = composition.record_id
                      AND (composition.status = 'pending'
                        OR change.cursor < composition.completion_cursor))
                    OR
                    (composition.entity_type = 'sales'
                      AND change.entity_type IN ('sale_items', 'payments')
                      AND json_extract(change.payload_json, '$.saleRecordId') = composition.record_id
                      AND (composition.status = 'pending'
                        OR change.cursor <= composition.completion_cursor))
                    OR
                    (composition.entity_type = 'purchases'
                      AND change.entity_type = 'purchase_items'
                      AND json_extract(change.payload_json, '$.purchaseDocumentRecordId') = composition.record_id
                      AND (composition.status = 'pending'
                        OR change.cursor <= composition.completion_cursor))
                  )
              )
            ORDER BY change.cursor LIMIT ?`,
      args: [context.claims.tid, cursor, storeId, limit + 1],
    });
    const hasMore = result.rows.length > limit;
    const rows = result.rows.slice(0, limit);
    const changes = rows.map((row) => ({
      cursor: integer(row.cursor), entityType: text(row.entity_type), recordId: text(row.record_id),
      storeId: nullableText(row.store_id) ?? "", version: integer(row.version),
      updatedAtUtc: text(row.updated_at_utc), deletedAtUtc: nullableText(row.deleted_at_utc),
      lastModifiedDeviceId: text(row.last_modified_device_id), payload: safeJsonObject(row.payload_json),
    }));
    const nextCursor = changes.length ? changes[changes.length - 1]!.cursor : cursor;
    await client.batch([
      {
        sql: `INSERT INTO device_sync_cursors
              (tenant_id, store_id, device_id, cursor, last_pull_at_utc)
              VALUES (?, ?, ?, ?, ?)
              ON CONFLICT(tenant_id, store_id, device_id) DO UPDATE SET
                cursor = MAX(device_sync_cursors.cursor, excluded.cursor),
                last_pull_at_utc = excluded.last_pull_at_utc`,
        args: [context.claims.tid, storeId, context.claims.did, nextCursor, nowIso()],
      },
      {
        sql: "UPDATE registered_devices SET last_sync_at_utc = ?, updated_at_utc = ? WHERE id = ? AND tenant_id = ?",
        args: [nowIso(), nowIso(), context.claims.did, context.claims.tid],
      },
    ], "write");
    return jsonResponse({ changes, nextCursor, hasMore, requestId: context.requestId }, 200, context.requestId);
  } finally { client.close(); }
}

export async function syncStatus(context: AuthContext, env: Env, url: URL): Promise<Response> {
  requirePermission(context.claims, "sync.use");
  const requestedStoreId = url.searchParams.get("storeId");
  const storeId = requestedStoreId == null ? null : validateUuid(requestedStoreId, "storeId");
  const client = database(env);
  try {
    if (storeId)
      await assertStore(client, context.claims.tid, storeId,
        context.claims.sub, context.claims.role === "admin");
    const statements = COUNT_ENTITIES.map((entityType) => ({
      sql: `SELECT ? AS entity_type, COUNT(*) AS count FROM ${ENTITY_TABLES[entityType]}
            WHERE tenant_id = ? AND deleted_at_utc IS NULL`,
      args: [entityType, context.claims.tid],
    }));
    const results = await client.batch(statements, "read");
    const counts: Record<string, number> = {};
    for (const result of results) {
      const row = result.rows[0];
      if (row) counts[text(row.entity_type)] = integer(row.count);
    }
    const cursorResult = await client.execute({
      sql: "SELECT COALESCE(MAX(cursor), 0) AS cursor FROM sync_changes WHERE tenant_id = ?",
      args: [context.claims.tid],
    });
    const migration = storeId == null ? null : await client.execute({
      sql: `SELECT user_id, device_id FROM migration_sessions
            WHERE tenant_id = ? AND store_id = ? AND status = 'active' AND expires_at_utc > ? LIMIT 1`,
      args: [context.claims.tid, storeId, nowIso()],
    });
    const migrationRow = migration?.rows[0];
    const latestDeviceMigration = storeId == null ? null : await client.execute({
      sql: `SELECT id, status, expires_at_utc FROM migration_sessions
            WHERE tenant_id = ? AND store_id = ? AND user_id = ? AND device_id = ?
            ORDER BY started_at_utc DESC LIMIT 1`,
      args: [context.claims.tid, storeId, context.claims.sub, context.claims.did],
    });
    const latestDeviceMigrationRow = latestDeviceMigration?.rows[0];
    const businessDataEmpty = BUSINESS_COUNT_ENTITIES.every((entityType) => (counts[entityType] ?? 0) === 0);
    return jsonResponse({
      businessDataEmpty,
      counts,
      serverCursor: integer(cursorResult.rows[0]?.cursor),
      apiVersion: Number(env.API_VERSION ?? "1"),
      schemaVersion: Number(env.SCHEMA_VERSION ?? "4"),
      initialMigrationInProgress: migrationRow != null,
      initialMigrationOwnedByDevice: migrationRow != null &&
        text(migrationRow.user_id) === context.claims.sub && text(migrationRow.device_id) === context.claims.did,
      initialMigrationCompletedByDevice: latestDeviceMigrationRow != null &&
        text(latestDeviceMigrationRow.status) === "completed",
      initialMigrationResumableByDevice: latestDeviceMigrationRow != null &&
        (text(latestDeviceMigrationRow.status) === "expired" ||
         (text(latestDeviceMigrationRow.status) === "active" &&
          Date.parse(text(latestDeviceMigrationRow.expires_at_utc)) <= Date.now())),
      latestDeviceMigrationId: latestDeviceMigrationRow == null ? null : text(latestDeviceMigrationRow.id),
      requestId: context.requestId,
    }, 200, context.requestId);
  } finally { client.close(); }
}

async function applyOperation(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  selectedStoreId: string,
  deviceId: string,
  editableSaleIds: Set<string>,
  editablePurchaseIds: Set<string>,
  migrationAuthorized: boolean,
): Promise<{ result: SyncOperationResult; cursor: number }> {
  const table = ENTITY_TABLES[operation.entityType];
  if (!table) throw new ApiError(400, "UNSUPPORTED_ENTITY", "This record type cannot be synchronized.");
  requireOperationWrite(context, operation);
  const storeId = GLOBAL_ENTITIES.has(operation.entityType) ? null : selectedStoreId;
  validateOperationScope(context.claims.tid, operation.storeId, storeId);
  if (operation.operation === "upsert") {
    validateRecordPayload(operation.entityType, operation.payload);
    if (operation.entityType === "products") delete operation.payload.stockQuantity;
    if (operation.entityType === "inventory_movements") delete operation.payload.balanceAfter;
  }

  // These independent reads share one HTTP pipeline. Besides reducing latency,
  // this bounds Turso external subrequests for a two-operation push.
  const [duplicate, existingResult] = await transaction.batch([
    {
      sql: `SELECT operation_id, idempotency_key, record_id, result_version, status
            FROM sync_operations
            WHERE tenant_id = ? AND (operation_id = ? OR idempotency_key = ?) LIMIT 1`,
      args: [context.claims.tid, operation.operationId, operation.idempotencyKey],
    },
    {
      sql: `SELECT id, tenant_id, store_id, payload_json, version, created_at_utc, updated_at_utc,
                   deleted_at_utc, created_by_user_id, updated_by_user_id, last_modified_device_id
            FROM ${table} WHERE id = ? LIMIT 1`,
      args: [operation.recordId],
    },
  ]);
  if (!duplicate || !existingResult)
    throw new Error("The database returned an incomplete synchronization read batch.");
  if (duplicate.rows[0]) {
    const row = duplicate.rows[0];
    if (text(row.operation_id) !== operation.operationId || text(row.record_id) !== operation.recordId)
      throw new ApiError(409, "IDEMPOTENCY_KEY_REUSED", "The idempotency key was already used for another operation.");
    return {
      result: {
        operationId: operation.operationId, recordId: operation.recordId, accepted: true,
        duplicate: true, serverVersion: integer(row.result_version),
      },
      cursor: 0,
    };
  }

  const existing = existingResult.rows[0] as unknown as ServerRecord | undefined;
  validateOperationScope(context.claims.tid, operation.storeId, storeId,
    existing ? text(existing.tenant_id) : undefined,
    existing ? nullableText(existing.store_id) : undefined);
  if (operation.operation === "upsert")
    await validateRelatedRecords(
      transaction, context, operation, storeId, editableSaleIds, editablePurchaseIds,
      migrationAuthorized,
    );
  const currentVersion = existing ? integer(existing.version) : 0;
  if (currentVersion !== operation.baseVersion) {
    return {
      result: {
        operationId: operation.operationId, recordId: operation.recordId, accepted: false,
        duplicate: false, serverVersion: currentVersion, errorCode: "VERSION_CONFLICT",
        message: "The record was changed by another device.",
        serverPayload: existing ? safeJsonObject(existing.payload_json) : {},
        serverStoreId: existing ? nullableText(existing.store_id) : null,
        serverUpdatedAtUtc: existing ? text(existing.updated_at_utc) : undefined,
        serverDeletedAtUtc: existing ? nullableText(existing.deleted_at_utc) : null,
        serverLastModifiedDeviceId: existing ? text(existing.last_modified_device_id) : undefined,
      },
      cursor: 0,
    };
  }
  if (!existing && operation.operation === "delete")
    throw new ApiError(404, "RECORD_NOT_FOUND", "The record does not exist.");
  const existingPayload = existing ? safeJsonObject(existing.payload_json) : {};
  let draftLineEdit = operation.entityType === "sale_items" &&
    isEditableSaleLine(existingPayload, editableSaleIds);
  if (existing && operation.entityType === "sale_items" && !draftLineEdit) {
    const currentSaleId = typeof existingPayload.saleRecordId === "string"
      ? existingPayload.saleRecordId
      : null;
    const requestedSaleId = operation.operation === "upsert" && typeof operation.payload.saleRecordId === "string"
      ? operation.payload.saleRecordId
      : currentSaleId;
    draftLineEdit = currentSaleId != null && currentSaleId === requestedSaleId &&
      await isSuspendedSale(transaction, context.claims.tid, storeId, currentSaleId);
  }
  if (existing && operation.operation === "delete" && !draftLineEdit &&
      (IMMUTABLE_ENTITIES.has(operation.entityType) || operation.entityType === "sales" || operation.entityType === "purchases"))
    throw new ApiError(409, "IMMUTABLE_TRANSACTION", "Finalized financial records must be corrected with a reversal, refund, void, or adjustment.");
  if (existing && operation.operation === "upsert" && !draftLineEdit)
    enforceImmutableTransition(operation.entityType, existingPayload, operation.payload, editableSaleIds);

  const stageComposition = operation.operation === "upsert" &&
    shouldStageFinancialComposition(operation.entityType, existingPayload, operation.payload, existing != null);

  const timestamp = nowIso();
  const nextVersion = currentVersion + 1;
  const payloadJson = operation.operation === "delete"
    ? text(existing!.payload_json)
    : JSON.stringify(operation.payload);
  const deletedAt = operation.operation === "delete" ? timestamp : null;

  if (!existing) {
    await transaction.execute({
      sql: `INSERT INTO ${table}
            (id, tenant_id, store_id, payload_json, created_at_utc, updated_at_utc, deleted_at_utc,
             version, created_by_user_id, updated_by_user_id, last_modified_device_id)
            VALUES (?, ?, ?, ?, ?, ?, NULL, 1, ?, ?, ?)`,
      args: [operation.recordId, context.claims.tid, storeId, payloadJson, timestamp, timestamp,
        context.claims.sub, context.claims.sub, deviceId],
    });
  } else {
    const update = await transaction.execute({
      sql: `UPDATE ${table} SET payload_json = ?, updated_at_utc = ?, deleted_at_utc = ?,
                   version = version + 1, updated_by_user_id = ?, last_modified_device_id = ?
            WHERE id = ? AND tenant_id = ? AND version = ?`,
      args: [payloadJson, timestamp, deletedAt, context.claims.sub, deviceId,
        operation.recordId, context.claims.tid, operation.baseVersion],
    });
    if (update.rowsAffected !== 1)
      throw new ApiError(409, "VERSION_CONFLICT", "The record changed while the batch was being processed.");
  }

  if (stageComposition) {
    const compositionEntity = operation.entityType as CompositionEntity;
    await transaction.execute({
      sql: `INSERT INTO sync_transaction_compositions
            (tenant_id, store_id, entity_type, record_id, expected_item_count,
             expected_payment_count, status, created_at_utc, completed_at_utc, completion_cursor)
            VALUES (?, ?, ?, ?, ?, ?, 'pending', ?, NULL, NULL)
            ON CONFLICT(tenant_id, entity_type, record_id) DO UPDATE SET
              store_id = excluded.store_id,
              expected_item_count = excluded.expected_item_count,
              expected_payment_count = excluded.expected_payment_count,
              status = 'pending', completed_at_utc = NULL, completion_cursor = NULL`,
      args: [context.claims.tid, storeId, compositionEntity, operation.recordId,
        serverExpectedCount(operation.payload, "expectedItemCount"),
        compositionEntity === "sales"
          ? serverExpectedCount(operation.payload, "expectedPaymentCount")
          : 0,
        timestamp],
    });
  }
  const financialHeaderPending = stageComposition ||
    (operation.operation === "upsert" &&
      (operation.entityType === "sales" || operation.entityType === "purchases") &&
      await hasPendingComposition(
        transaction, context.claims.tid, storeId!, operation.entityType, operation.recordId));

  if (operation.entityType === "sales" && operation.operation === "upsert" &&
      (!existing || Number(existingPayload.status) === 1))
    editableSaleIds.add(operation.recordId);
  if (operation.entityType === "purchases" && operation.operation === "upsert" && !existing)
    editablePurchaseIds.add(operation.recordId);

  const change = await transaction.execute({
    sql: `INSERT INTO sync_changes
          (tenant_id, store_id, entity_type, record_id, version, updated_at_utc, deleted_at_utc,
           last_modified_device_id, payload_json)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) RETURNING cursor`,
    args: [context.claims.tid, storeId, operation.entityType, operation.recordId, nextVersion,
      timestamp, deletedAt, deviceId, payloadJson],
  });
  let cursor = integer(change.rows[0]?.cursor);
  await transaction.batch([
    {
      sql: `INSERT INTO sync_operations
            (tenant_id, operation_id, idempotency_key, device_id, store_id, entity_type, record_id,
             operation, base_version, result_version, status, created_at_utc, applied_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'accepted', ?, ?)`,
      args: [context.claims.tid, operation.operationId, operation.idempotencyKey, deviceId, storeId,
        operation.entityType, operation.recordId, operation.operation, operation.baseVersion,
        nextVersion, operation.clientTimestampUtc, timestamp],
    },
    {
      sql: `INSERT INTO audit_logs
            (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action,
             affected_type, affected_id, request_id, metadata_json)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      args: [crypto.randomUUID(), context.claims.tid, storeId, context.claims.sub, deviceId, timestamp,
        financialHeaderPending ? stagedAuditAction(operation) : auditAction(operation),
        operation.entityType, operation.recordId, context.requestId,
        JSON.stringify({ operationId: operation.operationId, version: nextVersion })],
    },
  ]);
  const completion = operation.operation === "upsert"
    ? await tryCompleteFinancialComposition(transaction, context, operation, storeId!, deviceId)
    : null;
  let resultVersion = nextVersion;
  if (completion != null) {
    cursor = Math.max(cursor, completion.cursor);
    if (completion.entityType === operation.entityType && completion.recordId === operation.recordId) {
      resultVersion = completion.version;
      await transaction.execute({
        sql: `UPDATE sync_operations SET result_version = ?
              WHERE tenant_id = ? AND operation_id = ?`,
        args: [resultVersion, context.claims.tid, operation.operationId],
      });
    }
  }
  return {
    result: {
      operationId: operation.operationId, recordId: operation.recordId,
      accepted: true, duplicate: false, serverVersion: resultVersion,
    },
    cursor,
  };
}

export function shouldStageFinancialComposition(
  entityType: string,
  current: Record<string, unknown>,
  next: Record<string, unknown>,
  exists: boolean,
): boolean {
  if (entityType === "purchases") return !exists;
  if (entityType !== "sales" || Number(next.status) === 1) return false;
  return !exists || Number(current.status) === 1;
}

export function isCompositionCursorPublishable(
  cursor: number,
  status: "pending" | "complete",
  completionCursor: number | null,
  parentChange: boolean,
): boolean {
  if (status === "pending" || completionCursor == null) return false;
  return parentChange ? cursor >= completionCursor : cursor > completionCursor;
}

async function hasPendingComposition(
  transaction: Transaction,
  tenantId: string,
  storeId: string,
  entityType: CompositionEntity,
  recordId: string,
): Promise<boolean> {
  const result = await transaction.execute({
    sql: `SELECT 1 FROM sync_transaction_compositions
          WHERE tenant_id = ? AND store_id = ? AND entity_type = ? AND record_id = ?
            AND status = 'pending' LIMIT 1`,
    args: [tenantId, storeId, entityType, recordId],
  });
  return result.rows[0] != null;
}

async function tryCompleteFinancialComposition(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  storeId: string,
  deviceId: string,
): Promise<CompositionCompletion | null> {
  const parent = compositionParent(operation);
  if (parent == null) return null;

  const staged = await transaction.execute({
    sql: `SELECT expected_item_count, expected_payment_count
          FROM sync_transaction_compositions
          WHERE tenant_id = ? AND store_id = ? AND entity_type = ? AND record_id = ?
            AND status = 'pending' LIMIT 1`,
    args: [context.claims.tid, storeId, parent.entityType, parent.recordId],
  });
  const stagedRow = staged.rows[0];
  if (stagedRow == null) return null;

  const expectedItems = integer(stagedRow.expected_item_count);
  const expectedPayments = integer(stagedRow.expected_payment_count);
  const parentTable = parent.entityType;
  const parentResult = await transaction.execute({
    sql: `SELECT id, payload_json, version FROM ${parentTable}
          WHERE id = ? AND tenant_id = ? AND store_id = ? AND deleted_at_utc IS NULL LIMIT 1`,
    args: [parent.recordId, context.claims.tid, storeId],
  });
  const parentRow = parentResult.rows[0];
  if (parentRow == null)
    throw new ApiError(409, "SYNC_DEPENDENCY_MISSING", "The staged financial header is unavailable.");

  const itemTable = parent.entityType === "sales" ? "sale_items" : "purchase_items";
  const itemRelationship = parent.entityType === "sales" ? "saleRecordId" : "purchaseDocumentRecordId";
  const items = await relatedEnvelopeRows(
    transaction, itemTable, itemRelationship, parent.recordId, context.claims.tid, storeId,
  );
  const payments = parent.entityType === "sales"
    ? await relatedEnvelopeRows(
      transaction, "payments", "saleRecordId", parent.recordId, context.claims.tid, storeId,
    )
    : [];

  if (items.length < expectedItems || payments.length < expectedPayments) return null;
  if (items.length !== expectedItems || payments.length !== expectedPayments)
    throw new ApiError(409, "IMMUTABLE_TRANSACTION",
      "The uploaded financial composition exceeds the immutable counts declared by its header.");

  const parentPayload = safeJsonObject(parentRow.payload_json);
  const itemPayloads = items.map((row) => safeJsonObject(row.payload_json));
  if (parent.entityType === "sales") {
    validateSaleLineTotals(parentPayload, itemPayloads);
    validateCompletePaymentTotals(parentPayload, payments.map((row) => safeJsonObject(row.payload_json)));
  } else {
    validatePurchaseTotals(parentPayload, itemPayloads);
  }

  const completedAt = nowIso();
  const parentVersion = integer(parentRow.version) + 1;
  const update = await transaction.execute({
    sql: `UPDATE ${parentTable}
          SET version = version + 1, updated_at_utc = ?, updated_by_user_id = ?,
              last_modified_device_id = ?
          WHERE id = ? AND tenant_id = ? AND store_id = ? AND version = ?`,
    args: [completedAt, context.claims.sub, deviceId, parent.recordId, context.claims.tid,
      storeId, parentVersion - 1],
  });
  if (update.rowsAffected !== 1)
    throw new ApiError(409, "VERSION_CONFLICT", "The financial header changed while it was being completed.");

  const headerChange = await transaction.execute({
    sql: `INSERT INTO sync_changes
          (tenant_id, store_id, entity_type, record_id, version, updated_at_utc,
           deleted_at_utc, last_modified_device_id, payload_json)
          VALUES (?, ?, ?, ?, ?, ?, NULL, ?, ?) RETURNING cursor`,
    args: [context.claims.tid, storeId, parent.entityType, parent.recordId, parentVersion,
      completedAt, deviceId, text(parentRow.payload_json)],
  });
  const completionCursor = integer(headerChange.rows[0]?.cursor);
  await transaction.execute({
    sql: `UPDATE sync_transaction_compositions
          SET status = 'complete', completed_at_utc = ?, completion_cursor = ?
          WHERE tenant_id = ? AND store_id = ? AND entity_type = ? AND record_id = ?
            AND status = 'pending'`,
    args: [completedAt, completionCursor, context.claims.tid, storeId,
      parent.entityType, parent.recordId],
  });

  let cursor = completionCursor;
  cursor = Math.max(cursor, await republishCompositionRows(
    transaction, context.claims.tid, storeId, itemTable, itemRelationship, parent.recordId,
  ));
  if (parent.entityType === "sales") {
    cursor = Math.max(cursor, await republishCompositionRows(
      transaction, context.claims.tid, storeId, "payments", "saleRecordId", parent.recordId,
    ));
  }
  await insertAudit(transaction, context, completedCompositionAuditAction(parent.entityType, parentPayload),
    parent.entityType, parent.recordId,
    { expectedItemCount: expectedItems, expectedPaymentCount: expectedPayments, parentVersion },
    storeId);
  return { entityType: parent.entityType, recordId: parent.recordId, version: parentVersion, cursor };
}

function compositionParent(
  operation: SyncOperation,
): { entityType: CompositionEntity; recordId: string } | null {
  if (operation.entityType === "sales")
    return { entityType: "sales", recordId: operation.recordId };
  if (operation.entityType === "purchases")
    return { entityType: "purchases", recordId: operation.recordId };
  if (operation.entityType === "sale_items" || operation.entityType === "payments") {
    const recordId = operation.payload.saleRecordId;
    return typeof recordId === "string" ? { entityType: "sales", recordId } : null;
  }
  if (operation.entityType === "purchase_items") {
    const recordId = operation.payload.purchaseDocumentRecordId;
    return typeof recordId === "string" ? { entityType: "purchases", recordId } : null;
  }
  return null;
}

async function relatedEnvelopeRows(
  transaction: Transaction,
  table: "sale_items" | "payments" | "purchase_items",
  relationship: "saleRecordId" | "purchaseDocumentRecordId",
  parentId: string,
  tenantId: string,
  storeId: string,
): Promise<Row[]> {
  const result = await transaction.execute({
    sql: `SELECT id, version, updated_at_utc, deleted_at_utc,
                 last_modified_device_id, payload_json
          FROM ${table}
          WHERE tenant_id = ? AND store_id = ? AND deleted_at_utc IS NULL
            AND json_extract(payload_json, ?) = ?
          ORDER BY created_at_utc, id`,
    args: [tenantId, storeId, `$.${relationship}`, parentId],
  });
  return [...result.rows];
}

async function republishCompositionRows(
  transaction: Transaction,
  tenantId: string,
  storeId: string,
  entityType: "sale_items" | "payments" | "purchase_items",
  relationship: "saleRecordId" | "purchaseDocumentRecordId",
  parentId: string,
): Promise<number> {
  const result = await transaction.execute({
    sql: `INSERT INTO sync_changes
          (tenant_id, store_id, entity_type, record_id, version, updated_at_utc,
           deleted_at_utc, last_modified_device_id, payload_json)
          SELECT tenant_id, store_id, ?, id, version, updated_at_utc,
                 deleted_at_utc, last_modified_device_id, payload_json
          FROM ${entityType}
          WHERE tenant_id = ? AND store_id = ? AND deleted_at_utc IS NULL
            AND json_extract(payload_json, ?) = ?
          ORDER BY created_at_utc, id
          RETURNING cursor`,
    args: [entityType, tenantId, storeId, `$.${relationship}`, parentId],
  });
  return result.rows.reduce((maximum, row) => Math.max(maximum, integer(row.cursor)), 0);
}

function validateCompletePaymentTotals(
  sale: Record<string, unknown>,
  payments: Record<string, unknown>[],
): void {
  const expected = serverExpectedCount(sale, "expectedPaymentCount");
  if (payments.length !== expected)
    throw new ApiError(409, "SYNC_DEPENDENCY_MISSING",
      "The sale cannot be published until all declared payments are synchronized.");
  const total = Number(sale.subtotal) - Number(sale.discountTotal) +
    Number(sale.taxTotal) + Number(sale.rounding);
  const paid = payments.reduce((sum, payment) => sum + Number(payment.amount), 0);
  if (!moneyEqual(paid, total))
    throw new ApiError(409, "PAYMENT_TOTAL_EXCEEDED",
      "The synchronized payments do not reconcile exactly to the immutable sale total.");
}

function validatePurchaseTotals(
  purchase: Record<string, unknown>,
  items: Record<string, unknown>[],
): void {
  const subtotal = items.reduce((sum, item) =>
    sum + Number(item.quantity) * Number(item.unitCost), 0);
  const tax = items.reduce((sum, item) =>
    sum + Number(item.quantity) * Number(item.unitCost) * Number(item.taxRate) / 100, 0);
  if (!moneyEqual(subtotal, Number(purchase.subtotal)) ||
      !moneyEqual(tax, Number(purchase.taxTotal)) ||
      !moneyEqual(subtotal + tax, Number(purchase.total)))
    throw new ApiError(400, "VALIDATION_ERROR",
      "The declared purchase totals do not reconcile to its immutable lines.");
}

function stagedAuditAction(operation: SyncOperation): string {
  return operation.entityType === "sales" ? "sale.sync_staged" : "purchase.sync_staged";
}

function completedCompositionAuditAction(
  entityType: CompositionEntity,
  payload: Record<string, unknown>,
): string {
  if (entityType === "purchases")
    return Number(payload.status) === 1 ? "purchase.voided" : "purchase.completed";
  const status = Number(payload.status);
  if (status === 2) return "sale.voided";
  if (status === 3) return "sale.refunded";
  return "sale.completed";
}

export function enforceImmutableTransition(
  entityType: string,
  current: Record<string, unknown>,
  next: Record<string, unknown>,
  editableSaleIds: ReadonlySet<string> = new Set<string>(),
): void {
  if (IMMUTABLE_ENTITIES.has(entityType) &&
      !(entityType === "sale_items" && current.saleRecordId === next.saleRecordId &&
        isEditableSaleLine(next, editableSaleIds)))
    throw new ApiError(409, "IMMUTABLE_TRANSACTION", "This finalized transaction cannot be overwritten.");
  if (entityType === "sales") {
    const previousStatus = Number(current.status);
    const nextStatus = Number(next.status);
    if (previousStatus === 1) return; // suspended/open sale may be completed or edited
    if (previousStatus === nextStatus && samePayloadExcept(current, next, ["updatedAt"])) return;
    const validCorrection = previousStatus === 0 && [2, 3].includes(nextStatus) &&
      samePayloadExcept(current, next, ["status", "updatedAt"]);
    if (!validCorrection)
      throw new ApiError(409, "IMMUTABLE_TRANSACTION", "A completed sale can only be voided or refunded without rewriting its financial values.");
  }
  if (entityType === "purchases") {
    const validVoid = Number(current.status) === 0 && Number(next.status) === 1 &&
      samePayloadExcept(current, next, ["status", "updatedAt"]);
    if (!validVoid && JSON.stringify(current) !== JSON.stringify(next))
      throw new ApiError(409, "IMMUTABLE_TRANSACTION", "A posted purchase can only be voided; use inventory adjustments for corrections.");
  }
  if (entityType === "register_sessions" && current.closedAt != null && JSON.stringify(current) !== JSON.stringify(next))
    throw new ApiError(409, "IMMUTABLE_TRANSACTION", "A closed register session cannot be overwritten.");
}

function isEditableSaleLine(
  payload: Record<string, unknown>,
  editableSaleIds: ReadonlySet<string>,
): boolean {
  return typeof payload.saleRecordId === "string" && editableSaleIds.has(payload.saleRecordId);
}

async function isSuspendedSale(
  transaction: Transaction,
  tenantId: string,
  storeId: string | null,
  saleId: string,
): Promise<boolean> {
  const result = await transaction.execute({
    sql: `SELECT payload_json FROM sales
          WHERE id = ? AND tenant_id = ? AND
                (store_id = ? OR (store_id IS NULL AND ? IS NULL)) AND deleted_at_utc IS NULL`,
    args: [saleId, tenantId, storeId, storeId],
  });
  return result.rows[0] != null && Number(safeJsonObject(result.rows[0].payload_json).status) === 1;
}

export function validateOperationScope(
  authenticatedTenantId: string,
  requestedStoreId: string | null | undefined,
  selectedStoreId: string | null,
  existingTenantId?: string,
  existingStoreId?: string | null,
): void {
  if (existingTenantId != null && existingTenantId !== authenticatedTenantId)
    throw new ApiError(403, "CROSS_TENANT_ACCESS_DENIED", "The record belongs to another organization.");
  if ((requestedStoreId ?? null) !== selectedStoreId)
    throw new ApiError(403, "STORE_ACCESS_DENIED", "A record cannot be written to another store.");
  if (existingTenantId != null && (existingStoreId ?? null) !== selectedStoreId)
    throw new ApiError(403, "STORE_ACCESS_DENIED", "The record belongs to another store.");
}

export function requireOperationWrite(context: AuthContext, operation: SyncOperation): void {
  if (operation.entityType === "sales") {
    const status = Number(operation.payload.status);
    if (status === 2) return requirePermission(context.claims, "sales.void");
    if (status === 3) return requirePermission(context.claims, "sales.refund");
  }
  if (operation.entityType === "inventory_movements") {
    const movementType = Number(operation.payload.type);
    if (movementType === 0 && hasPermission(context.claims, "sales.create") &&
        typeof operation.payload.saleRecordId === "string") return;
    if (movementType === 1 && hasPermission(context.claims, "sales.refund") &&
        typeof operation.payload.saleRecordId === "string") return;
    if (movementType === 2 && hasPermission(context.claims, "purchases.manage") &&
        typeof operation.payload.purchaseDocumentRecordId === "string" &&
        typeof operation.payload.purchaseItemRecordId === "string") return;
  }
  requireEntityWrite(context.claims, operation.entityType);
}

function samePayloadExcept(
  left: Record<string, unknown>,
  right: Record<string, unknown>,
  ignored: string[],
): boolean {
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  for (const key of ignored) keys.delete(key);
  return [...keys].every((key) =>
    JSON.stringify(left[key] ?? null) === JSON.stringify(right[key] ?? null));
}

function safeJsonObject(value: unknown): Record<string, unknown> {
  try {
    const parsed = JSON.parse(text(value));
    return parsed != null && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
  } catch { return {}; }
}

async function validateRelatedRecords(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  storeId: string | null,
  editableSaleIds: ReadonlySet<string>,
  editablePurchaseIds: ReadonlySet<string>,
  migrationAuthorized: boolean,
): Promise<void> {
  const tenantId = context.claims.tid;
  const payload = operation.payload;
  const requireStoreRecord = (table: string, recordId: string, label: string) =>
    requireRelatedRecord(transaction, table, recordId, tenantId, storeId, label);
  const requireUser = async (name: string, required: boolean): Promise<void> => {
    const id = relationshipId(payload, name, required);
    if (id) await requireRelatedRecord(transaction, "user_sync_records", id, tenantId, null, name);
  };

  if (operation.entityType === "products") {
    const categoryId = relationshipId(payload, "categoryRecordId", false);
    if (categoryId) await requireStoreRecord("categories", categoryId, "categoryRecordId");
  } else if (operation.entityType === "sales") {
    const customerId = relationshipId(payload, "customerRecordId", false);
    if (customerId) await requireStoreRecord("customers", customerId, "customerRecordId");
    const sessionId = relationshipId(payload, "cashSessionRecordId", false);
    if (sessionId) await requireStoreRecord("register_sessions", sessionId, "cashSessionRecordId");
    const refundedId = relationshipId(payload, "refundedSaleRecordId", false);
    if (refundedId) await requireStoreRecord("sales", refundedId, "refundedSaleRecordId");
    await requireUser("userRecordId", true);
    if (Number(payload.status) !== 1) {
      const current = await transaction.execute({
        sql: `SELECT payload_json FROM sales
              WHERE id = ? AND tenant_id = ? AND
                    (store_id = ? OR (store_id IS NULL AND ? IS NULL)) AND deleted_at_utc IS NULL`,
        args: [operation.recordId, tenantId, storeId, storeId],
      });
      if (current.rows[0] != null && Number(safeJsonObject(current.rows[0].payload_json).status) === 1)
        await validateCompleteSaleLines(transaction, operation.recordId, tenantId, storeId!, payload);
    }
  } else if (operation.entityType === "sale_items") {
    const saleId = relationshipId(payload, "saleRecordId", true)!;
    const sale = await requireStoreRecord("sales", saleId, "saleRecordId");
    await validateSaleItemCapacity(
      transaction, context, operation, storeId!, saleId, sale, editableSaleIds,
    );
    const productId = relationshipId(payload, "productRecordId", true)!;
    const product = await requireStoreRecord("products", productId, "productRecordId");
    const promotionId = relationshipId(payload, "promotionRecordId", false);
    if (promotionId) await requireStoreRecord("discounts", promotionId, "promotionRecordId");
    const refundedItemId = relationshipId(payload, "refundedSaleItemRecordId", false);
    const refundedItem = refundedItemId
      ? await requireStoreRecord("sale_items", refundedItemId, "refundedSaleItemRecordId")
      : null;
    await validateSaleLineSource(
      transaction, context, operation, storeId!, productId, product, refundedItemId, refundedItem,
      migrationAuthorized,
    );
  } else if (operation.entityType === "payments") {
    const saleId = relationshipId(payload, "saleRecordId", true)!;
    const sale = await requireStoreRecord("sales", saleId, "saleRecordId");
    await validatePaymentLimit(transaction, context, operation, storeId!, saleId, sale);
  } else if (operation.entityType === "purchases") {
    const supplierId = relationshipId(payload, "supplierRecordId", false);
    if (supplierId) await requireStoreRecord("suppliers", supplierId, "supplierRecordId");
    await requireUser("userRecordId", true);
  } else if (operation.entityType === "purchase_items") {
    const purchaseId = relationshipId(payload, "purchaseDocumentRecordId", true)!;
    const purchase = await requireStoreRecord("purchases", purchaseId, "purchaseDocumentRecordId");
    await validatePurchaseItemCapacity(
      transaction, context, operation, storeId!, purchaseId, purchase, editablePurchaseIds,
    );
    await requireStoreRecord("products", relationshipId(payload, "productRecordId", true)!, "productRecordId");
  } else if (operation.entityType === "inventory_movements") {
    await requireStoreRecord("products", relationshipId(payload, "productRecordId", true)!, "productRecordId");
    await validateInventorySource(transaction, context, payload, storeId!);
    await requireUser("userRecordId", false);
  } else if (operation.entityType === "register_sessions") {
    await requireUser("openedByUserRecordId", true);
    await requireUser("closedByUserRecordId", payload.closedAt != null);
  } else if (operation.entityType === "cash_movements") {
    await requireStoreRecord("register_sessions", relationshipId(payload, "cashSessionRecordId", true)!,
      "cashSessionRecordId");
    await requireUser("userRecordId", true);
  } else if (operation.entityType === "expenses") {
    await requireUser("userRecordId", true);
  }
}

async function validateSaleItemCapacity(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  storeId: string,
  saleId: string,
  saleRow: Row,
  editableSaleIds: ReadonlySet<string>,
): Promise<void> {
  const sale = safeJsonObject(saleRow.payload_json);
  if (Number(sale.status) === 1) return;
  const expected = serverExpectedCount(sale, "expectedItemCount");
  const existing = await relatedPayloads(
    transaction, "sale_items", "saleRecordId", saleId,
    context.claims.tid, storeId, operation.recordId,
  );
  if (existing.length >= expected)
    throw new ApiError(409, "IMMUTABLE_TRANSACTION",
      "Lines cannot be appended to a finalized sale; use a refund, void, or correction record.");
  if (!editableSaleIds.has(saleId) && existing.length + 1 > expected)
    throw new ApiError(409, "IMMUTABLE_TRANSACTION", "The sale already has its declared immutable lines.");
  if (existing.length + 1 === expected)
    validateSaleLineTotals(sale, [...existing, operation.payload]);
}

async function validateSaleLineSource(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  storeId: string,
  productId: string,
  productRow: Row,
  refundedItemId: string | null,
  refundedItemRow: Row | null,
  migrationAuthorized: boolean,
): Promise<void> {
  const line = operation.payload;
  if (line.legacyPriceSnapshot === true) {
    if (context.claims.role !== "admin" || !migrationAuthorized)
      throw new ApiError(403, "PERMISSION_DENIED",
        "Legacy financial price snapshots require this device's active initial-migration lease.");
    return;
  }

  if (Number(line.quantity) < 0) {
    if (!refundedItemId || !refundedItemRow)
      throw new ApiError(400, "VALIDATION_ERROR", "A refund line must identify its original sale line.");
    const original = safeJsonObject(refundedItemRow.payload_json);
    const returned = Math.abs(Number(line.quantity));
    const sold = Number(original.quantity);
    const proportionalDiscount = sold === 0
      ? Number.NaN
      : Number(original.discountAmount) * returned / sold;
    if (original.productRecordId !== productId ||
        !moneyEqual(Number(line.unitPrice), Number(original.unitPrice)) ||
        !moneyEqual(Number(line.costPrice), Number(original.costPrice)) ||
        !moneyEqual(Number(line.taxRate), Number(original.taxRate)) ||
        !moneyEqual(Math.abs(Number(line.discountAmount)), proportionalDiscount))
      throw new ApiError(400, "VALIDATION_ERROR",
        "The refund price, cost, tax, or discount does not match its original immutable line.");
    const prior = await transaction.execute({
      sql: `SELECT payload_json FROM sale_items
            WHERE tenant_id = ? AND store_id = ? AND id <> ? AND deleted_at_utc IS NULL
              AND json_extract(payload_json, '$.refundedSaleItemRecordId') = ?`,
      args: [context.claims.tid, storeId, operation.recordId, refundedItemId],
    });
    const alreadyReturned = prior.rows.reduce((sum, row) =>
      sum + Math.abs(Number(safeJsonObject(row.payload_json).quantity)), 0);
    if (!Number.isFinite(sold) || sold <= 0 || alreadyReturned + returned > sold + 0.0001)
      throw new ApiError(409, "REFUND_QUANTITY_EXCEEDED",
        "The cumulative refund quantity exceeds the original sale line.");
    return;
  }

  const requestedVersion = Number(line.catalogVersion);
  let catalog: Record<string, unknown> | null = null;
  if (requestedVersion === integer(productRow.version)) {
    catalog = safeJsonObject(productRow.payload_json);
  } else {
    const historical = await transaction.execute({
      sql: `SELECT payload_json FROM sync_changes
            WHERE tenant_id = ? AND store_id = ? AND entity_type = 'products'
              AND record_id = ? AND version = ?
            ORDER BY cursor DESC LIMIT 1`,
      args: [context.claims.tid, storeId, productId, requestedVersion],
    });
    if (historical.rows[0]) catalog = safeJsonObject(historical.rows[0].payload_json);
  }
  if (!catalog)
    throw new ApiError(409, "CATALOG_VERSION_UNAVAILABLE",
      "The product version used by this offline sale is no longer available.");

  if (!moneyEqual(Number(line.unitPrice), Number(catalog.price)) ||
      !moneyEqual(Number(line.costPrice), Number(catalog.costPrice)) ||
      !moneyEqual(Number(line.taxRate), Number(catalog.taxRate)) ||
      Number(line.unit) !== Number(catalog.effectiveUnit ?? catalog.unit) ||
      (Number(line.discountAmount) > 0 && catalog.allowDiscount !== true))
    throw new ApiError(400, "VALIDATION_ERROR",
      "The sale line does not match its authoritative product price, cost, tax, unit, or discount policy.");
}

async function validateCompleteSaleLines(
  transaction: Transaction,
  saleId: string,
  tenantId: string,
  storeId: string,
  sale: Record<string, unknown>,
): Promise<void> {
  const expected = serverExpectedCount(sale, "expectedItemCount");
  const rows = await relatedPayloads(
    transaction, "sale_items", "saleRecordId", saleId, tenantId, storeId, "",
  );
  if (rows.length !== expected)
    throw new ApiError(409, "SYNC_DEPENDENCY_MISSING",
      "The saved sale cannot be finalized until all declared lines are synchronized.");
  validateSaleLineTotals(sale, rows);
}

function validateSaleLineTotals(
  sale: Record<string, unknown>,
  items: Record<string, unknown>[],
): void {
  const subtotal = items.reduce((sum, item) =>
    sum + Number(item.unitPrice) * Number(item.quantity), 0);
  const discount = items.reduce((sum, item) => sum + Number(item.discountAmount), 0);
  const tax = items.reduce((sum, item) => {
    const line = Number(item.unitPrice) * Number(item.quantity) - Number(item.discountAmount);
    return sum + line * Number(item.taxRate) / 100;
  }, 0);
  if (!moneyEqual(subtotal, Number(sale.subtotal)) ||
      !moneyEqual(discount, Number(sale.discountTotal)) ||
      !moneyEqual(tax, Number(sale.taxTotal)))
    throw new ApiError(400, "VALIDATION_ERROR",
      "The declared sale totals do not reconcile to its immutable lines.");
}

async function validatePurchaseItemCapacity(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  storeId: string,
  purchaseId: string,
  purchaseRow: Row,
  editablePurchaseIds: ReadonlySet<string>,
): Promise<void> {
  const purchase = safeJsonObject(purchaseRow.payload_json);
  const expected = serverExpectedCount(purchase, "expectedItemCount");
  const existing = await relatedPayloads(
    transaction, "purchase_items", "purchaseDocumentRecordId", purchaseId,
    context.claims.tid, storeId, operation.recordId,
  );
  if (existing.length >= expected)
    throw new ApiError(409, "IMMUTABLE_TRANSACTION",
      "Lines cannot be appended to an already posted purchase; use an adjustment record.");
  if (!editablePurchaseIds.has(purchaseId) && existing.length + 1 > expected)
    throw new ApiError(409, "IMMUTABLE_TRANSACTION", "The purchase already has its declared immutable lines.");
  if (existing.length + 1 === expected) {
    const items = [...existing, operation.payload];
    const subtotal = items.reduce((sum, item) =>
      sum + Number(item.quantity) * Number(item.unitCost), 0);
    const tax = items.reduce((sum, item) =>
      sum + Number(item.quantity) * Number(item.unitCost) * Number(item.taxRate) / 100, 0);
    if (!moneyEqual(subtotal, Number(purchase.subtotal)) ||
        !moneyEqual(tax, Number(purchase.taxTotal)) ||
        !moneyEqual(subtotal + tax, Number(purchase.total)))
      throw new ApiError(400, "VALIDATION_ERROR",
        "The declared purchase totals do not reconcile to its immutable lines.");
  }
}

async function relatedPayloads(
  transaction: Transaction,
  table: "sale_items" | "purchase_items",
  relationship: "saleRecordId" | "purchaseDocumentRecordId",
  parentId: string,
  tenantId: string,
  storeId: string,
  excludedRecordId: string,
): Promise<Record<string, unknown>[]> {
  const result = await transaction.execute({
    sql: `SELECT payload_json FROM ${table}
          WHERE tenant_id = ? AND store_id = ? AND id <> ? AND deleted_at_utc IS NULL
            AND json_extract(payload_json, ?) = ?`,
    args: [tenantId, storeId, excludedRecordId, `$.${relationship}`, parentId],
  });
  return result.rows.map((row) => safeJsonObject(row.payload_json));
}

function serverExpectedCount(payload: Record<string, unknown>, name: string): number {
  const value = Number(payload[name]);
  if (!Number.isSafeInteger(value) || value < 0 || value > 100_000)
    throw new ApiError(409, "SERVER_SCHEMA_MISMATCH", `The server record is missing ${name}.`);
  return value;
}

function moneyEqual(left: number, right: number): boolean {
  return Number.isFinite(left) && Number.isFinite(right) && Math.abs(left - right) <= 0.0001;
}

async function requireRelatedRecord(
  transaction: Transaction,
  table: string,
  recordId: string,
  tenantId: string,
  storeId: string | null,
  label: string,
): Promise<Row> {
  const result = await transaction.execute({
    sql: `SELECT id, payload_json, version FROM ${table}
          WHERE id = ? AND tenant_id = ? AND
                (store_id = ? OR (store_id IS NULL AND ? IS NULL)) AND deleted_at_utc IS NULL`,
    args: [recordId, tenantId, storeId, storeId],
  });
  const row = result.rows[0];
  if (!row) throw new ApiError(409, "SYNC_DEPENDENCY_MISSING", `${label} does not reference an accessible record.`);
  return row;
}

function relationshipId(payload: Record<string, unknown>, name: string, required: boolean): string | null {
  if (payload[name] == null || payload[name] === "") {
    if (required) throw new ApiError(400, "VALIDATION_ERROR", `${name} is required.`);
    return null;
  }
  return validateUuid(payload[name], name);
}

async function validatePaymentLimit(
  transaction: Transaction,
  context: AuthContext,
  operation: SyncOperation,
  storeId: string,
  saleId: string,
  saleRow: Row,
): Promise<void> {
  const sale = safeJsonObject(saleRow.payload_json);
  const total = Number(sale.subtotal) - Number(sale.discountTotal) + Number(sale.taxTotal) + Number(sale.rounding);
  const amount = Number(operation.payload.amount);
  const expectedPayments = serverExpectedCount(sale, "expectedPaymentCount");
  if (!Number.isFinite(total) || total === 0 || (total > 0 && amount <= 0) || (total < 0 && amount >= 0))
    throw new ApiError(400, "VALIDATION_ERROR", "The payment direction does not match the sale total.");
  const existing = await transaction.execute({
    sql: `SELECT payload_json FROM payments
          WHERE tenant_id = ? AND store_id = ? AND id <> ? AND deleted_at_utc IS NULL
            AND json_extract(payload_json, '$.saleRecordId') = ?`,
    args: [context.claims.tid, storeId, operation.recordId, saleId],
  });
  const nextTotal = existing.rows.reduce((sum, row) =>
    sum + Number(safeJsonObject(row.payload_json).amount ?? 0), amount);
  if (existing.rows.length >= expectedPayments)
    throw new ApiError(409, "IMMUTABLE_TRANSACTION",
      "The sale already has its declared immutable payments.");
  if ((total > 0 && nextTotal > total + 0.0001) || (total < 0 && nextTotal < total - 0.0001))
    throw new ApiError(409, "PAYMENT_TOTAL_EXCEEDED", "Payments cannot exceed the immutable sale total.");
  if (existing.rows.length + 1 === expectedPayments && !moneyEqual(nextTotal, total))
    throw new ApiError(409, "PAYMENT_TOTAL_EXCEEDED",
      "The declared payment count does not reconcile exactly to the immutable sale total.");
}

async function validateInventorySource(
  transaction: Transaction,
  context: AuthContext,
  payload: Record<string, unknown>,
  storeId: string,
): Promise<void> {
  const type = Number(payload.type);
  if (type === 2) {
    const purchaseId = relationshipId(payload, "purchaseDocumentRecordId", true)!;
    const itemId = relationshipId(payload, "purchaseItemRecordId", true)!;
    const purchaseRow = await requireRelatedRecord(transaction, "purchases", purchaseId,
      context.claims.tid, storeId, "purchaseDocumentRecordId");
    const itemRow = await requireRelatedRecord(transaction, "purchase_items", itemId,
      context.claims.tid, storeId, "purchaseItemRecordId");
    const purchase = safeJsonObject(purchaseRow.payload_json);
    const item = safeJsonObject(itemRow.payload_json);
    if (Number(purchase.status) !== 0 || item.purchaseDocumentRecordId !== purchaseId ||
        item.productRecordId !== payload.productRecordId ||
        Math.abs(Number(item.quantity) - Number(payload.quantity)) > 0.0001 ||
        (payload.unitCost != null && Math.abs(Number(item.unitCost) - Number(payload.unitCost)) > 0.0001))
      throw new ApiError(409, "INVENTORY_SOURCE_MISMATCH",
        "The inventory movement does not reconcile to its immutable purchase line.");
    payload.unitCost = Number(item.unitCost);
    return;
  }
  if (type !== 0 && type !== 1) return;
  const saleId = relationshipId(payload, "saleRecordId", true)!;
  const itemId = relationshipId(payload, "saleItemRecordId", true)!;
  const saleRow = await requireRelatedRecord(transaction, "sales", saleId,
    context.claims.tid, storeId, "saleRecordId");
  const itemRow = await requireRelatedRecord(transaction, "sale_items", itemId,
    context.claims.tid, storeId, "saleItemRecordId");
  const sale = safeJsonObject(saleRow.payload_json);
  const item = safeJsonObject(itemRow.payload_json);
  const itemQuantity = Number(item.quantity);
  const movementQuantity = Number(payload.quantity);
  const expectedQuantity = type === 0
    ? -itemQuantity
    : itemQuantity < 0
      ? -itemQuantity
      : Number(sale.status) === 2 ? itemQuantity : Number.NaN;
  if (item.saleRecordId !== saleId || item.productRecordId !== payload.productRecordId ||
      !Number.isFinite(expectedQuantity) || Math.abs(expectedQuantity - movementQuantity) > 0.0001 ||
      (payload.unitCost != null && Math.abs(Number(item.costPrice) - Number(payload.unitCost)) > 0.0001))
    throw new ApiError(409, "INVENTORY_SOURCE_MISMATCH",
      "The inventory movement does not reconcile to its immutable sale line.");
  payload.unitCost = Number(item.costPrice);
}

function auditAction(operation: SyncOperation): string {
  if (operation.entityType === "sales") {
    const status = Number(operation.payload.status);
    if (status === 2) return "sale.voided";
    if (status === 3) return "sale.refunded";
    return "sale.completed";
  }
  if (operation.entityType === "payments") return "payment.recorded";
  if (operation.entityType === "refunds") return "sale.refunded";
  if (operation.entityType === "voided_sales") return "sale.voided";
  if (operation.entityType === "inventory_movements") return "inventory.adjusted";
  if (operation.entityType === "purchases") return "purchase.completed";
  if (operation.entityType === "cash_movements") return "register.movement_recorded";
  if (operation.entityType === "expenses") return "expense.recorded";
  return `sync.${operation.operation}`;
}

function assertSchemaCompatibility(clientSchemaVersion: number, env: Env): void {
  const minimum = Number(env.MINIMUM_CLIENT_SCHEMA_VERSION ?? "4");
  const server = Number(env.SCHEMA_VERSION ?? "4");
  if (clientSchemaVersion < minimum)
    throw new ApiError(409, "CLIENT_VERSION_INCOMPATIBLE", "Update PosApp before synchronizing.");
  if (clientSchemaVersion > server)
    throw new ApiError(409, "SERVER_VERSION_INCOMPATIBLE", "The online service must be upgraded before this client can synchronize.");
}
