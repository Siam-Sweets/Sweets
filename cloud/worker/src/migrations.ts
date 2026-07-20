import type { InStatement, Transaction } from "@libsql/client/web";
import { assertStore } from "./auth";
import { database, nowIso, text } from "./db";
import { ApiError, jsonResponse } from "./errors";
import { requirePermission } from "./permissions";
import type { AuthContext, Env } from "./types";
import { requireObject, validateUuid } from "./validation";

const BUSINESS_TABLES = [
  "categories", "product_units", "products", "customers", "suppliers", "discounts",
  "promotions", "taxes", "application_settings", "sales", "sale_items", "payments",
  "refunds", "voided_sales", "open_sales", "purchases", "purchase_items",
  "inventory_adjustments", "inventory_movements", "expenses", "register_sessions", "cash_movements",
] as const;

export async function startInitialMigration(
  context: AuthContext,
  env: Env,
  body: unknown,
): Promise<Response> {
  requirePermission(context.claims, "settings.manage");
  if (context.claims.role !== "admin")
    throw new ApiError(403, "PERMISSION_DENIED", "Only an administrator can start data migration.");
  const source = requireObject(body);
  const storeId = validateUuid(source.storeId, "storeId");
  const resumeMigrationId = source.migrationId == null
    ? null
    : validateUuid(source.migrationId, "migrationId");
  const client = database(env);
  let transaction: Transaction | undefined;
  try {
    await assertStore(client, context.claims.tid, storeId, context.claims.sub, true);
    transaction = await client.transaction("write");
    await expireStaleMigration(transaction, context.claims.tid, storeId);
    const active = await transaction.execute({
      sql: `SELECT id, user_id, device_id FROM migration_sessions
            WHERE tenant_id = ? AND store_id = ? AND status = 'active' LIMIT 1`,
      args: [context.claims.tid, storeId],
    });
    if (active.rows[0]) {
      const row = active.rows[0];
      if (text(row.user_id) !== context.claims.sub || text(row.device_id) !== context.claims.did)
        throw new ApiError(409, "MIGRATION_IN_PROGRESS",
          "Another administrator device is already migrating this store.");
      const expiresAtUtc = migrationExpiry();
      await transaction.execute({
        sql: "UPDATE migration_sessions SET expires_at_utc = ? WHERE id = ?",
        args: [expiresAtUtc, text(row.id)],
      });
      await transaction.commit();
      return jsonResponse({ migrationId: text(row.id), expiresAtUtc, requestId: context.requestId },
        200, context.requestId);
    }

    if (resumeMigrationId) {
      const resumable = await transaction.execute({
        sql: `SELECT id FROM migration_sessions
              WHERE id = ? AND tenant_id = ? AND store_id = ? AND user_id = ? AND device_id = ?
                AND status = 'expired' LIMIT 1`,
        args: [resumeMigrationId, context.claims.tid, storeId, context.claims.sub, context.claims.did],
      });
      if (!resumable.rows[0])
        throw new ApiError(409, "MIGRATION_NOT_ACTIVE",
          "The previous migration cannot be resumed by this user and device.");
      const expiresAtUtc = migrationExpiry();
      const resumedAtUtc = nowIso();
      await transaction.batch([
        {
          sql: `UPDATE migration_sessions SET status = 'active', expires_at_utc = ?, completed_at_utc = NULL
                WHERE id = ? AND status = 'expired'`,
          args: [expiresAtUtc, resumeMigrationId],
        },
        migrationAudit(context, storeId, resumeMigrationId, "data.migration_resumed", resumedAtUtc),
      ]);
      await transaction.commit();
      return jsonResponse({ migrationId: resumeMigrationId, expiresAtUtc, requestId: context.requestId },
        200, context.requestId);
    }

    for (const table of BUSINESS_TABLES) {
      const count = await transaction.execute({
        sql: `SELECT 1 FROM ${table}
              WHERE tenant_id = ? AND deleted_at_utc IS NULL LIMIT 1`,
        args: [context.claims.tid],
      });
      if (count.rows.length)
        throw new ApiError(409, "CLOUD_DATA_NOT_EMPTY",
          "Initial migration is blocked because this organization already contains business data.");
    }

    const migrationId = crypto.randomUUID();
    const startedAtUtc = nowIso();
    const expiresAtUtc = migrationExpiry();
    await transaction.batch([
      {
        sql: `INSERT INTO migration_sessions
              (id, tenant_id, store_id, user_id, device_id, status, started_at_utc, expires_at_utc)
              VALUES (?, ?, ?, ?, ?, 'active', ?, ?)`,
        args: [migrationId, context.claims.tid, storeId, context.claims.sub, context.claims.did,
          startedAtUtc, expiresAtUtc],
      },
      migrationAudit(context, storeId, migrationId, "data.migration_started", startedAtUtc),
    ]);
    await transaction.commit();
    return jsonResponse({ migrationId, expiresAtUtc, requestId: context.requestId }, 201, context.requestId);
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

export async function finishInitialMigration(
  context: AuthContext,
  env: Env,
  body: unknown,
): Promise<Response> {
  requirePermission(context.claims, "settings.manage");
  const source = requireObject(body);
  const storeId = validateUuid(source.storeId, "storeId");
  const client = database(env);
  let transaction: Transaction | undefined;
  try {
    await assertStore(client, context.claims.tid, storeId, context.claims.sub,
      context.claims.role === "admin");
    transaction = await client.transaction("write");
    const active = await transaction.execute({
      sql: `SELECT id FROM migration_sessions
            WHERE tenant_id = ? AND store_id = ? AND user_id = ? AND device_id = ?
              AND status = 'active' AND expires_at_utc > ? LIMIT 1`,
      args: [context.claims.tid, storeId, context.claims.sub, context.claims.did, nowIso()],
    });
    if (!active.rows[0])
      throw new ApiError(409, "MIGRATION_NOT_ACTIVE", "No active migration belongs to this device.");
    const migrationId = text(active.rows[0].id);
    const incompleteComposition = await transaction.execute({
      sql: `SELECT 1 FROM sync_transaction_compositions
            WHERE tenant_id = ? AND store_id = ? AND status = 'pending' LIMIT 1`,
      args: [context.claims.tid, storeId],
    });
    if (incompleteComposition.rows[0])
      throw new ApiError(409, "MIGRATION_INCOMPLETE",
        "The migration still contains an incomplete sale or purchase composition.");
    const completedAtUtc = nowIso();
    await transaction.batch([
      {
        sql: `UPDATE migration_sessions SET status = 'completed', completed_at_utc = ?
              WHERE id = ? AND status = 'active'`,
        args: [completedAtUtc, migrationId],
      },
      migrationAudit(context, storeId, migrationId, "data.migration", completedAtUtc),
    ]);
    await transaction.commit();
    return jsonResponse({ ok: true, migrationId, requestId: context.requestId }, 200, context.requestId);
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

export async function assertMigrationWriteAccess(
  transaction: Transaction,
  context: AuthContext,
  storeId: string,
): Promise<boolean> {
  await expireStaleMigration(transaction, context.claims.tid, storeId);
  const result = await transaction.execute({
    sql: `SELECT user_id, device_id FROM migration_sessions
          WHERE tenant_id = ? AND store_id = ? AND status = 'active' LIMIT 1`,
    args: [context.claims.tid, storeId],
  });
  const row = result.rows[0];
  if (!row) return false;
  if (text(row.user_id) !== context.claims.sub || text(row.device_id) !== context.claims.did)
    throw new ApiError(409, "MIGRATION_IN_PROGRESS",
      "Synchronization is paused while another administrator device migrates this store.");
  return true;
}

async function expireStaleMigration(
  transaction: Transaction,
  tenantId: string,
  storeId: string,
): Promise<void> {
  await transaction.execute({
    sql: `UPDATE migration_sessions SET status = 'expired'
          WHERE tenant_id = ? AND store_id = ? AND status = 'active' AND expires_at_utc <= ?`,
    args: [tenantId, storeId, nowIso()],
  });
}

function migrationExpiry(): string {
  return new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
}

function migrationAudit(
  context: AuthContext,
  storeId: string,
  migrationId: string,
  action: string,
  timestamp: string,
): InStatement {
  return {
    sql: `INSERT INTO audit_logs
          (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action,
           affected_type, affected_id, request_id, metadata_json)
          VALUES (?, ?, ?, ?, ?, ?, ?, 'migration', ?, ?, '{}')`,
    args: [crypto.randomUUID(), context.claims.tid, storeId, context.claims.sub,
      context.claims.did, timestamp, action, migrationId, context.requestId],
  };
}
