import type { Client } from "@libsql/client/web";
import { passwordCryptoSelfTest, signAccessToken, verifyAccessToken, sha256 } from "./crypto";
import { database, inspectDatabaseReadiness, integer, nowIso } from "./db";
import { jsonResponse, safeProviderErrorCode } from "./errors";
import type { Env } from "./types";

export type DiagnosticStatus = "pass" | "fail";

export interface DiagnosticCheck {
  id: string;
  name: string;
  status: DiagnosticStatus;
  message: string;
  code?: string;
  stage?: string;
  providerCode?: string | null;
}

export interface CloudDiagnosticReport {
  service: string;
  deploymentVersion: string;
  checkedAtUtc: string;
  requestId: string;
  ready: boolean;
  accountCreationReady: boolean;
  apiVersion: number;
  schemaVersion: number;
  minimumClientSchemaVersion: number;
  checks: DiagnosticCheck[];
}

const signupSchema: Readonly<Record<string, readonly string[]>> = {
  schema_migrations: ["version", "name", "applied_at_utc"],
  organizations: ["id", "name", "is_active", "created_at_utc", "updated_at_utc", "schema_version"],
  stores: ["id", "tenant_id", "name", "code", "is_active", "created_at_utc", "updated_at_utc", "version"],
  users: [
    "id", "tenant_id", "default_store_id", "username", "username_normalized", "email",
    "email_normalized", "full_name", "password_hash", "password_version", "role",
    "permissions_json", "is_active", "created_at_utc", "updated_at_utc", "version",
  ],
  user_store_assignments: ["tenant_id", "user_id", "store_id", "is_active", "created_at_utc"],
  registered_devices: [
    "id", "tenant_id", "registered_by_user_id", "assigned_store_id", "name", "operating_system",
    "machine_name", "status", "first_registered_at_utc", "last_login_at_utc", "updated_at_utc",
  ],
  login_sessions: [
    "id", "tenant_id", "user_id", "device_id", "created_at_utc", "last_login_at_utc",
    "last_seen_at_utc", "expires_at_utc",
  ],
  refresh_tokens: [
    "id", "session_id", "family_id", "parent_token_id", "token_hash", "created_at_utc", "expires_at_utc",
  ],
  audit_logs: [
    "id", "tenant_id", "store_id", "user_id", "device_id", "timestamp_utc", "action",
    "affected_type", "affected_id", "request_id", "metadata_json",
  ],
  user_sync_records: [
    "id", "tenant_id", "store_id", "payload_json", "created_at_utc", "updated_at_utc",
    "deleted_at_utc", "version", "created_by_user_id", "updated_by_user_id", "last_modified_device_id",
  ],
  sync_changes: [
    "cursor", "tenant_id", "store_id", "entity_type", "record_id", "version", "updated_at_utc",
    "deleted_at_utc", "last_modified_device_id", "payload_json",
  ],
};

export async function runCloudDiagnostics(env: Env, requestId: string): Promise<CloudDiagnosticReport> {
  const expectedSchemaVersion = Number(env.SCHEMA_VERSION ?? "4");
  const checks: DiagnosticCheck[] = [];

  const databaseState = await inspectDatabaseReadiness(env, expectedSchemaVersion);
  checks.push({
    id: "database-connectivity",
    name: "Turso database connectivity",
    status: databaseState.configured && databaseState.reachable ? "pass" : "fail",
    message: !databaseState.configured
      ? "The Turso database bindings are missing."
      : databaseState.reachable
        ? "The Worker can connect to Turso."
        : "The Worker cannot connect to Turso.",
    code: !databaseState.configured ? "DATABASE_CONFIGURATION_ERROR" :
      databaseState.reachable ? undefined : "DATABASE_UNREACHABLE",
  });

  checks.push({
    id: "schema-version",
    name: "Database migration version",
    status: databaseState.schemaReady ? "pass" : "fail",
    message: databaseState.schemaReady
      ? `Database schema version ${databaseState.schemaVersion} matches the required version.`
      : `Database schema version ${databaseState.schemaVersion} is below required version ${expectedSchemaVersion}.`,
    code: databaseState.schemaReady ? undefined : "DATABASE_SCHEMA_NOT_READY",
  });

  const authConfigured = Boolean(
    env.JWT_SIGNING_SECRET?.length >= 32 && env.REFRESH_TOKEN_SECRET?.length >= 32 &&
    env.PASSWORD_PEPPER_SECRET?.length >= 32,
  );
  let authReady = false;
  if (!authConfigured) {
    checks.push({
      id: "authentication-crypto",
      name: "Authentication cryptography",
      status: "fail",
      message: "JWT, refresh-token, or password-pepper secrets are missing or too short.",
      code: "AUTHENTICATION_CONFIGURATION_ERROR",
    });
  } else {
    let authenticationStage = "derive password verifier";
    try {
      const passwordReady = await passwordCryptoSelfTest(env);
      authenticationStage = "sign access token";
      const signed = await signAccessToken({
        sub: crypto.randomUUID(), tid: crypto.randomUUID(), sid: crypto.randomUUID(),
        did: crypto.randomUUID(), role: "admin", permissions: ["*"], pv: 1,
      }, env);
      authenticationStage = "verify access token";
      const verified = await verifyAccessToken(signed.token, env);
      authenticationStage = "hash refresh token";
      await sha256(`diagnostic.${env.REFRESH_TOKEN_SECRET}`);
      authReady = passwordReady && verified.role === "admin" && verified.pv === 1;
      checks.push({
        id: "authentication-crypto",
        name: "Authentication cryptography",
        status: authReady ? "pass" : "fail",
        message: authReady
          ? "The dedicated-pepper PBKDF2 password verifier (12,000 rounds), access-token signing and verification, and refresh-token hashing work."
          : "Authentication cryptography returned an invalid verification result.",
        code: authReady ? undefined : "AUTHENTICATION_DIAGNOSTIC_FAILED",
        stage: authReady ? undefined : authenticationStage,
      });
    } catch (error) {
      checks.push({
        id: "authentication-crypto",
        name: "Authentication cryptography",
        status: "fail",
        message: "Authentication cryptography could not complete its self-test.",
        code: "AUTHENTICATION_DIAGNOSTIC_FAILED",
        stage: authenticationStage,
        providerCode: safeProviderErrorCode(error),
      });
    }
  }

  let schemaShapeReady = false;
  if (databaseState.configured && databaseState.reachable) {
    const client = database(env);
    try {
      const schema = await inspectSignupSchema(client);
      schemaShapeReady = schema.missing.length === 0;
      checks.push({
        id: "signup-schema",
        name: "Organization-creation tables and columns",
        status: schemaShapeReady ? "pass" : "fail",
        message: schemaShapeReady
          ? "Every table and column required for organization creation is present."
          : `The cloud schema is incomplete: ${schema.missing.join(", ")}.`,
        code: schemaShapeReady ? undefined : "DATABASE_SCHEMA_INCOMPLETE",
      });
    } catch (error) {
      checks.push({
        id: "signup-schema",
        name: "Organization-creation tables and columns",
        status: "fail",
        message: "The Worker could not inspect the organization-creation schema.",
        code: "DATABASE_SCHEMA_INSPECTION_FAILED",
        providerCode: safeProviderErrorCode(error),
      });
    } finally {
      client.close();
    }
  } else {
    checks.push({
      id: "signup-schema",
      name: "Organization-creation tables and columns",
      status: "fail",
      message: "Schema inspection was skipped because the database is unavailable.",
      code: "DATABASE_UNAVAILABLE",
    });
  }

  let accountCreationReady = false;
  if (databaseState.schemaReady && schemaShapeReady && authReady) {
    const result = await runOrganizationCreationPreflight(env, requestId);
    accountCreationReady = result.status === "pass";
    checks.push(result);
  } else {
    checks.push({
      id: "organization-creation-preflight",
      name: "Organization creation preflight",
      status: "fail",
      message: "The write-and-rollback preflight was skipped until the preceding checks pass.",
      code: "PREFLIGHT_BLOCKED",
    });
  }

  const ready = checks.every((check) => check.status === "pass") && accountCreationReady;
  return {
    service: "PosApp Cloud API",
    deploymentVersion: env.DEPLOYMENT_VERSION ?? "2.0.15",
    checkedAtUtc: nowIso(),
    requestId,
    ready,
    accountCreationReady,
    apiVersion: Number(env.API_VERSION ?? "1"),
    schemaVersion: expectedSchemaVersion,
    minimumClientSchemaVersion: Number(env.MINIMUM_CLIENT_SCHEMA_VERSION ?? "4"),
    checks,
  };
}

export async function diagnosticsJson(env: Env, requestId: string): Promise<Response> {
  const report = await runCloudDiagnostics(env, requestId);
  // Readiness is part of the JSON contract. Always return JSON with HTTP 200 so
  // browsers and CI can read the failed stage instead of discarding the body
  // through curl --fail when a check reports not ready.
  return jsonResponse(report, 200, requestId);
}

export async function diagnosticsPage(request: Request, env: Env, requestId: string): Promise<Response> {
  const origin = new URL(request.url).origin;
  const deploymentVersion = env.DEPLOYMENT_VERSION ?? "2.0.15";
  const nonce = crypto.randomUUID().replace(/-/g, "");
  const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <meta name="color-scheme" content="light dark">
  <meta name="posapp-status-page" content="true">
  <meta name="posapp-deployment-version" content="${escapeHtml(deploymentVersion)}">
  <meta name="posapp-diagnostics-endpoint" content="/api/v1/diagnostics">
  <title>PosApp Cloud status</title>
  <style>
    :root { font-family: Inter, ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif; color-scheme: light dark; }
    * { box-sizing: border-box; }
    body { margin: 0; min-height: 100vh; background: #0b1220; color: #e8eef8; }
    main { width: min(900px, calc(100% - 28px)); margin: 0 auto; padding: 38px 0 54px; }
    .hero, .check, .meta { background: #121d2f; border: 1px solid #2a3a53; border-radius: 18px; }
    .hero { padding: 28px; margin-bottom: 18px; }
    .brand { margin: 0 0 6px; font-size: clamp(1.8rem, 5vw, 2.8rem); }
    .subtitle { margin: 0; color: #aebbd0; line-height: 1.55; }
    .status { display: inline-flex; align-items: center; gap: 8px; margin-top: 20px; padding: 8px 13px; border-radius: 999px; font-weight: 700; }
    .status.checking { color: #dce8ff; background: #24344d; border: 1px solid #526b91; }
    .status.ready { color: #b9f6cf; background: #123b2a; border: 1px solid #25764e; }
    .status.attention { color: #ffd1d1; background: #431d27; border: 1px solid #8e394e; }
    .checks { display: grid; gap: 12px; }
    .check { display: grid; grid-template-columns: 38px 1fr; gap: 13px; padding: 18px; }
    .check.pass { border-color: #265b45; }
    .check.fail { border-color: #7d3446; }
    .icon { width: 34px; height: 34px; border-radius: 50%; display: grid; place-items: center; font-weight: 900; }
    .pass .icon { background: #153f2d; color: #89e2ad; }
    .fail .icon { background: #49202b; color: #ff9dad; }
    h2 { margin: 2px 0 6px; font-size: 1rem; }
    p { margin: 0; color: #b7c3d5; line-height: 1.5; }
    .detail { margin-top: 7px; font-size: .88rem; }
    code { color: #dce8ff; overflow-wrap: anywhere; }
    .meta { margin-top: 18px; padding: 18px; display: grid; gap: 8px; font-size: .9rem; color: #b7c3d5; }
    .actions { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 18px; }
    button, a.button { border: 0; color: #fff; background: #3478f6; text-decoration: none; padding: 11px 15px; border-radius: 11px; font: inherit; font-weight: 700; cursor: pointer; }
    a.secondary { background: #24344d; }
    button:disabled { opacity: .65; cursor: wait; }
    @media (prefers-color-scheme: light) {
      body { background: #eef3f9; color: #122033; }
      .hero, .check, .meta { background: #fff; border-color: #d1dbe8; }
      .subtitle, p, .meta { color: #506176; }
      code { color: #20314a; }
      a.secondary { background: #52657e; }
    }
  </style>
</head>
<body>
  <main>
    <section class="hero">
      <h1 class="brand">PosApp Cloud API</h1>
      <p class="subtitle">Public deployment diagnostics. The check performs one safe atomic organization-creation batch and forces it to roll back, so it verifies more than basic connectivity.</p>
      <div id="status" class="status checking" role="status">Checking deployment…</div>
      <div class="actions">
        <button id="run" type="button">Run diagnostics again</button>
        <a class="button secondary" href="/api/v1/diagnostics">View JSON</a>
        <a class="button secondary" href="/api/v1/meta">View metadata</a>
      </div>
    </section>
    <section id="checks" class="checks" aria-live="polite">
      <article class="check">
        <div class="icon" aria-hidden="true">…</div>
        <div><h2>End-to-end verification</h2><p>Testing the Free-plan password verifier, tokens, Turso schema, atomic organization provisioning, and rollback.</p></div>
      </article>
    </section>
    <section class="meta">
      <div><strong>Worker:</strong> <span id="version">${escapeHtml(deploymentVersion)}</span></div>
      <div><strong>Account creation:</strong> <span id="account">checking</span></div>
      <div><strong>API base URL:</strong> <code>${escapeHtml(origin)}</code></div>
      <div><strong>Checked:</strong> <span id="checked">pending</span></div>
      <div><strong>Request ID:</strong> <code id="request">${escapeHtml(requestId)}</code></div>
      <div>No credentials, tokens, database URLs, SQL statements, or user data are displayed.</div>
    </section>
  </main>
  <script nonce="${nonce}">
    const statusNode = document.getElementById('status');
    const checksNode = document.getElementById('checks');
    const runButton = document.getElementById('run');
    const versionNode = document.getElementById('version');
    const accountNode = document.getElementById('account');
    const checkedNode = document.getElementById('checked');
    const requestNode = document.getElementById('request');

    function addText(parent, tag, text, className) {
      const node = document.createElement(tag);
      if (className) node.className = className;
      node.textContent = text;
      parent.appendChild(node);
      return node;
    }

    function renderCheck(check) {
      const article = document.createElement('article');
      article.className = 'check ' + (check.status === 'pass' ? 'pass' : 'fail');
      const icon = addText(article, 'div', check.status === 'pass' ? '✓' : '!', 'icon');
      icon.setAttribute('aria-hidden', 'true');
      const body = document.createElement('div');
      addText(body, 'h2', String(check.name || 'Diagnostic check'));
      addText(body, 'p', String(check.message || 'No diagnostic message was returned.'));
      if (check.stage) addText(body, 'p', 'Failed stage: ' + check.stage, 'detail');
      if (check.code) addText(body, 'p', 'Code: ' + check.code, 'detail');
      if (check.providerCode) addText(body, 'p', 'Provider code: ' + check.providerCode, 'detail');
      article.appendChild(body);
      checksNode.appendChild(article);
    }

    async function runDiagnostics() {
      runButton.disabled = true;
      statusNode.className = 'status checking';
      statusNode.textContent = 'Checking deployment…';
      accountNode.textContent = 'checking';
      checksNode.replaceChildren();
      try {
        const response = await fetch('/api/v1/diagnostics?time=' + Date.now(), {
          cache: 'no-store', headers: { accept: 'application/json' }
        });
        const contentType = response.headers.get('content-type') || '';
        if (!contentType.includes('application/json')) {
          throw new Error('The diagnostic endpoint returned HTTP ' + response.status + ' instead of JSON. Check Cloudflare Worker errors and CPU limits.');
        }
        const report = await response.json();
        statusNode.className = 'status ' + (report.ready === true ? 'ready' : 'attention');
        statusNode.textContent = report.ready === true ? 'Ready' : 'Needs attention';
        versionNode.textContent = report.deploymentVersion || versionNode.textContent;
        accountNode.textContent = report.accountCreationReady === true ? 'verified' : 'not verified';
        checkedNode.textContent = report.checkedAtUtc || 'unknown';
        requestNode.textContent = report.requestId || response.headers.get('x-request-id') || 'unknown';
        const checks = Array.isArray(report.checks) ? report.checks : [];
        if (checks.length === 0) renderCheck({ status: 'fail', name: 'Diagnostic response', message: 'No checks were returned.', code: 'INVALID_DIAGNOSTIC_RESPONSE' });
        else checks.forEach(renderCheck);
      } catch (error) {
        statusNode.className = 'status attention';
        statusNode.textContent = 'Needs attention';
        accountNode.textContent = 'not verified';
        checkedNode.textContent = new Date().toISOString();
        renderCheck({
          status: 'fail',
          name: 'Diagnostic request',
          message: error instanceof Error ? error.message : 'The diagnostic request failed.',
          code: 'DIAGNOSTIC_REQUEST_FAILED'
        });
      } finally {
        runButton.disabled = false;
      }
    }

    runButton.addEventListener('click', runDiagnostics);
    runDiagnostics();
  </script>
</body>
</html>`;

  return new Response(html, {
    status: 200,
    headers: {
      "content-type": "text/html; charset=utf-8",
      "cache-control": "no-store",
      "x-request-id": requestId,
      "x-posapp-status-page": "1",
      "x-posapp-deployment-version": deploymentVersion,
      "x-content-type-options": "nosniff",
      "referrer-policy": "no-referrer",
      "content-security-policy": `default-src 'none'; connect-src 'self'; script-src 'nonce-${nonce}'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`,
    },
  });
}

async function inspectSignupSchema(client: Client): Promise<{ missing: string[] }> {
  const tables = await client.execute("SELECT name FROM sqlite_master WHERE type = 'table'");
  const existingTables = new Set(tables.rows.map((row) => String(row.name)));
  const missing: string[] = [];
  for (const [table, columns] of Object.entries(signupSchema)) {
    if (!existingTables.has(table)) {
      missing.push(`table ${table}`);
      continue;
    }
    // Table names come only from the constant map above.
    const info = await client.execute(`PRAGMA table_info(\"${table}\")`);
    const existingColumns = new Set(info.rows.map((row) => String(row.name)));
    for (const column of columns) {
      if (!existingColumns.has(column)) missing.push(`${table}.${column}`);
    }
  }
  return { missing };
}

async function runOrganizationCreationPreflight(env: Env, requestId: string): Promise<DiagnosticCheck> {
  const client = database(env);
  const tenantId = crypto.randomUUID();
  const storeId = crypto.randomUUID();
  const userId = crypto.randomUUID();
  const deviceId = crypto.randomUUID();
  const sessionId = crypto.randomUUID();
  const familyId = crypto.randomUUID();
  const refreshId = crypto.randomUUID();
  const timestamp = nowIso();
  const expires = new Date(Date.now() + 86_400_000).toISOString();
  const payload = JSON.stringify({
    username: `diagnostic-${userId.slice(0, 8)}`,
    fullName: "PosApp diagnostic user",
    role: 2,
    isActive: true,
    email: `diagnostic-${userId.slice(0, 8)}@example.invalid`,
    createdAt: timestamp,
    updatedAt: timestamp,
  });
  const steps: Array<{ stage: string; sql: string; args: Array<string | number | null> }> = [
    {
      stage: "insert organization",
      sql: `INSERT INTO organizations
            (id, name, is_active, created_at_utc, updated_at_utc, schema_version)
            VALUES (?, ?, 1, ?, ?, ?)`,
      args: [tenantId, "PosApp diagnostics", timestamp, timestamp, Number(env.SCHEMA_VERSION ?? "4")],
    },
    {
      stage: "insert store",
      sql: `INSERT INTO stores
            (id, tenant_id, name, code, is_active, created_at_utc, updated_at_utc, version)
            VALUES (?, ?, ?, ?, 1, ?, ?, 1)`,
      args: [storeId, tenantId, "Diagnostics", "DIAG", timestamp, timestamp],
    },
    {
      stage: "insert administrator",
      sql: `INSERT INTO users
            (id, tenant_id, default_store_id, username, username_normalized, email, email_normalized,
             full_name, password_hash, password_version, role, permissions_json, is_active,
             created_at_utc, updated_at_utc, version)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, 'admin', '[]', 1, ?, ?, 1)`,
      args: [userId, tenantId, storeId, `diagnostic-${userId.slice(0, 8)}`, `diagnostic-${userId.slice(0, 8)}`,
        `diagnostic-${userId.slice(0, 8)}@example.invalid`, `diagnostic-${userId.slice(0, 8)}@example.invalid`,
        "PosApp diagnostic user", "diagnostic-hash", timestamp, timestamp],
    },
    {
      stage: "assign administrator to store",
      sql: `INSERT INTO user_store_assignments
            (tenant_id, user_id, store_id, is_active, created_at_utc)
            VALUES (?, ?, ?, 1, ?)`,
      args: [tenantId, userId, storeId, timestamp],
    },
    {
      stage: "register device",
      sql: `INSERT INTO registered_devices
            (id, tenant_id, registered_by_user_id, assigned_store_id, name, operating_system,
             machine_name, status, first_registered_at_utc, last_login_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, 'active', ?, ?, ?)`,
      args: [deviceId, tenantId, userId, storeId, "PosApp diagnostics", "Cloudflare Worker", null,
        timestamp, timestamp, timestamp],
    },
    {
      stage: "create login session",
      sql: `INSERT INTO login_sessions
            (id, tenant_id, user_id, device_id, created_at_utc, last_login_at_utc,
             last_seen_at_utc, expires_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
      args: [sessionId, tenantId, userId, deviceId, timestamp, timestamp, timestamp, expires],
    },
    {
      stage: "create refresh token",
      sql: `INSERT INTO refresh_tokens
            (id, session_id, family_id, parent_token_id, token_hash, created_at_utc, expires_at_utc)
            VALUES (?, ?, ?, NULL, ?, ?, ?)`,
      args: [refreshId, sessionId, familyId, "diagnostic-token-hash", timestamp, expires],
    },
    {
      stage: "create synchronized user record",
      sql: `INSERT INTO user_sync_records
            (id, tenant_id, store_id, payload_json, created_at_utc, updated_at_utc, deleted_at_utc,
             version, created_by_user_id, updated_by_user_id, last_modified_device_id)
            VALUES (?, ?, NULL, ?, ?, ?, NULL, 1, ?, ?, ?)`,
      args: [userId, tenantId, payload, timestamp, timestamp, userId, userId, deviceId],
    },
    {
      stage: "create synchronization change",
      sql: `INSERT INTO sync_changes
            (tenant_id, store_id, entity_type, record_id, version, updated_at_utc, deleted_at_utc,
             last_modified_device_id, payload_json)
            VALUES (?, NULL, 'users', ?, 1, ?, NULL, ?, ?)`,
      args: [tenantId, userId, timestamp, deviceId, payload],
    },
    {
      stage: "create audit event",
      sql: `INSERT INTO audit_logs
            (id, tenant_id, store_id, user_id, device_id, timestamp_utc, action, affected_type,
             affected_id, request_id, metadata_json)
            VALUES (?, ?, ?, ?, ?, ?, 'diagnostic.organization_preflight', 'organization', ?, ?, '{}')`,
      args: [crypto.randomUUID(), tenantId, storeId, userId, deviceId, timestamp, tenantId, requestId],
    },
  ];

  // The final duplicate insert is an intentional rollback sentinel. libSQL's
  // non-interactive batch runs all statements in one atomic transaction; a
  // failure at this final index proves every preceding provisioning statement
  // was accepted while also guaranteeing that no diagnostic data is committed.
  const rollbackSentinelIndex = steps.length;
  const rollbackSentinel = {
    stage: "force diagnostic rollback",
    sql: `INSERT INTO organizations
          (id, name, is_active, created_at_utc, updated_at_utc, schema_version)
          VALUES (?, ?, 1, ?, ?, ?)`,
    args: [tenantId, "PosApp diagnostic rollback", timestamp, timestamp, Number(env.SCHEMA_VERSION ?? "4")],
  };

  try {
    try {
      await client.batch(
        [...steps, rollbackSentinel].map(({ sql, args }) => ({ sql, args })),
        "write",
      );
      // This should be unreachable because the sentinel repeats the primary key.
      await cleanupDiagnosticRows(client, tenantId, userId, deviceId, sessionId);
      return {
        id: "organization-creation-preflight",
        name: "Organization creation preflight",
        status: "fail",
        message: "The diagnostic rollback sentinel did not abort the atomic batch.",
        code: "DIAGNOSTIC_ROLLBACK_SENTINEL_FAILED",
        stage: rollbackSentinel.stage,
      };
    } catch (error) {
      const statementIndex = batchStatementIndex(error);
      if (statementIndex !== rollbackSentinelIndex || !isConstraintFailure(error)) {
        const stage = statementIndex == null
          ? "execute atomic organization batch"
          : steps[statementIndex]?.stage ?? rollbackSentinel.stage;
        return {
          id: "organization-creation-preflight",
          name: "Organization creation preflight",
          status: "fail",
          message: "The safe atomic organization test failed. Use the stage and request ID when checking Worker logs.",
          code: "ORGANIZATION_PREFLIGHT_FAILED",
          stage,
          providerCode: safeProviderErrorCode(error),
        };
      }
    }

    const rollback = await client.execute({
      sql: "SELECT COUNT(*) AS count FROM organizations WHERE id = ?",
      args: [tenantId],
    });
    if (integer(rollback.rows[0]?.count) !== 0) {
      await cleanupDiagnosticRows(client, tenantId, userId, deviceId, sessionId);
      return {
        id: "organization-creation-preflight",
        name: "Organization creation preflight",
        status: "fail",
        message: "The diagnostic batch failed as expected, but its rows were not rolled back.",
        code: "DIAGNOSTIC_ROLLBACK_FAILED",
        stage: "verify atomic rollback",
      };
    }

    return {
      id: "organization-creation-preflight",
      name: "Organization creation preflight",
      status: "pass",
      message: "The Worker accepted every organization, account, device, session, sync, and audit statement in one atomic Turso batch and rolled the batch back successfully.",
    };
  } catch (error) {
    return {
      id: "organization-creation-preflight",
      name: "Organization creation preflight",
      status: "fail",
      message: "The diagnostic could not verify the atomic organization batch.",
      code: "ORGANIZATION_PREFLIGHT_FAILED",
      stage: "verify atomic organization batch",
      providerCode: safeProviderErrorCode(error),
    };
  } finally {
    client.close();
  }
}

function batchStatementIndex(error: unknown): number | null {
  if (!error || typeof error !== "object") return null;
  const value = (error as Record<string, unknown>).statementIndex;
  return Number.isInteger(value) && Number(value) >= 0 ? Number(value) : null;
}

function isConstraintFailure(error: unknown): boolean {
  if (!error || typeof error !== "object") return false;
  const value = error as Record<string, unknown>;
  const code = String(value.extendedCode ?? value.code ?? "");
  const message = error instanceof Error ? error.message : "";
  return /CONSTRAINT|UNIQUE|PRIMARYKEY/i.test(code) || /constraint|unique|primary key/i.test(message);
}

async function cleanupDiagnosticRows(
  client: Client,
  tenantId: string,
  userId: string,
  deviceId: string,
  sessionId: string,
): Promise<void> {
  // This is only a last-resort guard for an impossible sentinel success or a
  // provider rollback defect. Normal diagnostics never commit these rows.
  await client.batch([
    { sql: "DELETE FROM audit_logs WHERE tenant_id = ?", args: [tenantId] },
    { sql: "DELETE FROM sync_changes WHERE tenant_id = ?", args: [tenantId] },
    { sql: "DELETE FROM user_sync_records WHERE tenant_id = ?", args: [tenantId] },
    { sql: "DELETE FROM refresh_tokens WHERE session_id = ?", args: [sessionId] },
    { sql: "DELETE FROM login_sessions WHERE tenant_id = ?", args: [tenantId] },
    { sql: "DELETE FROM registered_devices WHERE id = ?", args: [deviceId] },
    { sql: "DELETE FROM user_store_assignments WHERE tenant_id = ?", args: [tenantId] },
    { sql: "DELETE FROM users WHERE id = ?", args: [userId] },
    { sql: "DELETE FROM stores WHERE tenant_id = ?", args: [tenantId] },
    { sql: "DELETE FROM organizations WHERE id = ?", args: [tenantId] },
  ], "write");
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, (character) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;",
  })[character]!);
}
