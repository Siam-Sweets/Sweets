import { createClient, type Client } from "@libsql/client/web";
import { ApiError } from "./errors";
import type { Env } from "./types";

export interface DatabaseReadiness {
  configured: boolean;
  reachable: boolean;
  schemaVersion: number;
  schemaReady: boolean;
}

export function database(env: Env): Client {
  if (!env.TURSO_DATABASE_URL || !env.TURSO_AUTH_TOKEN) {
    throw new ApiError(500, "DATABASE_CONFIGURATION_ERROR", "The Turso database bindings are missing.");
  }
  return createClient({ url: env.TURSO_DATABASE_URL, authToken: env.TURSO_AUTH_TOKEN });
}

export async function inspectDatabaseReadiness(env: Env, expectedSchemaVersion: number): Promise<DatabaseReadiness> {
  const configured = Boolean(env.TURSO_DATABASE_URL && env.TURSO_AUTH_TOKEN);
  if (!configured) return { configured: false, reachable: false, schemaVersion: 0, schemaReady: false };

  const client = database(env);
  try {
    await client.execute("SELECT 1 AS ok");
    try {
      const result = await client.execute("SELECT MAX(version) AS version FROM schema_migrations");
      const schemaVersion = integer(result.rows[0]?.version);
      return {
        configured: true,
        reachable: true,
        schemaVersion,
        schemaReady: schemaVersion >= expectedSchemaVersion,
      };
    } catch (error) {
      if (isDatabaseSchemaError(error)) {
        return { configured: true, reachable: true, schemaVersion: 0, schemaReady: false };
      }
      throw error;
    }
  } catch {
    return { configured: true, reachable: false, schemaVersion: 0, schemaReady: false };
  } finally {
    client.close();
  }
}

export async function requireDatabaseSchema(client: Client, expectedSchemaVersion: number): Promise<void> {
  try {
    const result = await client.execute("SELECT MAX(version) AS version FROM schema_migrations");
    const currentSchemaVersion = integer(result.rows[0]?.version);
    if (currentSchemaVersion < expectedSchemaVersion) {
      throw new ApiError(
        503,
        "DATABASE_SCHEMA_NOT_READY",
        `The online database schema must be migrated to version ${expectedSchemaVersion}. Redeploy the Worker to apply migrations.`,
        { expectedSchemaVersion, currentSchemaVersion },
      );
    }
  } catch (error) {
    if (error instanceof ApiError) throw error;
    if (isDatabaseSchemaError(error)) {
      throw new ApiError(
        503,
        "DATABASE_SCHEMA_NOT_READY",
        `The online database schema is not initialized. Redeploy the Worker to apply migration version ${expectedSchemaVersion}.`,
        { expectedSchemaVersion, currentSchemaVersion: 0 },
      );
    }
    throw error;
  }
}

export function isDatabaseSchemaError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? "");
  return /no such table|no such column|has no column named|duplicate column name|database schema/i.test(message);
}

export function text(value: unknown): string {
  return value == null ? "" : String(value);
}

export function nullableText(value: unknown): string | null {
  return value == null ? null : String(value);
}

export function integer(value: unknown): number {
  return typeof value === "bigint" ? Number(value) : Number(value ?? 0);
}

export function booleanValue(value: unknown): boolean {
  return value === true || value === 1 || value === 1n || value === "1";
}

export function nowIso(): string {
  return new Date().toISOString();
}
