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
    return jsonResponse(
      { error: { code: error.code, message: error.message, details: error.details }, requestId },
      error.status,
      requestId,
    );
  }
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
