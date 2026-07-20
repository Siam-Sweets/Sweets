import {
  authenticate,
  authorizeDevice,
  changePassword,
  createUser,
  listSessions,
  listUsers,
  login,
  logout,
  profile,
  recordClientAudit,
  refresh,
  registerDevice,
  revokeSession,
  revokeDevice,
  signup,
  updateUser,
} from "./auth";
import { ApiError, errorResponse, jsonResponse } from "./errors";
import { createStore, listStores, verifySelectedStore } from "./stores";
import { pull, push, syncStatus } from "./sync";
import { finishInitialMigration, startInitialMigration } from "./migrations";
import type { Env } from "./types";
import { clampNumber } from "./crypto";
import { inspectDatabaseReadiness } from "./db";
import { diagnosticsJson, diagnosticsPage } from "./diagnostics";

const requestWindows = new Map<string, { count: number; resetAt: number }>();
const diagnosticWindows = new Map<string, { count: number; resetAt: number }>();

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const requestId = normalizeRequestId(request.headers.get("x-request-id"));
    try {
      const url = new URL(request.url);
      enforceHttps(url, request);
      const clientAddress = request.headers.get("cf-connecting-ip") ?? "unknown";
      enforceGeneralRateLimit(clientAddress);

      if (request.method === "OPTIONS") return new Response(null, { status: 204 });
      if (request.method === "GET" && url.pathname === "/") {
        enforceDiagnosticRateLimit(clientAddress);
        return await diagnosticsPage(request, env, requestId);
      }
      if (request.method === "GET" && url.pathname === "/api/v1/diagnostics") {
        enforceDiagnosticRateLimit(clientAddress);
        return await diagnosticsJson(env, requestId);
      }
      if (request.method === "GET" && url.pathname === "/api/v1/meta") {
        const expectedSchemaVersion = Number(env.SCHEMA_VERSION ?? "4");
        const database = await inspectDatabaseReadiness(env, expectedSchemaVersion);
        const authenticationConfigured = Boolean(
          env.JWT_SIGNING_SECRET?.length >= 32 && env.REFRESH_TOKEN_SECRET?.length >= 32 &&
          env.PASSWORD_PEPPER_SECRET?.length >= 32,
        );
        return jsonResponse({
          service: "PosApp Cloud API",
          deploymentVersion: env.DEPLOYMENT_VERSION ?? "2.0.15",
          apiVersion: Number(env.API_VERSION ?? "1"),
          schemaVersion: expectedSchemaVersion,
          minimumClientSchemaVersion: Number(env.MINIMUM_CLIENT_SCHEMA_VERSION ?? "4"),
          statusPage: "/",
          diagnosticsEndpoint: "/api/v1/diagnostics",
          configuration: {
            ready: database.configured && database.reachable && database.schemaReady && authenticationConfigured,
            databaseConfigured: database.configured,
            databaseReachable: database.reachable,
            databaseSchemaVersion: database.schemaVersion,
            databaseSchemaReady: database.schemaReady,
            authenticationConfigured,
          },
          requestId,
        }, 200, requestId);
      }

      if (request.method === "POST" && url.pathname === "/api/v1/auth/signup")
        return await signup(request, env, requestId, await readJson(request, env));
      if (request.method === "POST" && url.pathname === "/api/v1/auth/login")
        return await login(request, env, requestId, await readJson(request, env), clientAddress);
      if (request.method === "POST" && url.pathname === "/api/v1/auth/refresh")
        return await refresh(env, requestId, await readJson(request, env));

      const context = await authenticate(request, env, requestId);
      if (request.method === "POST" && url.pathname === "/api/v1/auth/logout")
        return await logout(context, env, await readJson(request, env, true));
      if (request.method === "POST" && url.pathname === "/api/v1/auth/register-device")
        return await registerDevice(context, env, await readJson(request, env));
      if (request.method === "GET" && url.pathname === "/api/v1/auth/sessions")
        return await listSessions(context, env);
      const sessionMatch = url.pathname.match(/^\/api\/v1\/auth\/sessions\/([0-9a-f-]+)$/i);
      if (request.method === "DELETE" && sessionMatch)
        return await revokeSession(context, env, sessionMatch[1]!);
      const deviceMatch = url.pathname.match(/^\/api\/v1\/auth\/devices\/([0-9a-f-]+)$/i);
      if (request.method === "DELETE" && deviceMatch)
        return await revokeDevice(context, env, deviceMatch[1]!);
      if (request.method === "PATCH" && deviceMatch)
        return await authorizeDevice(context, env, deviceMatch[1]!);

      if (request.method === "GET" && url.pathname === "/api/v1/account/profile")
        return await profile(context, env);
      if (request.method === "POST" && url.pathname === "/api/v1/account/password")
        return await changePassword(context, env, await readJson(request, env));
      if (request.method === "POST" && url.pathname === "/api/v1/audit/events")
        return await recordClientAudit(context, env, await readJson(request, env));

      if (request.method === "GET" && url.pathname === "/api/v1/users")
        return await listUsers(context, env);
      if (request.method === "POST" && url.pathname === "/api/v1/users")
        return await createUser(context, env, await readJson(request, env));
      const userMatch = url.pathname.match(/^\/api\/v1\/users\/([0-9a-f-]+)$/i);
      if (request.method === "PATCH" && userMatch)
        return await updateUser(context, env, userMatch[1]!, await readJson(request, env));

      if (request.method === "GET" && url.pathname === "/api/v1/stores")
        return await listStores(context, env);
      if (request.method === "POST" && url.pathname === "/api/v1/stores")
        return await createStore(context, env, await readJson(request, env));
      const storeMatch = url.pathname.match(/^\/api\/v1\/stores\/([0-9a-f-]+)\/verify$/i);
      if (request.method === "GET" && storeMatch)
        return await verifySelectedStore(context, env, storeMatch[1]!);

      if (request.method === "POST" && url.pathname === "/api/v1/sync/push")
        return await push(context, env, await readJson(request, env));
      if (request.method === "GET" && url.pathname === "/api/v1/sync/pull")
        return await pull(context, env, url);
      if (request.method === "GET" && url.pathname === "/api/v1/sync/status")
        return await syncStatus(context, env, url);
      if (request.method === "POST" && url.pathname === "/api/v1/migrations/initial/start")
        return await startInitialMigration(context, env, await readJson(request, env));
      if (request.method === "POST" && url.pathname === "/api/v1/migrations/initial/finish")
        return await finishInitialMigration(context, env, await readJson(request, env));

      throw new ApiError(404, "ENDPOINT_NOT_FOUND", "The requested API endpoint was not found.");
    } catch (error) {
      return errorResponse(error, requestId);
    }
  },
};

async function readJson(request: Request, env: Env, allowEmpty = false): Promise<unknown> {
  const maximum = clampNumber(env.MAX_REQUEST_BYTES, 1_048_576, 64_000, 2_000_000);
  const contentLength = Number(request.headers.get("content-length") ?? "0");
  if (contentLength > maximum) throw new ApiError(413, "REQUEST_TOO_LARGE", "The request body is too large.");
  const contentType = request.headers.get("content-type") ?? "";
  if (!contentType.toLowerCase().includes("application/json") && !(allowEmpty && contentLength === 0))
    throw new ApiError(415, "UNSUPPORTED_MEDIA_TYPE", "Use application/json.");
  if (allowEmpty && !request.body) return {};

  let buffer: ArrayBuffer;
  const encoding = (request.headers.get("content-encoding") ?? "").toLowerCase();
  try {
    if (encoding === "gzip") {
      if (!request.body) throw new ApiError(400, "INVALID_JSON", "A JSON body is required.");
      buffer = await readLimitedBody(request.body.pipeThrough(new DecompressionStream("gzip")), maximum);
    } else if (encoding === "" || encoding === "identity") {
      if (!request.body) buffer = new ArrayBuffer(0);
      else buffer = await readLimitedBody(request.body, maximum);
    } else {
      throw new ApiError(415, "UNSUPPORTED_CONTENT_ENCODING", "The request encoding is not supported.");
    }
  } catch (error) {
    if (error instanceof ApiError) throw error;
    throw new ApiError(400, "INVALID_REQUEST_ENCODING", "The compressed request body is invalid.");
  }
  if (buffer.byteLength > maximum) throw new ApiError(413, "REQUEST_TOO_LARGE", "The request body is too large.");
  if (buffer.byteLength === 0 && allowEmpty) return {};
  try {
    return JSON.parse(new TextDecoder().decode(buffer));
  } catch {
    throw new ApiError(400, "INVALID_JSON", "The request body is not valid JSON.");
  }
}

async function readLimitedBody(stream: ReadableStream<Uint8Array>, maximum: number): Promise<ArrayBuffer> {
  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      length += next.value.byteLength;
      if (length > maximum) {
        await reader.cancel("request too large");
        throw new ApiError(413, "REQUEST_TOO_LARGE", "The request body is too large.");
      }
      chunks.push(next.value);
    }
  } finally {
    reader.releaseLock();
  }
  const combined = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    combined.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return combined.buffer;
}

function normalizeRequestId(value: string | null): string {
  if (value && /^[A-Za-z0-9._-]{8,64}$/.test(value)) return value;
  return crypto.randomUUID();
}

function enforceHttps(url: URL, request: Request): void {
  const forwarded = request.headers.get("x-forwarded-proto");
  const local = url.hostname === "localhost" || url.hostname === "127.0.0.1";
  if (!local && url.protocol !== "https:" && forwarded !== "https")
    throw new ApiError(400, "HTTPS_REQUIRED", "HTTPS is required.");
}


function enforceDiagnosticRateLimit(address: string): void {
  const now = Date.now();
  const entry = diagnosticWindows.get(address);
  if (entry && entry.resetAt > now && entry.count >= 12)
    throw new ApiError(429, "DIAGNOSTIC_RATE_LIMITED", "Too many diagnostic requests. Try again in one minute.");
  diagnosticWindows.set(address, !entry || entry.resetAt <= now
    ? { count: 1, resetAt: now + 60_000 }
    : { count: entry.count + 1, resetAt: entry.resetAt });
  if (diagnosticWindows.size > 2_000) {
    for (const [key, value] of diagnosticWindows) if (value.resetAt <= now) diagnosticWindows.delete(key);
  }
}

function enforceGeneralRateLimit(address: string): void {
  const now = Date.now();
  const entry = requestWindows.get(address);
  if (entry && entry.resetAt > now && entry.count >= 600)
    throw new ApiError(429, "RATE_LIMITED", "Too many requests. Try again shortly.");
  requestWindows.set(address, !entry || entry.resetAt <= now
    ? { count: 1, resetAt: now + 60_000 }
    : { count: entry.count + 1, resetAt: entry.resetAt });
  if (requestWindows.size > 10_000) {
    for (const [key, value] of requestWindows) if (value.resetAt <= now) requestWindows.delete(key);
    while (requestWindows.size > 10_000) {
      const oldest = requestWindows.keys().next().value as string | undefined;
      if (oldest == null) break;
      requestWindows.delete(oldest);
    }
  }
}
