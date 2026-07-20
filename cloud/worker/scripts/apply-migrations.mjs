import { createClient } from "@libsql/client";
import { readdir, readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const databaseUrl = process.env.TURSO_DATABASE_URL?.trim() ?? "";
const authToken = process.env.TURSO_AUTH_TOKEN?.trim() ?? "";

main().catch((error) => {
  const message = sanitize(error instanceof Error ? error.message : String(error));
  console.error(`Migration failed: ${message}`);
  process.exitCode = 1;
});

async function main() {
  validateConfiguration();
  const migrationsDirectory = fileURLToPath(new URL("../../migrations/", import.meta.url));
  const client = createClient({ url: databaseUrl, authToken: authToken || undefined });

  try {
    const migrations = (await readdir(migrationsDirectory))
      .filter((file) => /^\d{4}_.+\.sql$/i.test(file))
      .sort((left, right) => left.localeCompare(right))
      .map((file) => ({ file, version: Number(file.slice(0, 4)) }));

    if (migrations.length === 0) fail("No SQL migrations were found.");

    const applied = await readAppliedVersions(client);
    for (const migration of migrations) {
      if (applied.has(migration.version)) {
        console.log(`Migration ${migration.file} is already applied.`);
        continue;
      }

      let sql = await readFile(path.join(migrationsDirectory, migration.file), "utf8");
      // Migration 3 predates this automated runner. If an interrupted manual run
      // added the column but did not record the migration, omit only that already-
      // completed ALTER statement and safely apply the remaining idempotent SQL.
      if (migration.version === 3 && await columnExists(client, "registered_devices", "assigned_store_id")) {
        sql = sql.replace(
          /ALTER\s+TABLE\s+registered_devices\s+ADD\s+COLUMN\s+assigned_store_id\s+TEXT\s*;/i,
          "-- assigned_store_id already exists from an interrupted migration",
        );
      }

      console.log(`Applying migration ${migration.file}...`);
      await client.executeMultiple(sql);
      const recorded = await client.execute({
        sql: "SELECT version FROM schema_migrations WHERE version = ? LIMIT 1",
        args: [migration.version],
      });
      if (recorded.rows.length !== 1) {
        fail(`Migration ${migration.file} completed without recording schema version ${migration.version}.`);
      }
    }

    const expectedVersion = migrations[migrations.length - 1].version;
    const result = await client.execute("SELECT MAX(version) AS version FROM schema_migrations");
    const actualVersion = Number(result.rows[0]?.version ?? 0);
    if (actualVersion !== expectedVersion) {
      fail(`Database schema verification failed. Expected version ${expectedVersion}, received ${actualVersion}.`);
    }

    for (const table of ["organizations", "stores", "users", "registered_devices", "login_sessions", "sync_changes"]) {
      if (!await tableExists(client, table)) {
        fail(`Database schema verification failed because table ${table} is missing.`);
      }
    }
    if (!await columnExists(client, "registered_devices", "assigned_store_id")) {
      fail("Database schema verification failed because registered_devices.assigned_store_id is missing.");
    }

    console.log(`Turso schema is ready at version ${actualVersion}.`);
  } finally {
    client.close();
  }
}

function validateConfiguration() {
  if (!databaseUrl) fail("TURSO_DATABASE_URL is not configured.");
  if (!authToken && !databaseUrl.startsWith("file:")) fail("TURSO_AUTH_TOKEN is not configured.");
  if (!databaseUrl.startsWith("libsql://") && !databaseUrl.startsWith("https://") && !databaseUrl.startsWith("file:")) {
    fail("TURSO_DATABASE_URL must start with libsql://, https://, or file: for local validation.");
  }
}

async function readAppliedVersions(client) {
  try {
    const result = await client.execute("SELECT version FROM schema_migrations ORDER BY version");
    return new Set(result.rows.map((row) => Number(row.version)));
  } catch (error) {
    if (isMissingSchemaTable(error)) return new Set();
    throw error;
  }
}

async function tableExists(client, tableName) {
  const result = await client.execute({
    sql: "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ? LIMIT 1",
    args: [tableName],
  });
  return result.rows.length === 1;
}

async function columnExists(client, tableName, columnName) {
  if (!await tableExists(client, tableName)) return false;
  // Table names are selected only from constants in this script. They are not
  // derived from user or repository input.
  const result = await client.execute(`PRAGMA table_info(\"${tableName}\")`);
  return result.rows.some((row) => String(row.name) === columnName);
}

function isMissingSchemaTable(error) {
  const message = error instanceof Error ? error.message : String(error);
  return /no such table\s*:\s*schema_migrations/i.test(message);
}

function sanitize(message) {
  let value = message;
  if (databaseUrl) value = value.split(databaseUrl).join("[database-url-redacted]");
  if (authToken) value = value.split(authToken).join("[token-redacted]");
  return value.replace(/(authToken|authorization|bearer)\s*[:=]\s*\S+/gi, "$1=[redacted]");
}

function fail(message) {
  throw new Error(message);
}
