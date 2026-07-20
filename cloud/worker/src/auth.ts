import type { Client, Row, Transaction } from "@libsql/client/web";
import { ApiError, jsonResponse } from "./errors";
import { booleanValue, database, integer, nowIso, nullableText, text } from "./db";
import {
  clampNumber,
  hashPassword,
  randomToken,
  sha256,
  signAccessToken,
  timingSafeEqual,
  verifyAccessToken,
  verifyPassword,
  base64UrlDecode,
} from "./crypto";
import {
  effectivePermissions,
  hasPermission,
  requirePermission,
  validateCustomPermissions,
} from "./permissions";
import {
  optionalString,
  requiredString,
  requireObject,
  validateDevice,
  validateEmail,
  validatePassword,
  validateUsername,
  validateUuid,
} from "./validation";
import type { AccessClaims, AuthContext, DeviceInput, Env, Role } from "./types";

const loginWindows = new Map<string, { count: number; resetAt: number }>();

export async function authenticate(request: Request, env: Env, requestId: string): Promise<AuthContext> {
  const authorization = request.headers.get("authorization") ?? "";
  if (!authorization.startsWith("Bearer ")) throw new ApiError(401, "AUTH_REQUIRED", "Sign in is required.");
  const claims = await verifyAccessToken(authorization.slice(7), env);
  const client = database(env);
  try {
    const result = await client.execute({
      sql: `SELECT s.revoked_at_utc, s.expires_at_utc, s.device_id,
                   u.is_active, u.role, u.permissions_json, u.password_version,
                   o.is_active AS organization_active, d.status AS device_status
            FROM login_sessions s
            JOIN users u ON u.id = s.user_id AND u.tenant_id = s.tenant_id
            JOIN organizations o ON o.id = s.tenant_id
            JOIN registered_devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            WHERE s.id = ? AND s.user_id = ? AND s.tenant_id = ?`,
      args: [claims.sid, claims.sub, claims.tid],
    });
    const row = result.rows[0];
    if (!row || row.revoked_at_utc != null || Date.parse(text(row.expires_at_utc)) <= Date.now())
      throw new ApiError(401, "SESSION_REVOKED", "This session is no longer active.");
    if (text(row.device_id) !== claims.did || text(row.device_status) !== "active")
      throw new ApiError(401, "DEVICE_REVOKED", "This device has been revoked.");
    if (!booleanValue(row.is_active)) throw new ApiError(403, "USER_DISABLED", "This user account is disabled.");
    if (!booleanValue(row.organization_active))
      throw new ApiError(403, "ORGANIZATION_DISABLED", "This organization is disabled.");
    if (integer(row.password_version) !== claims.pv)
      throw new ApiError(401, "ACCESS_TOKEN_EXPIRED", "Sign in again after the password change.");

    const role = parseRole(text(row.role));
    claims.role = role;
    claims.permissions = effectivePermissions(role, parsePermissions(row.permissions_json));
    return { claims, requestId };
  } finally {
    client.close();
  }
}

export async function signup(request: Request, env: Env, requestId: string, body: unknown): Promise<Response> {
  const source = requireObject(body);
  const organizationName = requiredString(source, "organizationName", 160);
  const storeName = requiredString(source, "storeName", 160);
  const fullName = requiredString(source, "fullName", 100);
  const username = validateUsername(requiredString(source, "username", 60));
  const email = validateEmail(requiredString(source, "email", 255));
  const password = validatePassword(source.password);
  const device = validateDevice(source.device);
  assertClientSchema(source, env);
  assertAuthSecrets(env);
  const passwordHash = await hashPassword(password);

  const tenantId = crypto.randomUUID();
  const storeId = crypto.randomUUID();
  const userId = crypto.randomUUID();
  const sessionId = crypto.randomUUID();
  const familyId = crypto.randomUUID();
  const refreshId = crypto.randomUUID();
  const refreshPlaintext = `${refreshId}.${randomToken(32)}`;
  const refreshHash = await hashRefreshToken(refreshPlaintext, env);
  const refreshExpiresAtUtc = refreshExpiry(env);
  const permissions = effectivePermissions("admin");
  const access = await signAccessToken({
    sub: userId, tid: tenantId, sid: sessionId, did: device.id,
    role: "admin", permissions, pv: 1,
  }, env);
  const timestamp = nowIso();
  const syncPayload = userSyncPayload(username, email, fullName, "admin", true, timestamp, timestamp);
  const client = database(env);
  let transaction: Transaction | undefined;
  try {
    transaction = await client.transaction("write");
    const registeredDevice = await transaction.execute({
      sql: "SELECT 1 FROM registered_devices WHERE id = ? LIMIT 1",
      args: [device.id],
    });
    if (registeredDevice.rows.length)
      throw new ApiError(409, "DEVICE_TENANT_MISMATCH", "This installation is already registered to an organization.");
    const duplicate = await transaction.execute({
      sql: "SELECT 1 FROM users WHERE username_normalized = ? OR email_normalized = ? LIMIT 1",
      args: [username, email],
    });
    if (duplicate.rows.length) throw new ApiError(409, "ACCOUNT_ALREADY_EXISTS", "That username or email is already registered.");

    await transaction.batch([
      {
        sql: `INSERT INTO organizations
              (id, name, is_active, created_at_utc, updated_at_utc, schema_version)
              VALUES (?, ?, 1, ?, ?, ?)`,
        args: [tenantId, organizationName, timestamp, timestamp, schemaVersion(env)],
      },
      {
        sql: `INSERT INTO stores
              (id, tenant_id, name, code, is_active, created_at_utc, updated_at_utc, version)
              VALUES (?, ?, ?, ?, 1, ?, ?, 1)`,
        args: [storeId, tenantId, storeName, "MAIN", timestamp, timestamp],
      },
      {
        sql: `INSERT INTO users
              (id, tenant_id, default_store_id, username, username_normalized, email, email_normalized,
               full_name, password_hash, password_version, role, permissions_json, is_active,
               created_at_utc, updated_at_utc, version)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, 'admin', '[]', 1, ?, ?, 1)`,
        args: [userId, tenantId, storeId, username, username, email, email, fullName, passwordHash, timestamp, timestamp],
      },
      {
        sql: `INSERT INTO user_store_assignments
              (tenant_id, user_id, store_id, is_active, created_at_utc)
              VALUES (?, ?, ?, 1, ?)`,
        args: [tenantId, userId, storeId, timestamp],
      },
      {
        sql: `INSERT INTO user_sync_records
              (id, tenant_id, store_id, payload_json, created_at_utc, updated_at_utc, deleted_at_utc,
               version, created_by_user_id, updated_by_user_id, last_modified_device_id)
              VALUES (?, ?, NULL, ?, ?, ?, NULL, 1, ?, ?, ?)`,
        args: [userId, tenantId,
          syncPayload,
          timestamp, timestamp, userId, userId, device.id],
      },
      {
        sql: `INSERT INTO sync_changes
              (tenant_id, store_id, entity_type, record_id, version, updated_at_utc, deleted_at_utc,
               last_modified_device_id, payload_json)
              VALUES (?, NULL, 'users', ?, 1, ?, NULL, ?, ?)`,
        args: [tenantId, userId, timestamp, device.id, syncPayload],
      },
      {
        sql: `INSERT INTO registered_devices
              (id, tenant_id, registered_by_user_id, assigned_store_id, name, operating_system,
               machine_name, status, first_registered_at_utc, last_login_at_utc, updated_at_utc)
              VALUES (?, ?, ?, ?, ?, ?, ?, 'active', ?, ?, ?)`,
        args: [device.id, tenantId, userId, storeId, device.name, device.operatingSystem ?? "Windows",
          device.machineName ?? null, timestamp, timestamp, timestamp],
      },
      {
        sql: `INSERT INTO login_sessions
              (id, tenant_id, user_id, device_id, created_at_utc, last_login_at_utc,
               last_seen_at_utc, expires_at_utc)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
        args: [sessionId, tenantId, userId, device.id, timestamp, timestamp, timestamp, refreshExpiresAtUtc],
      },
      {
        sql: `INSERT INTO refresh_tokens
              (id, session_id, family_id, parent_token_id, token_hash, created_at_utc, expires_at_utc)
              VALUES (?, ?, ?, NULL, ?, ?, ?)`,
        args: [refreshId, sessionId, familyId, refreshHash, timestamp, refreshExpiresAtUtc],
      },
      {
        sql: `INSERT INTO audit_logs
              (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
               affected_id, request_id, metadata_json)
              VALUES (?, ?, ?, ?, ?, ?, 'organization.created', 'organization', ?, ?, '{}')`,
        args: [crypto.randomUUID(), tenantId, storeId, userId, device.id, timestamp, tenantId, requestId],
      },
      {
        sql: `INSERT INTO audit_logs
              (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
               affected_id, request_id, metadata_json)
              VALUES (?, ?, ?, ?, ?, ?, 'auth.login', 'session', ?, ?, '{}')`,
        args: [crypto.randomUUID(), tenantId, storeId, userId, device.id, timestamp, sessionId, requestId],
      },
      {
        sql: `INSERT INTO audit_logs
              (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
               affected_id, request_id, metadata_json)
              VALUES (?, ?, ?, ?, ?, ?, 'device.registered', 'device', ?, ?, '{}')`,
        args: [crypto.randomUUID(), tenantId, storeId, userId, device.id, timestamp, device.id, requestId],
      },
    ]);
    await transaction.commit();
    return jsonResponse({
      tokens: {
        accessToken: access.token,
        refreshToken: refreshPlaintext,
        accessTokenExpiresAtUtc: access.expiresAtUtc,
        refreshTokenExpiresAtUtc: refreshExpiresAtUtc,
        sessionId,
      },
      user: {
        id: userId, tenantId, username, email, fullName, role: roleNumber("admin"),
        isActive: true, permissions,
      },
      organizationId: tenantId,
      organizationName,
      store: { id: storeId, tenantId, name: storeName, code: "MAIN", isActive: true },
      apiVersion: apiVersion(env), schemaVersion: schemaVersion(env), deviceId: device.id,
    }, 201, requestId);
  } catch (error) {
    if (transaction) await safeRollback(transaction);
    if (isUniqueError(error) && error instanceof Error && /registered_devices/i.test(error.message))
      throw new ApiError(409, "DEVICE_TENANT_MISMATCH", "This installation is already registered to an organization.");
    if (isUniqueError(error)) throw new ApiError(409, "ACCOUNT_ALREADY_EXISTS", "That username or email is already registered.");
    throw error;
  } finally {
    transaction?.close();
    client.close();
  }
}

export async function login(
  request: Request,
  env: Env,
  requestId: string,
  body: unknown,
  clientAddress: string,
): Promise<Response> {
  const source = requireObject(body);
  const identifier = requiredString(source, "usernameOrEmail", 255).toLowerCase();
  const password = typeof source.password === "string" ? source.password : "";
  if (password.length < 1 || password.length > 128) throw invalidCredentials();
  const device = validateDevice(source.device);
  assertClientSchema(source, env);
  assertAuthSecrets(env);
  const loginKey = `${clientAddress}:${identifier}`;
  enforceLoginRateLimit(loginKey);
  const loginKeyHash = await sha256(loginKey);
  const accountKeyHash = await sha256(`account:${identifier}`);

  const client = database(env);
  try {
    await enforcePersistentLoginRateLimit(client, loginKeyHash);
    await enforcePersistentLoginRateLimit(client, accountKeyHash);
    const result = await client.execute({
      sql: `SELECT u.id, u.tenant_id, u.default_store_id, u.username, u.email, u.full_name,
                   u.password_hash, u.password_version, u.role, u.permissions_json, u.is_active,
                   o.name AS organization_name, o.is_active AS organization_active,
                   s.name AS store_name, s.code AS store_code, s.is_active AS store_active
            FROM users u
            JOIN organizations o ON o.id = u.tenant_id
            JOIN stores s ON s.id = u.default_store_id AND s.tenant_id = u.tenant_id
            WHERE u.username_normalized = ? OR u.email_normalized = ?
            LIMIT 1`,
      args: [identifier, identifier],
    });
    const row = result.rows[0];
    if (!row || !await verifyPassword(password, text(row.password_hash))) {
      recordLoginFailure(loginKey);
      await recordPersistentLoginFailure(client, loginKeyHash);
      await recordPersistentLoginFailure(client, accountKeyHash);
      await auditFailedLogin(client, requestId, identifier, device.id, row);
      throw invalidCredentials();
    }
    if (!booleanValue(row.is_active)) {
      await auditFailedLogin(client, requestId, identifier, device.id, row, "user_disabled");
      throw new ApiError(403, "USER_DISABLED", "This user account is disabled.");
    }
    if (!booleanValue(row.organization_active)) {
      await auditFailedLogin(client, requestId, identifier, device.id, row, "organization_disabled");
      throw new ApiError(403, "ORGANIZATION_DISABLED", "This organization is disabled.");
    }
    clearLoginFailures(loginKey);
    await client.execute({
      sql: "DELETE FROM login_attempts WHERE key_hash = ? OR key_hash = ?",
      args: [loginKeyHash, accountKeyHash],
    });
    const account = userFromRow(row);
    let selectedStoreName = text(row.store_name);
    let selectedStoreCode = text(row.store_code);
    // Keep this installation on the branch selected during its previous
    // session when that branch is still active and the user may access it.
    // Falling back to the user's default store also handles first login.
    const assignedStore = await client.execute({
      sql: `SELECT s.id, s.name, s.code
            FROM registered_devices d
            JOIN stores s ON s.id = d.assigned_store_id AND s.tenant_id = d.tenant_id
            WHERE d.id = ? AND d.tenant_id = ? AND d.status = 'active' AND s.is_active = 1
              AND (? = 1 OR EXISTS (
                SELECT 1 FROM user_store_assignments usa
                WHERE usa.tenant_id = s.tenant_id AND usa.store_id = s.id
                  AND usa.user_id = ? AND usa.is_active = 1
              ))
            LIMIT 1`,
      args: [device.id, account.tenantId, account.role === "admin" ? 1 : 0, account.userId],
    });
    if (assignedStore.rows[0]) {
      account.storeId = text(assignedStore.rows[0].id);
      selectedStoreName = text(assignedStore.rows[0].name);
      selectedStoreCode = text(assignedStore.rows[0].code);
    } else if (!booleanValue(row.store_active)) {
      // A user's former default branch may have been disabled while another
      // assigned branch remains active. Select a deterministic authorized
      // fallback instead of locking the entire multi-store account out.
      const fallbackStore = await client.execute({
        sql: `SELECT s.id, s.name, s.code FROM stores s
              WHERE s.tenant_id = ? AND s.is_active = 1
                AND (? = 1 OR EXISTS (
                  SELECT 1 FROM user_store_assignments usa
                  WHERE usa.tenant_id = s.tenant_id AND usa.store_id = s.id
                    AND usa.user_id = ? AND usa.is_active = 1
                ))
              ORDER BY s.name, s.id LIMIT 1`,
        args: [account.tenantId, account.role === "admin" ? 1 : 0, account.userId],
      });
      if (!fallbackStore.rows[0]) {
        await auditFailedLogin(client, requestId, identifier, device.id, row, "store_disabled");
        throw new ApiError(403, "STORE_DISABLED", "No assigned store is active.");
      }
      account.storeId = text(fallbackStore.rows[0].id);
      selectedStoreName = text(fallbackStore.rows[0].name);
      selectedStoreCode = text(fallbackStore.rows[0].code);
    }
    const response = await createSession(
      client, env, requestId, account, device,
      text(row.organization_name), selectedStoreName, selectedStoreCode,
    );
    return jsonResponse(response, 200, requestId);
  } finally {
    client.close();
  }
}

export async function refresh(env: Env, requestId: string, body: unknown): Promise<Response> {
  assertAuthSecrets(env);
  const source = requireObject(body);
  const refreshToken = requiredString(source, "refreshToken", 256, 40);
  const sessionId = validateUuid(source.sessionId, "sessionId");
  const deviceId = validateUuid(source.deviceId, "deviceId");
  const [tokenId, secret, extra] = refreshToken.split(".");
  if (!tokenId || !secret || extra) throw new ApiError(401, "REFRESH_TOKEN_REVOKED", "Sign in again.");

  const client = database(env);
  let transaction: Transaction | undefined;
  try {
    const result = await client.execute({
      sql: `SELECT rt.id, rt.session_id, rt.family_id, rt.token_hash, rt.expires_at_utc,
                   rt.used_at_utc, rt.revoked_at_utc,
                   s.user_id, s.tenant_id, s.device_id, s.revoked_at_utc AS session_revoked,
                   s.expires_at_utc AS session_expires,
                   u.default_store_id, u.username, u.email, u.full_name, u.role, u.permissions_json,
                   u.password_version, u.is_active, d.status AS device_status,
                   o.is_active AS organization_active
            FROM refresh_tokens rt
            JOIN login_sessions s ON s.id = rt.session_id
            JOIN users u ON u.id = s.user_id AND u.tenant_id = s.tenant_id
            JOIN registered_devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            JOIN organizations o ON o.id = s.tenant_id
            WHERE rt.id = ? AND rt.session_id = ?`,
      args: [tokenId, sessionId],
    });
    const row = result.rows[0];
    if (!row) throw new ApiError(401, "REFRESH_TOKEN_REVOKED", "Sign in again.");
    const suppliedHash = await hashRefreshToken(refreshToken, env);
    if (!safeHashEqual(suppliedHash, text(row.token_hash)))
      throw new ApiError(401, "REFRESH_TOKEN_REVOKED", "Sign in again.");

    if (row.used_at_utc != null || row.revoked_at_utc != null) {
      await client.batch([
        { sql: "UPDATE refresh_tokens SET revoked_at_utc = COALESCE(revoked_at_utc, ?) WHERE family_id = ?", args: [nowIso(), text(row.family_id)] },
        { sql: "UPDATE login_sessions SET revoked_at_utc = COALESCE(revoked_at_utc, ?), revoke_reason = 'refresh_reuse' WHERE id = ?", args: [nowIso(), sessionId] },
      ], "write");
      throw new ApiError(401, "REFRESH_TOKEN_REUSE", "This session was revoked because an old refresh token was reused.");
    }
    if (Date.parse(text(row.expires_at_utc)) <= Date.now() || Date.parse(text(row.session_expires)) <= Date.now())
      throw new ApiError(401, "REFRESH_TOKEN_EXPIRED", "The online session has expired.");
    if (row.session_revoked != null) throw new ApiError(401, "REFRESH_TOKEN_REVOKED", "This session was revoked.");
    if (text(row.device_id) !== deviceId || text(row.device_status) !== "active")
      throw new ApiError(401, "DEVICE_REVOKED", "This device has been revoked.");
    if (!booleanValue(row.is_active)) throw new ApiError(403, "USER_DISABLED", "This user account is disabled.");
    if (!booleanValue(row.organization_active))
      throw new ApiError(403, "ORGANIZATION_DISABLED", "This organization is disabled.");

    const nextId = crypto.randomUUID();
    const nextPlaintext = `${nextId}.${randomToken(32)}`;
    const nextHash = await hashRefreshToken(nextPlaintext, env);
    const refreshExpires = refreshExpiry(env);
    const now = nowIso();
    transaction = await client.transaction("write");
    const consumed = await transaction.execute({
      sql: `UPDATE refresh_tokens SET used_at_utc = ?, revoked_at_utc = ?
            WHERE id = ? AND used_at_utc IS NULL AND revoked_at_utc IS NULL`,
      args: [now, now, tokenId],
    });
    if (consumed.rowsAffected !== 1) {
      await transaction.rollback();
      transaction.close();
      transaction = undefined;
      await client.batch([
        { sql: "UPDATE refresh_tokens SET revoked_at_utc = COALESCE(revoked_at_utc, ?) WHERE family_id = ?", args: [now, text(row.family_id)] },
        { sql: "UPDATE login_sessions SET revoked_at_utc = COALESCE(revoked_at_utc, ?), revoke_reason = 'refresh_reuse' WHERE id = ?", args: [now, sessionId] },
      ], "write");
      throw new ApiError(401, "REFRESH_TOKEN_REUSE", "This session has already been refreshed.");
    }
    await transaction.batch([
      {
        sql: `INSERT INTO refresh_tokens
              (id, session_id, family_id, parent_token_id, token_hash, created_at_utc, expires_at_utc)
              VALUES (?, ?, ?, ?, ?, ?, ?)`,
        args: [nextId, sessionId, text(row.family_id), tokenId, nextHash, now, refreshExpires],
      },
      { sql: "UPDATE login_sessions SET last_seen_at_utc = ? WHERE id = ?", args: [now, sessionId] },
    ]);
    await transaction.commit();

    const role = parseRole(text(row.role));
    const permissions = effectivePermissions(role, parsePermissions(row.permissions_json));
    const access = await signAccessToken({
      sub: text(row.user_id), tid: text(row.tenant_id), sid: sessionId, did: deviceId,
      role, permissions, pv: integer(row.password_version),
    }, env);
    return jsonResponse({
      tokens: {
        accessToken: access.token,
        refreshToken: nextPlaintext,
        accessTokenExpiresAtUtc: access.expiresAtUtc,
        refreshTokenExpiresAtUtc: refreshExpires,
        sessionId,
      },
    }, 200, requestId);
  } finally {
    transaction?.close();
    client.close();
  }
}

export async function logout(context: AuthContext, env: Env, body: unknown): Promise<Response> {
  const source = body == null ? {} : requireObject(body);
  const all = source.revokeAllDeviceSessions === true;
  const client = database(env);
  const now = nowIso();
  try {
    if (all) {
      await client.batch([
        { sql: "UPDATE login_sessions SET revoked_at_utc = COALESCE(revoked_at_utc, ?), revoke_reason = 'logout_all' WHERE tenant_id = ? AND user_id = ?", args: [now, context.claims.tid, context.claims.sub] },
        { sql: `UPDATE refresh_tokens SET revoked_at_utc = COALESCE(revoked_at_utc, ?)
                WHERE session_id IN (SELECT id FROM login_sessions WHERE tenant_id = ? AND user_id = ?)`, args: [now, context.claims.tid, context.claims.sub] },
      ], "write");
    } else {
      await client.batch([
        { sql: "UPDATE login_sessions SET revoked_at_utc = COALESCE(revoked_at_utc, ?), revoke_reason = 'logout' WHERE id = ? AND tenant_id = ?", args: [now, context.claims.sid, context.claims.tid] },
        { sql: "UPDATE refresh_tokens SET revoked_at_utc = COALESCE(revoked_at_utc, ?) WHERE session_id = ?", args: [now, context.claims.sid] },
      ], "write");
    }
    await insertAudit(client, context, "auth.logout", "session", context.claims.sid, {});
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally {
    client.close();
  }
}

export async function listSessions(context: AuthContext, env: Env): Promise<Response> {
  const client = database(env);
  try {
    const result = await client.execute({
      sql: `SELECT s.id, s.device_id, d.name AS device_name, d.operating_system,
                   u.username, d.assigned_store_id, assigned_store.name AS assigned_store_name,
                   d.first_registered_at_utc, s.last_login_at_utc, d.last_sync_at_utc,
                   s.expires_at_utc, s.revoked_at_utc
            FROM login_sessions s
            JOIN registered_devices d ON d.id = s.device_id AND d.tenant_id = s.tenant_id
            JOIN users u ON u.id = s.user_id AND u.tenant_id = s.tenant_id
            LEFT JOIN stores assigned_store ON assigned_store.id = d.assigned_store_id
                 AND assigned_store.tenant_id = d.tenant_id
            WHERE s.tenant_id = ? AND (? = 1 OR s.user_id = ?)
            ORDER BY s.last_login_at_utc DESC LIMIT 100`,
      args: [context.claims.tid, hasPermission(context.claims, "sessions.revoke") ? 1 : 0, context.claims.sub],
    });
    const sessions = result.rows.map((row) => ({
      sessionId: text(row.id), deviceId: text(row.device_id), deviceName: text(row.device_name),
      username: text(row.username),
      storeId: nullableText(row.assigned_store_id), storeName: nullableText(row.assigned_store_name),
      operatingSystem: text(row.operating_system), firstRegisteredAtUtc: text(row.first_registered_at_utc),
      lastLoginAtUtc: nullableText(row.last_login_at_utc), lastSyncAtUtc: nullableText(row.last_sync_at_utc),
      expiresAtUtc: text(row.expires_at_utc), isCurrent: text(row.id) === context.claims.sid,
      isRevoked: row.revoked_at_utc != null,
    }));
    return jsonResponse({ sessions, requestId: context.requestId }, 200, context.requestId);
  } finally {
    client.close();
  }
}

export async function revokeSession(context: AuthContext, env: Env, sessionId: string): Promise<Response> {
  validateUuid(sessionId, "sessionId");
  const client = database(env);
  try {
    const target = await client.execute({
      sql: "SELECT user_id FROM login_sessions WHERE id = ? AND tenant_id = ?",
      args: [sessionId, context.claims.tid],
    });
    const row = target.rows[0];
    if (!row) throw new ApiError(404, "SESSION_NOT_FOUND", "The device session was not found.");
    if (text(row.user_id) !== context.claims.sub && !hasPermission(context.claims, "sessions.revoke"))
      throw new ApiError(403, "PERMISSION_DENIED", "You cannot revoke that session.");
    const now = nowIso();
    await client.batch([
      { sql: "UPDATE login_sessions SET revoked_at_utc = COALESCE(revoked_at_utc, ?), revoke_reason = 'user_revoked' WHERE id = ? AND tenant_id = ?", args: [now, sessionId, context.claims.tid] },
      { sql: "UPDATE refresh_tokens SET revoked_at_utc = COALESCE(revoked_at_utc, ?) WHERE session_id = ?", args: [now, sessionId] },
    ], "write");
    await insertAudit(client, context, "device.session_revoked", "session", sessionId, {});
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally {
    client.close();
  }
}

export async function revokeDevice(context: AuthContext, env: Env, deviceId: string): Promise<Response> {
  requirePermission(context.claims, "sessions.revoke");
  validateUuid(deviceId, "deviceId");
  const client = database(env);
  const now = nowIso();
  try {
    const target = await client.execute({
      sql: "SELECT id FROM registered_devices WHERE id = ? AND tenant_id = ?",
      args: [deviceId, context.claims.tid],
    });
    if (!target.rows.length) throw new ApiError(404, "DEVICE_NOT_FOUND", "The registered device was not found.");
    await client.batch([
      {
        sql: `UPDATE registered_devices SET status = 'revoked', revoked_at_utc = ?,
                     revoked_by_user_id = ?, updated_at_utc = ?
              WHERE id = ? AND tenant_id = ?`,
        args: [now, context.claims.sub, now, deviceId, context.claims.tid],
      },
      {
        sql: `UPDATE login_sessions SET revoked_at_utc = COALESCE(revoked_at_utc, ?),
                     revoke_reason = 'device_revoked'
              WHERE tenant_id = ? AND device_id = ?`,
        args: [now, context.claims.tid, deviceId],
      },
      {
        sql: `UPDATE refresh_tokens SET revoked_at_utc = COALESCE(revoked_at_utc, ?)
              WHERE session_id IN (
                SELECT id FROM login_sessions WHERE tenant_id = ? AND device_id = ?
              )`,
        args: [now, context.claims.tid, deviceId],
      },
    ], "write");
    await insertAudit(client, context, "device.revoked", "device", deviceId, {});
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally { client.close(); }
}

export async function authorizeDevice(context: AuthContext, env: Env, deviceId: string): Promise<Response> {
  requirePermission(context.claims, "sessions.revoke");
  validateUuid(deviceId, "deviceId");
  const client = database(env);
  const now = nowIso();
  try {
    const result = await client.execute({
      sql: `UPDATE registered_devices SET status = 'active', revoked_at_utc = NULL,
                   revoked_by_user_id = NULL, updated_at_utc = ?
            WHERE id = ? AND tenant_id = ?`,
      args: [now, deviceId, context.claims.tid],
    });
    if (result.rowsAffected !== 1)
      throw new ApiError(404, "DEVICE_NOT_FOUND", "The registered device was not found.");
    await insertAudit(client, context, "device.authorized", "device", deviceId, {});
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally { client.close(); }
}

export async function profile(context: AuthContext, env: Env): Promise<Response> {
  const client = database(env);
  try {
    const result = await client.execute({
      sql: `SELECT u.id, u.tenant_id, u.username, u.email, u.full_name, u.role, u.is_active,
                   o.name AS organization_name, s.id AS store_id, s.name AS store_name, s.code AS store_code
            FROM users u JOIN organizations o ON o.id = u.tenant_id
            JOIN stores s ON s.id = u.default_store_id AND s.tenant_id = u.tenant_id
            WHERE u.id = ? AND u.tenant_id = ?`,
      args: [context.claims.sub, context.claims.tid],
    });
    const row = result.rows[0];
    if (!row) throw new ApiError(404, "USER_NOT_FOUND", "The user account was not found.");
    return jsonResponse({
      user: publicUser(row, context.claims.permissions),
      organizationId: context.claims.tid,
      organizationName: text(row.organization_name),
      store: publicStore(row),
      requestId: context.requestId,
    }, 200, context.requestId);
  } finally {
    client.close();
  }
}

export async function changePassword(context: AuthContext, env: Env, body: unknown): Promise<Response> {
  const source = requireObject(body);
  const currentPassword = typeof source.currentPassword === "string" ? source.currentPassword : "";
  if (currentPassword.length < 1 || currentPassword.length > 128) throw invalidCredentials();
  const newPassword = validatePassword(source.newPassword);
  const client = database(env);
  try {
    const result = await client.execute({
      sql: "SELECT password_hash FROM users WHERE id = ? AND tenant_id = ? AND is_active = 1",
      args: [context.claims.sub, context.claims.tid],
    });
    const row = result.rows[0];
    if (!row || !await verifyPassword(currentPassword, text(row.password_hash)))
      throw new ApiError(401, "INVALID_CREDENTIALS", "The current password is incorrect.");
    const nextHash = await hashPassword(newPassword);
    const now = nowIso();
    await client.batch([
      { sql: "UPDATE users SET password_hash = ?, password_version = password_version + 1, updated_at_utc = ?, version = version + 1 WHERE id = ? AND tenant_id = ?", args: [nextHash, now, context.claims.sub, context.claims.tid] },
      { sql: "UPDATE login_sessions SET revoked_at_utc = ?, revoke_reason = 'password_changed' WHERE tenant_id = ? AND user_id = ? AND id <> ? AND revoked_at_utc IS NULL", args: [now, context.claims.tid, context.claims.sub, context.claims.sid] },
      { sql: `UPDATE refresh_tokens SET revoked_at_utc = ? WHERE session_id IN
              (SELECT id FROM login_sessions WHERE tenant_id = ? AND user_id = ? AND id <> ?) AND revoked_at_utc IS NULL`, args: [now, context.claims.tid, context.claims.sub, context.claims.sid] },
    ], "write");
    await insertAudit(client, context, "user.password_changed", "user", context.claims.sub, {});
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally {
    client.close();
  }
}

export async function listUsers(context: AuthContext, env: Env): Promise<Response> {
  requirePermission(context.claims, "users.manage");
  const client = database(env);
  try {
    const result = await client.execute({
      sql: `SELECT id, tenant_id, username, email, full_name, role, permissions_json, is_active,
                   created_at_utc, updated_at_utc, version
            FROM users WHERE tenant_id = ? ORDER BY full_name LIMIT 500`,
      args: [context.claims.tid],
    });
    return jsonResponse({
      users: result.rows.map((row) => publicUser(row, effectivePermissions(parseRole(text(row.role)), parsePermissions(row.permissions_json)))),
      requestId: context.requestId,
    }, 200, context.requestId);
  } finally { client.close(); }
}

export async function createUser(context: AuthContext, env: Env, body: unknown): Promise<Response> {
  requirePermission(context.claims, "users.manage");
  const source = requireObject(body);
  const username = validateUsername(requiredString(source, "username", 60));
  const email = validateEmail(requiredString(source, "email", 255));
  const fullName = requiredString(source, "fullName", 100);
  const password = validatePassword(source.password);
  const role = parseRole(requiredString(source, "role", 20).toLowerCase());
  const storeId = validateUuid(source.storeId, "storeId");
  const requestedRecordId = source.recordId == null ? null : validateUuid(source.recordId, "recordId");
  const permissions = validateCustomPermissions(source.permissions);
  const client = database(env);
  try {
    await assertStore(client, context.claims.tid, storeId,
      context.claims.sub, context.claims.role === "admin");
    const mirrorResult = await client.execute({
      sql: `SELECT id, payload_json, created_at_utc FROM user_sync_records
            WHERE tenant_id = ? AND deleted_at_utc IS NULL LIMIT 500`,
      args: [context.claims.tid],
    });
    const usernameMirror = mirrorResult.rows.find((row) =>
      userPayloadUsername(row.payload_json) === username);
    const requestedResult = requestedRecordId == null ? null : await client.execute({
      sql: "SELECT id, payload_json, created_at_utc FROM user_sync_records WHERE id = ? AND tenant_id = ?",
      args: [requestedRecordId, context.claims.tid],
    });
    const requestedMirror = requestedResult?.rows[0];
    if (requestedMirror && userPayloadUsername(requestedMirror.payload_json) !== username)
      throw new ApiError(409, "USER_RECORD_MISMATCH", "That synchronized user record belongs to another username.");
    if (usernameMirror && requestedMirror && text(usernameMirror.id) !== text(requestedMirror.id))
      throw new ApiError(409, "DUPLICATE_USER_RECORD", "Multiple synchronized user records use that username.");
    const hash = await hashPassword(password);
    const id = usernameMirror ? text(usernameMirror.id) : requestedRecordId ?? crypto.randomUUID();
    const now = nowIso();
    const mirrorCreatedAt = usernameMirror ? text(usernameMirror.created_at_utc) : now;
    const mirrorPayload = userSyncPayload(username, email, fullName, role, true, mirrorCreatedAt, now);
    await client.batch([
      {
        sql: `INSERT INTO users
              (id, tenant_id, default_store_id, username, username_normalized, email, email_normalized,
               full_name, password_hash, password_version, role, permissions_json, is_active,
               created_at_utc, updated_at_utc, version)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, 1, ?, ?, 1)`,
        args: [id, context.claims.tid, storeId, username, username, email, email, fullName, hash,
          role, JSON.stringify(permissions), now, now],
      },
      {
        sql: `INSERT INTO user_sync_records
              (id, tenant_id, store_id, payload_json, created_at_utc, updated_at_utc, deleted_at_utc,
               version, created_by_user_id, updated_by_user_id, last_modified_device_id)
              VALUES (?, ?, NULL, ?, ?, ?, NULL, 1, ?, ?, ?)
              ON CONFLICT(id) DO UPDATE SET payload_json = excluded.payload_json,
                updated_at_utc = excluded.updated_at_utc,
                deleted_at_utc = NULL,
                version = user_sync_records.version + 1,
                updated_by_user_id = excluded.updated_by_user_id,
                last_modified_device_id = excluded.last_modified_device_id
              WHERE user_sync_records.tenant_id = excluded.tenant_id`,
        args: [id, context.claims.tid, mirrorPayload, mirrorCreatedAt, now,
          context.claims.sub, context.claims.sub, context.claims.did],
      },
      {
        sql: `INSERT INTO user_store_assignments
              (tenant_id, user_id, store_id, is_active, created_at_utc)
              VALUES (?, ?, ?, 1, ?)`,
        args: [context.claims.tid, id, storeId, now],
      },
      {
        sql: `INSERT INTO sync_changes
              (tenant_id, store_id, entity_type, record_id, version, updated_at_utc, deleted_at_utc,
               last_modified_device_id, payload_json)
              SELECT tenant_id, NULL, 'users', id, version, ?, NULL, ?, payload_json
              FROM user_sync_records WHERE id = ? AND tenant_id = ?`,
        args: [now, context.claims.did, id, context.claims.tid],
      },
    ], "write");
    await insertAudit(client, context, "user.created", "user", id, { role });
    return jsonResponse({ id, requestId: context.requestId }, 201, context.requestId);
  } catch (error) {
    if (isUniqueError(error)) throw new ApiError(409, "ACCOUNT_ALREADY_EXISTS", "That username or email is already registered.");
    throw error;
  } finally { client.close(); }
}

export async function updateUser(
  context: AuthContext, env: Env, userId: string, body: unknown,
): Promise<Response> {
  requirePermission(context.claims, "users.manage");
  validateUuid(userId, "userId");
  const source = requireObject(body);
  const client = database(env);
  let transaction: Transaction | undefined;
  try {
    // The final-administrator decision and mutation share one write
    // transaction. Two administrators therefore cannot concurrently pass a
    // stale count check and leave the tenant with no active administrator.
    transaction = await client.transaction("write");
    const targetResult = await transaction.execute({
      sql: `SELECT username, email, full_name, role, is_active, created_at_utc
            FROM users WHERE id = ? AND tenant_id = ?`,
      args: [userId, context.claims.tid],
    });
    const target = targetResult.rows[0];
    if (!target) throw new ApiError(404, "USER_NOT_FOUND", "The user account was not found.");
    if (source.isActive != null && typeof source.isActive !== "boolean")
      throw new ApiError(400, "VALIDATION_ERROR", "isActive must be true or false.");
    const nextActive = source.isActive == null ? booleanValue(target.is_active) : source.isActive === true;
    const nextRole = source.role == null ? parseRole(text(target.role)) : parseRole(String(source.role).toLowerCase());
    if (!nextActive && userId === context.claims.sub)
      throw new ApiError(409, "CURRENT_USER_PROTECTED", "You cannot deactivate the currently signed-in user.");
    const wasAdmin = parseRole(text(target.role)) === "admin" && booleanValue(target.is_active);
    if (wasAdmin && (!nextActive || nextRole !== "admin")) {
      const admins = await transaction.execute({
        sql: "SELECT COUNT(*) AS count FROM users WHERE tenant_id = ? AND role = 'admin' AND is_active = 1",
        args: [context.claims.tid],
      });
      if (integer(admins.rows[0]?.count) <= 1)
        throw new ApiError(409, "FINAL_ADMIN_PROTECTED", "The final active administrator cannot be changed.");
    }
    const fullName = source.fullName == null ? null : requiredString(source, "fullName", 100);
    const permissions = source.permissions == null ? null : validateCustomPermissions(source.permissions);
    const now = nowIso();
    const nextFullName = fullName ?? text(target.full_name);
    const mirrorPayload = userSyncPayload(
      text(target.username), text(target.email), nextFullName, nextRole, nextActive,
      text(target.created_at_utc), now,
    );
    await transaction.batch([
      {
        sql: `UPDATE users SET full_name = COALESCE(?, full_name), role = ?,
                     permissions_json = COALESCE(?, permissions_json), is_active = ?,
                     updated_at_utc = ?, version = version + 1
              WHERE id = ? AND tenant_id = ?`,
        args: [fullName, nextRole, permissions == null ? null : JSON.stringify(permissions), nextActive ? 1 : 0,
          now, userId, context.claims.tid],
      },
      {
        sql: `INSERT INTO user_sync_records
              (id, tenant_id, store_id, payload_json, created_at_utc, updated_at_utc, deleted_at_utc,
               version, created_by_user_id, updated_by_user_id, last_modified_device_id)
              VALUES (?, ?, NULL, ?, ?, ?, NULL, 1, ?, ?, ?)
              ON CONFLICT(id) DO UPDATE SET payload_json = excluded.payload_json,
                updated_at_utc = excluded.updated_at_utc,
                version = user_sync_records.version + 1,
                updated_by_user_id = excluded.updated_by_user_id,
                last_modified_device_id = excluded.last_modified_device_id
              WHERE user_sync_records.tenant_id = excluded.tenant_id`,
        args: [userId, context.claims.tid, mirrorPayload, text(target.created_at_utc), now,
          context.claims.sub, context.claims.sub, context.claims.did],
      },
      {
        sql: `INSERT INTO sync_changes
              (tenant_id, store_id, entity_type, record_id, version, updated_at_utc, deleted_at_utc,
               last_modified_device_id, payload_json)
              SELECT tenant_id, NULL, 'users', id, version, ?, NULL, ?, payload_json
              FROM user_sync_records WHERE id = ? AND tenant_id = ?`,
        args: [now, context.claims.did, userId, context.claims.tid],
      },
    ]);
    if (!nextActive) {
      await transaction.batch([
        { sql: "UPDATE login_sessions SET revoked_at_utc = ?, revoke_reason = 'user_disabled' WHERE tenant_id = ? AND user_id = ? AND revoked_at_utc IS NULL", args: [now, context.claims.tid, userId] },
        { sql: `UPDATE refresh_tokens SET revoked_at_utc = ? WHERE session_id IN
                (SELECT id FROM login_sessions WHERE tenant_id = ? AND user_id = ?) AND revoked_at_utc IS NULL`, args: [now, context.claims.tid, userId] },
      ]);
    }
    const auditAction = booleanValue(target.is_active) && !nextActive
      ? "user.deactivated"
      : parseRole(text(target.role)) !== nextRole || permissions != null
        ? "user.permission_changed"
        : "user.updated";
    await insertAudit(transaction, context, auditAction, "user", userId,
      { role: nextRole, isActive: nextActive });
    await transaction.commit();
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } catch (error) {
    if (transaction) await safeRollback(transaction);
    throw error;
  } finally {
    transaction?.close();
    client.close();
  }
}

export async function registerDevice(context: AuthContext, env: Env, body: unknown): Promise<Response> {
  const source = requireObject(body);
  const device = validateDevice(source.device ?? source);
  const requestedStoreId = source.storeId == null ? null : validateUuid(source.storeId, "storeId");
  if (device.id !== context.claims.did) throw new ApiError(403, "DEVICE_MISMATCH", "The device identity does not match this session.");
  const client = database(env);
  try {
    let storeId = requestedStoreId;
    if (!storeId) {
      const user = await client.execute({
        sql: "SELECT default_store_id FROM users WHERE id = ? AND tenant_id = ? AND is_active = 1",
        args: [context.claims.sub, context.claims.tid],
      });
      storeId = nullableText(user.rows[0]?.default_store_id);
    }
    if (storeId) await assertStore(client, context.claims.tid, storeId,
      context.claims.sub, context.claims.role === "admin");
    await upsertDevice(client, context.claims.tid, context.claims.sub, device, storeId);
    await insertAudit(client, context, "device.registered", "device", device.id,
      { name: device.name, assignedStoreId: storeId }, storeId);
    return jsonResponse({ ok: true, requestId: context.requestId }, 200, context.requestId);
  } finally { client.close(); }
}

export async function recordClientAudit(
  context: AuthContext,
  env: Env,
  body: unknown,
): Promise<Response> {
  const source = requireObject(body);
  const action = requiredString(source, "action", 80);
  const allowed = new Set(["data.migration", "backup.restore_reconciled", "sync.conflict_resolved"]);
  if (!allowed.has(action)) throw new ApiError(400, "VALIDATION_ERROR", "The audit action is not allowed.");
  if (action !== "sync.conflict_resolved") requirePermission(context.claims, "settings.manage");
  else requirePermission(context.claims, "sync.use");
  const affectedType = requiredString(source, "affectedType", 80);
  const affectedId = validateUuid(source.affectedId, "affectedId");
  const storeId = source.storeId == null ? null : validateUuid(source.storeId, "storeId");
  const client = database(env);
  try {
    if (storeId) await assertStore(client, context.claims.tid, storeId,
      context.claims.sub, context.claims.role === "admin");
    await insertAudit(client, context, action, affectedType, affectedId, {}, storeId);
    return jsonResponse({ ok: true, requestId: context.requestId }, 201, context.requestId);
  } finally { client.close(); }
}

async function createSession(
  client: Client,
  env: Env,
  requestId: string,
  account: AccountRow,
  device: DeviceInput,
  organizationName: string,
  storeName: string,
  storeCode: string,
): Promise<Record<string, unknown>> {
  const deviceWasNew = await upsertDevice(client, account.tenantId, account.userId, device, account.storeId);
  const sessionId = crypto.randomUUID();
  const familyId = crypto.randomUUID();
  const refreshId = crypto.randomUUID();
  const refreshPlaintext = `${refreshId}.${randomToken(32)}`;
  const refreshHash = await hashRefreshToken(refreshPlaintext, env);
  const refreshExpiresAtUtc = refreshExpiry(env);
  const now = nowIso();
  const permissions = effectivePermissions(account.role, account.customPermissions);
  const sessionExpiresAtUtc = refreshExpiresAtUtc;
  const access = await signAccessToken({
    sub: account.userId, tid: account.tenantId, sid: sessionId, did: device.id,
    role: account.role, permissions, pv: account.passwordVersion,
  }, env);
  const statements = [
    {
      sql: `INSERT INTO login_sessions
            (id, tenant_id, user_id, device_id, created_at_utc, last_login_at_utc,
             last_seen_at_utc, expires_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      args: [sessionId, account.tenantId, account.userId, device.id, now, now, now, sessionExpiresAtUtc],
    },
    {
      sql: `INSERT INTO refresh_tokens
            (id, session_id, family_id, parent_token_id, token_hash, created_at_utc, expires_at_utc)
            VALUES (?, ?, ?, NULL, ?, ?, ?)`,
      args: [refreshId, sessionId, familyId, refreshHash, now, refreshExpiresAtUtc],
    },
    {
      sql: `INSERT INTO audit_logs
            (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
             affected_id, request_id, metadata_json)
            VALUES (?, ?, ?, ?, ?, ?, 'auth.login', 'session', ?, ?, '{}')`,
      args: [crypto.randomUUID(), account.tenantId, account.storeId, account.userId, device.id, now, sessionId, requestId],
    },
  ];
  if (deviceWasNew) {
    statements.push({
      sql: `INSERT INTO audit_logs
            (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
             affected_id, request_id, metadata_json)
            VALUES (?, ?, ?, ?, ?, ?, 'device.registered', 'device', ?, ?, '{}')`,
      args: [crypto.randomUUID(), account.tenantId, account.storeId, account.userId,
        device.id, now, device.id, requestId],
    });
  }
  await client.batch(statements, "write");
  return {
    tokens: {
      accessToken: access.token, refreshToken: refreshPlaintext,
      accessTokenExpiresAtUtc: access.expiresAtUtc, refreshTokenExpiresAtUtc: refreshExpiresAtUtc,
      sessionId,
    },
    user: {
      id: account.userId, tenantId: account.tenantId, username: account.username,
      email: account.email, fullName: account.fullName, role: roleNumber(account.role),
      isActive: true, permissions,
    },
    organizationId: account.tenantId,
    organizationName,
    store: { id: account.storeId, tenantId: account.tenantId, name: storeName, code: storeCode, isActive: true },
    apiVersion: apiVersion(env), schemaVersion: schemaVersion(env), deviceId: device.id,
  };
}

async function upsertDevice(
  client: Client,
  tenantId: string,
  userId: string,
  device: DeviceInput,
  assignedStoreId: string | null,
): Promise<boolean> {
  const existing = await client.execute({ sql: "SELECT tenant_id, status FROM registered_devices WHERE id = ?", args: [device.id] });
  const row = existing.rows[0];
  if (row && text(row.tenant_id) !== tenantId)
    throw new ApiError(403, "DEVICE_TENANT_MISMATCH", "This device is registered to another organization.");
  if (row && text(row.status) === "revoked") throw new ApiError(401, "DEVICE_REVOKED", "This device has been revoked.");
  const now = nowIso();
  await client.execute({
    sql: `INSERT INTO registered_devices
          (id, tenant_id, registered_by_user_id, assigned_store_id, name, operating_system, machine_name, status,
           first_registered_at_utc, last_login_at_utc, updated_at_utc)
          VALUES (?, ?, ?, ?, ?, ?, ?, 'active', ?, ?, ?)
          ON CONFLICT(id) DO UPDATE SET name = excluded.name, operating_system = excluded.operating_system,
             machine_name = excluded.machine_name, assigned_store_id = excluded.assigned_store_id,
             last_login_at_utc = excluded.last_login_at_utc,
             updated_at_utc = excluded.updated_at_utc
          WHERE registered_devices.tenant_id = excluded.tenant_id AND registered_devices.status = 'active'`,
    args: [device.id, tenantId, userId, assignedStoreId, device.name, device.operatingSystem ?? "Windows",
      device.machineName ?? null, now, now, now],
  });
  return row == null;
}

export async function insertAudit(
  client: Pick<Client, "execute">,
  context: AuthContext,
  action: string,
  affectedType: string,
  affectedId: string,
  metadata: Record<string, unknown>,
  storeId?: string | null,
): Promise<void> {
  await client.execute({
    sql: `INSERT INTO audit_logs
          (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
           affected_id, request_id, metadata_json)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    args: [crypto.randomUUID(), context.claims.tid, storeId ?? null, context.claims.sub,
      context.claims.did, nowIso(), action, affectedType, affectedId, context.requestId,
      JSON.stringify(metadata)],
  });
}

export async function assertStore(
  client: Client,
  tenantId: string,
  storeId: string,
  userId?: string,
  mayAccessAllStores = false,
): Promise<void> {
  const result = await client.execute({
    sql: `SELECT 1 FROM stores s
          WHERE s.id = ? AND s.tenant_id = ? AND s.is_active = 1
            AND (? = 1 OR EXISTS (
              SELECT 1 FROM user_store_assignments usa
              WHERE usa.tenant_id = s.tenant_id AND usa.store_id = s.id
                AND usa.user_id = ? AND usa.is_active = 1
            ))`,
    args: [storeId, tenantId, mayAccessAllStores ? 1 : 0, userId ?? ""],
  });
  if (!result.rows.length) throw new ApiError(403, "STORE_ACCESS_DENIED", "The selected store is unavailable.");
}

function userFromRow(row: Row): AccountRow {
  return {
    userId: text(row.id), tenantId: text(row.tenant_id), storeId: text(row.default_store_id),
    username: text(row.username), email: text(row.email), fullName: text(row.full_name),
    role: parseRole(text(row.role)), customPermissions: parsePermissions(row.permissions_json),
    passwordVersion: integer(row.password_version),
  };
}

function publicUser(row: Row, permissions: string[]): Record<string, unknown> {
  const role = parseRole(text(row.role));
  return {
    id: text(row.id), tenantId: text(row.tenant_id), username: text(row.username), email: text(row.email),
    fullName: text(row.full_name), role: roleNumber(role), roleName: role,
    isActive: booleanValue(row.is_active), permissions, version: integer(row.version),
  };
}

function publicStore(row: Row): Record<string, unknown> {
  return {
    id: text(row.store_id), tenantId: text(row.tenant_id), name: text(row.store_name),
    code: text(row.store_code), isActive: true,
  };
}

function userSyncPayload(
  username: string,
  email: string,
  fullName: string,
  role: Role,
  isActive: boolean,
  createdAtUtc: string,
  updatedAtUtc: string,
): string {
  return JSON.stringify({
    username,
    fullName,
    role: roleNumber(role),
    isActive,
    email,
    createdAt: createdAtUtc,
    updatedAt: updatedAtUtc,
  });
}

function userPayloadUsername(value: unknown): string {
  try {
    const payload = JSON.parse(text(value)) as Record<string, unknown>;
    return typeof payload.username === "string" ? payload.username.trim().toLowerCase() : "";
  } catch { return ""; }
}

export function parseRole(value: string): Role {
  if (value === "admin" || value === "manager" || value === "cashier") return value;
  throw new ApiError(400, "VALIDATION_ERROR", "The user role is invalid.");
}

function roleNumber(role: Role): number {
  return role === "admin" ? 2 : role === "manager" ? 1 : 0;
}

function parsePermissions(value: unknown): string[] {
  try {
    const parsed = JSON.parse(text(value));
    return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === "string") : [];
  } catch { return []; }
}

function refreshExpiry(env: Env): string {
  const days = clampNumber(env.REFRESH_TOKEN_TTL_DAYS, 30, 1, 90);
  return new Date(Date.now() + days * 86_400_000).toISOString();
}

async function hashRefreshToken(token: string, env: Env): Promise<string> {
  if (!env.REFRESH_TOKEN_SECRET || env.REFRESH_TOKEN_SECRET.length < 32)
    throw new ApiError(500, "AUTHENTICATION_CONFIGURATION_ERROR", "The authentication secrets are missing or too short.");
  return sha256(`${token}.${env.REFRESH_TOKEN_SECRET}`);
}

function safeHashEqual(left: string, right: string): boolean {
  try { return timingSafeEqual(base64UrlDecode(left), base64UrlDecode(right)); }
  catch { return false; }
}

function apiVersion(env: Env): number { return Number(env.API_VERSION ?? "1"); }
function schemaVersion(env: Env): number { return Number(env.SCHEMA_VERSION ?? "4"); }

function assertAuthSecrets(env: Env): void {
  if (!env.JWT_SIGNING_SECRET || env.JWT_SIGNING_SECRET.length < 32 ||
      !env.REFRESH_TOKEN_SECRET || env.REFRESH_TOKEN_SECRET.length < 32)
    throw new ApiError(500, "AUTHENTICATION_CONFIGURATION_ERROR", "The authentication secrets are missing or too short.");
}

function assertClientSchema(source: Record<string, unknown>, env: Env): void {
  const clientSchema = Number(source.clientSchemaVersion);
  const minimum = Number(env.MINIMUM_CLIENT_SCHEMA_VERSION ?? "4");
  const server = schemaVersion(env);
  if (!Number.isInteger(clientSchema) || clientSchema < minimum)
    throw new ApiError(409, "CLIENT_VERSION_INCOMPATIBLE", "Update PosApp before signing in.");
  if (clientSchema > server)
    throw new ApiError(409, "SERVER_VERSION_INCOMPATIBLE", "The online service must be upgraded before this client can sign in.");
}

function invalidCredentials(): ApiError {
  return new ApiError(401, "INVALID_CREDENTIALS", "The username, email, or password is incorrect.");
}

function enforceLoginRateLimit(key: string): void {
  const now = Date.now();
  const entry = loginWindows.get(key);
  if (entry && entry.resetAt > now && entry.count >= 10)
    throw new ApiError(429, "LOGIN_RATE_LIMITED", "Too many sign-in attempts. Try again later.");
  if (entry && entry.resetAt <= now) loginWindows.delete(key);
}

function recordLoginFailure(key: string): void {
  const now = Date.now();
  const entry = loginWindows.get(key);
  loginWindows.set(key, !entry || entry.resetAt <= now
    ? { count: 1, resetAt: now + 15 * 60_000 }
    : { count: entry.count + 1, resetAt: entry.resetAt });
  if (loginWindows.size > 5_000) {
    for (const [candidate, value] of loginWindows) if (value.resetAt <= now) loginWindows.delete(candidate);
    while (loginWindows.size > 5_000) {
      const oldest = loginWindows.keys().next().value as string | undefined;
      if (oldest == null) break;
      loginWindows.delete(oldest);
    }
  }
}

function clearLoginFailures(key: string): void { loginWindows.delete(key); }

async function enforcePersistentLoginRateLimit(client: Client, keyHash: string): Promise<void> {
  const result = await client.execute({
    sql: "SELECT attempt_count, blocked_until_utc, updated_at_utc FROM login_attempts WHERE key_hash = ?",
    args: [keyHash],
  });
  const row = result.rows[0];
  if (!row) return;
  const now = Date.now();
  if (row.blocked_until_utc != null && Date.parse(text(row.blocked_until_utc)) > now)
    throw new ApiError(429, "LOGIN_RATE_LIMITED", "Too many sign-in attempts. Try again later.");
  if (Date.parse(text(row.updated_at_utc)) <= now - 15 * 60_000)
    await client.execute({ sql: "DELETE FROM login_attempts WHERE key_hash = ?", args: [keyHash] });
}

async function recordPersistentLoginFailure(client: Client, keyHash: string): Promise<void> {
  const now = nowIso();
  const windowStart = new Date(Date.now() - 15 * 60_000).toISOString();
  const blockedUntil = new Date(Date.now() + 15 * 60_000).toISOString();
  await client.execute({
    sql: `INSERT INTO login_attempts
          (key_hash, attempt_count, first_attempt_at_utc, blocked_until_utc, updated_at_utc)
          VALUES (?, 1, ?, NULL, ?)
          ON CONFLICT(key_hash) DO UPDATE SET
            attempt_count = CASE WHEN login_attempts.updated_at_utc <= ? THEN 1
                                 ELSE login_attempts.attempt_count + 1 END,
            first_attempt_at_utc = CASE WHEN login_attempts.updated_at_utc <= ? THEN excluded.first_attempt_at_utc
                                        ELSE login_attempts.first_attempt_at_utc END,
            blocked_until_utc = CASE
              WHEN login_attempts.updated_at_utc > ? AND login_attempts.attempt_count + 1 >= 10 THEN ?
              WHEN login_attempts.updated_at_utc <= ? THEN NULL
              ELSE login_attempts.blocked_until_utc END,
            updated_at_utc = excluded.updated_at_utc`,
    args: [keyHash, now, now, windowStart, windowStart, windowStart, blockedUntil, windowStart],
  });
}

async function auditFailedLogin(
  client: Client,
  requestId: string,
  identifier: string,
  deviceId: string,
  account?: Row,
  reason = "invalid_credentials",
): Promise<void> {
  // Store only a one-way identifier hash, never the submitted identifier or password.
  const now = nowIso();
  const statements = [{
    sql: `INSERT INTO security_events
          (id, timestamp_utc, action, identifier_hash, device_id, request_id)
          VALUES (?, ?, 'auth.login_failed', ?, ?, ?)`,
    args: [crypto.randomUUID(), now, await sha256(identifier), deviceId, requestId],
  }];
  if (account) {
    statements.push({
      sql: `INSERT INTO audit_logs
            (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action,
             affected_type, affected_id, request_id, metadata_json)
            VALUES (?, ?, ?, ?, ?, ?, 'auth.login_failed', 'user', ?, ?, ?)`,
      args: [crypto.randomUUID(), text(account.tenant_id), text(account.default_store_id),
        text(account.id), deviceId, now, text(account.id), requestId, JSON.stringify({ reason })],
    });
  }
  await client.batch(statements, "write");
}

function isUniqueError(error: unknown): boolean {
  return error instanceof Error && /unique|constraint/i.test(error.message);
}

async function safeRollback(transaction: Transaction): Promise<void> {
  try { await transaction.rollback(); } catch { /* original error wins */ }
}

interface AccountRow {
  userId: string;
  tenantId: string;
  storeId: string;
  username: string;
  email: string;
  fullName: string;
  role: Role;
  customPermissions: string[];
  passwordVersion: number;
}
