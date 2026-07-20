export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly details?: unknown,
  ) {
    super(message);
  }
}

export function errorResponse(error: unknown, requestId: string): Response {
  if (error instanceof ApiError) {
    if (error.status >= 500) logServerFailure(error, requestId, error.code);
    return jsonResponse(
      { error: { code: error.code, message: error.message, details: error.details }, requestId },
      error.status,
      requestId,
    );
  }

  if (isDatabaseSchemaFailure(error)) {
    logServerFailure(error, requestId, "DATABASE_SCHEMA_NOT_READY");
    return jsonResponse(
      {
        error: {
          code: "DATABASE_SCHEMA_NOT_READY",
          message: "The online database schema is not initialized. Redeploy the Worker to apply migrations.",
        },
        requestId,
      },
      503,
      requestId,
    );
  }

  logServerFailure(error, requestId, "INTERNAL_ERROR");
  // Do not return SQL, stack, token, or provider details.
  return jsonResponse(
    { error: { code: "INTERNAL_ERROR", message: "The request could not be completed." }, requestId },
    500,
    requestId,
  );
}

export function jsonResponse(body: unknown, status: number, requestId: string): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
      "x-request-id": requestId,
      "x-content-type-options": "nosniff",
      "referrer-policy": "no-referrer",
    },
  });
}

function isDatabaseSchemaFailure(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? "");
  return /no such table|no such column|has no column named|duplicate column name|database schema/i.test(message);
}

function logServerFailure(error: unknown, requestId: string, category: string): void {
  const providerCode = providerErrorCode(error);
  const name = error instanceof Error ? error.name : typeof error;
  // Keep logs searchable by the request ID without logging SQL text, request
  // payloads, credentials, database URLs, or provider response bodies.
  console.error(JSON.stringify({
    event: "posapp_worker_request_failed",
    requestId,
    category,
    errorName: name,
    providerCode,
  }));
}

function providerErrorCode(error: unknown): string | null {
  if (!error || typeof error !== "object") return null;
  const value = error as Record<string, unknown>;
  const code = value.code ?? value.name;
  if (typeof code !== "string") return null;
  return /^[A-Za-z0-9._-]{1,80}$/.test(code) ? code : null;
}
