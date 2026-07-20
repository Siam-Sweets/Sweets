import { ApiError, jsonResponse } from "./errors";
import { assertStore, insertAudit } from "./auth";
import { booleanValue, database, nowIso, text } from "./db";
import { requirePermission } from "./permissions";
import { requiredString, requireObject } from "./validation";
import type { AuthContext, Env } from "./types";

export async function listStores(context: AuthContext, env: Env): Promise<Response> {
  const client = database(env);
  try {
    const result = await client.execute({
      sql: `SELECT s.id, s.tenant_id, s.name, s.code, s.is_active,
                   s.created_at_utc, s.updated_at_utc, s.version
            FROM stores s WHERE tenant_id = ? AND
              (? = 1 OR EXISTS (
                SELECT 1 FROM user_store_assignments usa
                WHERE usa.tenant_id = s.tenant_id AND usa.store_id = s.id
                  AND usa.user_id = ? AND usa.is_active = 1
              ))
            ORDER BY name LIMIT 200`,
      args: [context.claims.tid, context.claims.role === "admin" ? 1 : 0, context.claims.sub],
    });
    const stores = result.rows.map((row) => ({
      id: text(row.id), tenantId: text(row.tenant_id), name: text(row.name), code: text(row.code),
      isActive: booleanValue(row.is_active), createdAtUtc: text(row.created_at_utc),
      updatedAtUtc: text(row.updated_at_utc), version: Number(row.version),
    }));
    return jsonResponse({ stores, requestId: context.requestId }, 200, context.requestId);
  } finally { client.close(); }
}

export async function createStore(context: AuthContext, env: Env, body: unknown): Promise<Response> {
  requirePermission(context.claims, "stores.manage");
  const source = requireObject(body);
  const name = requiredString(source, "name", 160);
  const code = requiredString(source, "code", 20).toUpperCase();
  if (!/^[A-Z0-9_-]{2,20}$/.test(code)) throw new ApiError(400, "VALIDATION_ERROR", "Store code is invalid.");
  const client = database(env);
  try {
    const id = crypto.randomUUID();
    const now = nowIso();
    await client.batch([
      {
        sql: `INSERT INTO stores
              (id, tenant_id, name, code, is_active, created_at_utc, updated_at_utc, version)
              VALUES (?, ?, ?, ?, 1, ?, ?, 1)`,
        args: [id, context.claims.tid, name, code, now, now],
      },
      {
        sql: `INSERT INTO user_store_assignments
              (tenant_id, user_id, store_id, is_active, created_at_utc)
              VALUES (?, ?, ?, 1, ?)`,
        args: [context.claims.tid, context.claims.sub, id, now],
      },
    ], "write");
    await insertAudit(client, context, "store.created", "store", id, { code }, id);
    return jsonResponse({ id, requestId: context.requestId }, 201, context.requestId);
  } catch (error) {
    if (error instanceof Error && /unique|constraint/i.test(error.message))
      throw new ApiError(409, "STORE_CODE_EXISTS", "That store code is already in use.");
    throw error;
  } finally { client.close(); }
}

export async function verifySelectedStore(context: AuthContext, env: Env, storeId: string): Promise<Response> {
  const client = database(env);
  try {
    await assertStore(client, context.claims.tid, storeId,
      context.claims.sub, context.claims.role === "admin");
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally { client.close(); }
}
