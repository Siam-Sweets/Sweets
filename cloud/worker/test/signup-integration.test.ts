import { createClient, type Client } from "@libsql/client";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { readFile, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const dbPath = `/tmp/posapp-signup-integration-${process.pid}.db`;

vi.mock("../src/db", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/db")>();
  return {
    ...actual,
    database: vi.fn(() => createClient({ url: `file:${dbPath}` }) as unknown as Client),
    inspectDatabaseReadiness: vi.fn(async (_env: unknown, expectedSchemaVersion: number) => {
      const client = createClient({ url: `file:${dbPath}` });
      try {
        await client.execute("SELECT 1 AS ok");
        const result = await client.execute("SELECT MAX(version) AS version FROM schema_migrations");
        const schemaVersion = Number(result.rows[0]?.version ?? 0);
        return {
          configured: true,
          reachable: true,
          schemaVersion,
          schemaReady: schemaVersion >= expectedSchemaVersion,
        };
      } finally {
        client.close();
      }
    }),
  };
});

import { signup } from "../src/auth";
import { runCloudDiagnostics } from "../src/diagnostics";
import worker from "../src/index";
import type { Env } from "../src/types";

const env: Env = {
  TURSO_DATABASE_URL: `file:${dbPath}`,
  TURSO_AUTH_TOKEN: "local-test-token",
  JWT_SIGNING_SECRET: "jwt-test-secret-that-is-at-least-32-characters-long",
  REFRESH_TOKEN_SECRET: "refresh-test-secret-at-least-32-characters-long",
  PASSWORD_PEPPER_SECRET: "password-pepper-test-secret-at-least-32-characters",
  ACCESS_TOKEN_TTL_SECONDS: "600",
  REFRESH_TOKEN_TTL_DAYS: "30",
  SCHEMA_VERSION: "4",
  MINIMUM_CLIENT_SCHEMA_VERSION: "4",
  API_VERSION: "1",
  DEPLOYMENT_VERSION: "2.1.3-test",
};

beforeAll(async () => {
  await rm(dbPath, { force: true });
  const client = createClient({ url: `file:${dbPath}` });
  try {
    for (const file of [
      "0001_initial.sql",
      "0002_indexes.sql",
      "0003_migration_lock.sql",
      "0004_financial_composition_staging.sql",
    ]) {
      const migration = fileURLToPath(new URL(`../../migrations/${file}`, import.meta.url));
      await client.executeMultiple(await readFile(migration, "utf8"));
    }
  } finally {
    client.close();
  }
});

afterAll(async () => {
  await rm(dbPath, { force: true });
});

describe("organization provisioning integration", () => {
  it("renders the public status page with an account-creation result", async () => {
    const response = await worker.fetch(new Request("https://example.test/status"), env);
    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toContain("text/html");
    expect(response.headers.get("x-posapp-status-page")).toBe("1");
    expect(response.headers.get("x-posapp-deployment-version")).toBe("2.1.3-test");
    const html = await response.text();
    expect(html).toContain("PosApp Cloud API");
    expect(html).toContain("Run diagnostics again");
    expect(html).toContain("/api/v1/diagnostics");
    expect(html).toContain('name="posapp-status-page" content="true"');
    expect(html).toContain('name="posapp-deployment-version" content="2.1.3-test"');
    expect(html).toContain("Checking deployment");
  });

  it("renders the tenant-scoped account portal at the Worker root", async () => {
    const response = await worker.fetch(new Request("https://example.test/"), env);
    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toContain("text/html");
    expect(response.headers.get("x-posapp-portal")).toBe("1");
    expect(response.headers.get("x-posapp-deployment-version")).toBe("2.1.3-test");
    expect(response.headers.get("content-security-policy")).toContain("script-src 'nonce-");
    expect(response.headers.get("content-security-policy")).not.toContain("script-src 'unsafe-inline'");
    const html = await response.text();
    expect(html).toContain("PosApp Cloud Account");
    expect(html).toContain("Username or email");
    expect(html).toContain("Total users");
    expect(html).toContain("Create another organization");
    expect(html).toContain("/api/v1/users");
    expect(html).toContain("method:'DELETE'");
    expect(html).toContain("const candidateId=crypto.randomUUID()");
    expect(html).toContain("error.code!=='DEVICE_TENANT_MISMATCH'");
    expect(html).toContain("posappPortalDeviceProfiles");
    expect(html).toContain("sessionStorage.posappPortalDeviceId");
    expect(html).toContain('name="posapp-portal-version" content="2.1.3-test"');
  });

  it("publishes consistent portal, status, API, and schema metadata", async () => {
    const response = await worker.fetch(new Request("https://example.test/api/v1/meta"), env);
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      deploymentVersion: "2.1.3-test",
      apiVersion: 1,
      schemaVersion: 4,
      accountPortal: "/",
      statusPage: "/status",
    });
  });

  it("returns failed readiness checks as readable JSON instead of an opaque HTTP 503", async () => {
    const response = await worker.fetch(
      new Request("https://example.test/api/v1/diagnostics"),
      { ...env, JWT_SIGNING_SECRET: "too-short" },
    );

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toContain("application/json");
    const report = await response.json() as {
      ready: boolean;
      accountCreationReady: boolean;
      checks: Array<{ status: string; code?: string }>;
    };
    expect(report.ready).toBe(false);
    expect(report.accountCreationReady).toBe(false);
    expect(report.checks).toContainEqual(expect.objectContaining({
      status: "fail",
      code: "AUTHENTICATION_CONFIGURATION_ERROR",
    }));
  });

  it("reports a missing dedicated password pepper before running account creation", async () => {
    const response = await worker.fetch(
      new Request("https://example.test/api/v1/diagnostics"),
      { ...env, PASSWORD_PEPPER_SECRET: "too-short" },
    );

    expect(response.status).toBe(200);
    const report = await response.json() as {
      ready: boolean;
      accountCreationReady: boolean;
      checks: Array<{ id: string; status: string; code?: string }>;
    };
    expect(report.ready).toBe(false);
    expect(report.accountCreationReady).toBe(false);
    expect(report.checks).toContainEqual(expect.objectContaining({
      id: "authentication-crypto",
      status: "fail",
      code: "AUTHENTICATION_CONFIGURATION_ERROR",
    }));
  });

  it("passes the public write-and-rollback diagnostic without leaving rows", async () => {
    const report = await runCloudDiagnostics(env, "request-diagnostic-test");
    expect(report.ready).toBe(true);
    expect(report.accountCreationReady).toBe(true);
    expect(report.checks.every((check) => check.status === "pass")).toBe(true);

    const client = createClient({ url: `file:${dbPath}` });
    try {
      const result = await client.execute(
        "SELECT COUNT(*) AS count FROM organizations WHERE name = 'PosApp diagnostics'",
      );
      expect(Number(result.rows[0]?.count ?? 0)).toBe(0);
    } finally {
      client.close();
    }
  });

  it("creates an organization through the same atomic batch path used in production", async () => {
    const response = await signup(
      new Request("https://example.test/api/v1/auth/signup", { method: "POST" }),
      env,
      "request-signup-integration",
      {
        organizationName: "Siam",
        storeName: "Siam",
        fullName: "Siam Chowdhury",
        username: "siamtest",
        email: "siamtest@example.test",
        password: "password1234",
        clientSchemaVersion: 4,
        device: {
          id: "10000000-0000-4000-8000-000000000001",
          name: "DESKTOP-TEST",
          operatingSystem: "Windows",
          machineName: "DESKTOP-TEST",
        },
      },
    );

    expect(response.status).toBe(201);
    const body = await response.json() as {
      organizationId: string;
      user: { username: string };
      store: { code: string };
    };
    expect(body.organizationId).toMatch(/^[0-9a-f-]{36}$/);
    expect(body.user.username).toBe("siamtest");
    expect(body.store.code).toBe("MAIN");

    const client = createClient({ url: `file:${dbPath}` });
    try {
      const result = await client.execute({
        sql: `SELECT
                (SELECT COUNT(*) FROM organizations WHERE id = ?) AS organizations,
                (SELECT COUNT(*) FROM stores WHERE tenant_id = ?) AS stores,
                (SELECT COUNT(*) FROM users WHERE tenant_id = ?) AS users,
                (SELECT COUNT(*) FROM registered_devices WHERE tenant_id = ?) AS devices,
                (SELECT COUNT(*) FROM login_sessions WHERE tenant_id = ?) AS sessions,
                (SELECT COUNT(*) FROM refresh_tokens rt
                  JOIN login_sessions ls ON ls.id = rt.session_id WHERE ls.tenant_id = ?) AS refreshTokens,
                (SELECT COUNT(*) FROM user_sync_records WHERE tenant_id = ?) AS syncUsers,
                (SELECT COUNT(*) FROM sync_changes WHERE tenant_id = ?) AS syncChanges,
                (SELECT COUNT(*) FROM audit_logs WHERE tenant_id = ?) AS auditLogs`,
        args: Array(9).fill(body.organizationId),
      });
      const row = result.rows[0]!;
      expect(Number(row.organizations)).toBe(1);
      expect(Number(row.stores)).toBe(1);
      expect(Number(row.users)).toBe(1);
      expect(Number(row.devices)).toBe(1);
      expect(Number(row.sessions)).toBe(1);
      expect(Number(row.refreshTokens)).toBe(1);
      expect(Number(row.syncUsers)).toBe(1);
      expect(Number(row.syncChanges)).toBe(1);
      expect(Number(row.auditLogs)).toBe(3);
    } finally {
      client.close();
    }
  });

  it("creates another organization only with a separate browser device identity", async () => {
    const signupBody = (username: string, email: string, deviceId: string) => JSON.stringify({
      organizationName: `${username} organization`,
      storeName: `${username} store`,
      fullName: `${username} owner`,
      username,
      email,
      password: "anotherPassword123",
      clientSchemaVersion: 4,
      device: {
        id: deviceId,
        name: "Multi-organization browser",
        operatingSystem: "Web",
        machineName: "Browser",
      },
    });
    const request = (body: string) => new Request("https://example.test/api/v1/auth/signup", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body,
    });

    const firstDevice = "30000000-0000-4000-8000-000000000001";
    const first = await worker.fetch(request(signupBody(
      "multiownerone", "multiownerone@example.test", firstDevice,
    )), env);
    expect(first.status).toBe(201);

    const unsafeReuse = await worker.fetch(request(signupBody(
      "multiownertwo", "multiownertwo@example.test", firstDevice,
    )), env);
    expect(unsafeReuse.status).toBe(409);
    expect(await unsafeReuse.json()).toMatchObject({
      error: { code: "DEVICE_TENANT_MISMATCH" },
    });

    const isolated = await worker.fetch(request(signupBody(
      "multiownertwo", "multiownertwo@example.test",
      "30000000-0000-4000-8000-000000000002",
    )), env);
    expect(isolated.status).toBe(201);
    const isolatedBody = await isolated.json() as { organizationId: string; deviceId: string };
    expect(isolatedBody.deviceId).toBe("30000000-0000-4000-8000-000000000002");
  });

  it("returns exact tenant user counts and safely deletes a non-current user", async () => {
    const signupResponse = await worker.fetch(new Request("https://example.test/api/v1/auth/signup", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        organizationName: "Portal test organization",
        storeName: "Portal test store",
        fullName: "Portal Owner",
        username: "portalowner",
        email: "portalowner@example.test",
        password: "portalPassword123",
        clientSchemaVersion: 4,
        device: {
          id: "20000000-0000-4000-8000-000000000001",
          name: "Portal owner browser",
          operatingSystem: "Web",
          machineName: "Browser",
        },
      }),
    }), env);
    expect(signupResponse.status).toBe(201);
    const signupBody = await signupResponse.json() as {
      tokens: { accessToken: string };
      user: { id: string };
      store: { id: string };
    };
    const adminHeaders = {
      "content-type": "application/json",
      authorization: `Bearer ${signupBody.tokens.accessToken}`,
    };

    const createResponse = await worker.fetch(new Request("https://example.test/api/v1/users", {
      method: "POST",
      headers: adminHeaders,
      body: JSON.stringify({
        username: "portalcashier",
        email: "portalcashier@example.test",
        fullName: "Portal Cashier",
        password: "cashierPassword123",
        role: "cashier",
        storeId: signupBody.store.id,
        permissions: [],
      }),
    }), env);
    expect(createResponse.status).toBe(201);
    const created = await createResponse.json() as { id: string };

    const loginResponse = await worker.fetch(new Request("https://example.test/api/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        usernameOrEmail: "portalcashier",
        password: "cashierPassword123",
        clientSchemaVersion: 4,
        device: {
          id: "20000000-0000-4000-8000-000000000002",
          name: "Portal cashier browser",
          operatingSystem: "Web",
          machineName: "Browser",
        },
      }),
    }), env);
    expect(loginResponse.status).toBe(200);
    const loginBody = await loginResponse.json() as { tokens: { accessToken: string } };

    const beforeResponse = await worker.fetch(new Request("https://example.test/api/v1/users", {
      headers: adminHeaders,
    }), env);
    expect(beforeResponse.status).toBe(200);
    const before = await beforeResponse.json() as {
      users: Array<{ id: string; isActive: boolean }>;
      totalUsers: number;
      activeUsers: number;
    };
    expect(before.totalUsers).toBe(2);
    expect(before.activeUsers).toBe(2);
    expect(before.users).toHaveLength(2);

    const deleteResponse = await worker.fetch(new Request(
      `https://example.test/api/v1/users/${created.id}`,
      { method: "DELETE", headers: adminHeaders },
    ), env);
    expect(deleteResponse.status).toBe(200);

    const afterResponse = await worker.fetch(new Request("https://example.test/api/v1/users", {
      headers: adminHeaders,
    }), env);
    const after = await afterResponse.json() as {
      users: Array<{ id: string; isActive: boolean }>;
      totalUsers: number;
      activeUsers: number;
    };
    expect(after.totalUsers).toBe(2);
    expect(after.activeUsers).toBe(1);
    expect(after.users.find((user) => user.id === created.id)?.isActive).toBe(false);

    const revokedResponse = await worker.fetch(new Request(
      "https://example.test/api/v1/account/profile",
      { headers: { authorization: `Bearer ${loginBody.tokens.accessToken}` } },
    ), env);
    expect(revokedResponse.status).toBe(401);
    expect(await revokedResponse.json()).toMatchObject({ error: { code: "SESSION_REVOKED" } });

    const currentUserResponse = await worker.fetch(new Request(
      `https://example.test/api/v1/users/${signupBody.user.id}`,
      { method: "DELETE", headers: adminHeaders },
    ), env);
    expect(currentUserResponse.status).toBe(409);
    expect(await currentUserResponse.json()).toMatchObject({ error: { code: "CURRENT_USER_PROTECTED" } });

    const client = createClient({ url: `file:${dbPath}` });
    try {
      const result = await client.execute({
        sql: `SELECT u.is_active, u.deleted_at_utc, usr.deleted_at_utc AS sync_deleted_at_utc,
                     (SELECT COUNT(*) FROM audit_logs
                      WHERE tenant_id = u.tenant_id AND action = 'user.deleted'
                        AND affected_id = u.id) AS delete_audits
              FROM users u JOIN user_sync_records usr
                ON usr.id = u.id AND usr.tenant_id = u.tenant_id
              WHERE u.id = ?`,
        args: [created.id],
      });
      expect(Number(result.rows[0]?.is_active)).toBe(0);
      expect(result.rows[0]?.deleted_at_utc).not.toBeNull();
      expect(result.rows[0]?.sync_deleted_at_utc).not.toBeNull();
      expect(Number(result.rows[0]?.delete_audits)).toBe(1);
    } finally {
      client.close();
    }
  });
});
