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
  ACCESS_TOKEN_TTL_SECONDS: "600",
  REFRESH_TOKEN_TTL_DAYS: "30",
  SCHEMA_VERSION: "4",
  MINIMUM_CLIENT_SCHEMA_VERSION: "4",
  API_VERSION: "1",
  DEPLOYMENT_VERSION: "2.0.11-test",
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
    const html = await response.text();
    expect(html).toContain("PosApp Cloud API");
    expect(html).toContain("Run diagnostics again");
    expect(html).toContain("/api/v1/diagnostics");
    expect(html).toContain("Checking deployment");
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

  it("creates an organization through the same transaction path used in production", async () => {
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
  });
});
