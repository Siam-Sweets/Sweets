import { createClient, type Client } from "@libsql/client/web";
import { ApiError } from "./errors";
import type { Env } from "./types";

export function database(env: Env): Client {
  if (!env.TURSO_DATABASE_URL || !env.TURSO_AUTH_TOKEN) {
    throw new ApiError(500, "SERVER_CONFIGURATION_ERROR", "The database service is unavailable.");
  }
  return createClient({ url: env.TURSO_DATABASE_URL, authToken: env.TURSO_AUTH_TOKEN });
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
