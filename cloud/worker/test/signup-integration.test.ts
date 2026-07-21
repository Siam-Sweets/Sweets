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
  DEPLOYMENT_VERSION: "2.0.16-test",
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
  it("renders a public root status page with an account-creation result", async () => {
    const response = await worker.fetch(new Request("https://example.test/"), env);
    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toContain("text/html");
    expect(response.headers.get("x-posapp-status-page")).toBe("1");
    expect(response.headers.get("x-posapp-deployment-version")).toBe("2.0.16-test");
    const html = await response.text();
    expect(html).toContain("PosApp Cloud API");
    expect(html).toContain("Run diagnostics again");
    expect(html).toContain("/api/v1/diagnostics");
    expect(html).toContain('name="posapp-status-page" content="true"');
    expect(html).toContain('name="posapp-deployment-version" content="2.0.16-test"');
    expect(html).toContain("Checking deployment");
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
});
