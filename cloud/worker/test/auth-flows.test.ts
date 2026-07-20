import type { Client } from "@libsql/client/web";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AuthContext, Env } from "../src/types";

vi.mock("../src/db", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/db")>();
  return { ...actual, database: vi.fn() };
});

import { logout, refresh } from "../src/auth";
import { sha256 } from "../src/crypto";
import { database } from "../src/db";

const env: Env = {
  TURSO_DATABASE_URL: "libsql://example.invalid",
  TURSO_AUTH_TOKEN: "test",
  JWT_SIGNING_SECRET: "jwt-test-secret-that-is-at-least-32-characters-long",
  REFRESH_TOKEN_SECRET: "refresh-test-secret-at-least-32-characters-long",
  ACCESS_TOKEN_TTL_SECONDS: "600",
  REFRESH_TOKEN_TTL_DAYS: "30",
};

function refreshRow(refreshTokenHash: string, overrides: Record<string, unknown> = {}) {
  return {
    id: "10000000-0000-4000-8000-000000000001",
    session_id: "10000000-0000-4000-8000-000000000002",
    family_id: "10000000-0000-4000-8000-000000000003",
    token_hash: refreshTokenHash,
    expires_at_utc: "2099-01-01T00:00:00.000Z",
    used_at_utc: null,
    revoked_at_utc: null,
    user_id: "10000000-0000-4000-8000-000000000004",
    tenant_id: "10000000-0000-4000-8000-000000000005",
    device_id: "10000000-0000-4000-8000-000000000006",
    session_revoked: null,
    session_expires: "2099-01-01T00:00:00.000Z",
    username: "owner",
    email: "owner@example.test",
    full_name: "Owner",
    role: "admin",
    permissions_json: "[]",
    password_version: 1,
    is_active: 1,
    device_status: "active",
    organization_active: 1,
    ...overrides,
  };
}

function fakeRefreshClient(row: Record<string, unknown>, consumedRows = 1) {
  const transaction = {
    execute: vi.fn().mockResolvedValue({ rows: [], rowsAffected: consumedRows }),
    batch: vi.fn().mockResolvedValue([]),
    commit: vi.fn().mockResolvedValue(undefined),
    rollback: vi.fn().mockResolvedValue(undefined),
    close: vi.fn(),
  };
  const client = {
    execute: vi.fn().mockResolvedValue({ rows: [row], rowsAffected: 0 }),
    batch: vi.fn().mockResolvedValue([]),
    transaction: vi.fn().mockResolvedValue(transaction),
    close: vi.fn(),
  };
  return { client, transaction };
}

describe("refresh-token rotation and revocation", () => {
  beforeEach(() => vi.mocked(database).mockReset());

  it("consumes one refresh token and issues its rotated child transactionally", async () => {
    const tokenId = "10000000-0000-4000-8000-000000000001";
    const sessionId = "10000000-0000-4000-8000-000000000002";
    const deviceId = "10000000-0000-4000-8000-000000000006";
    const refreshToken = `${tokenId}.abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV`;
    const tokenHash = await sha256(`${refreshToken}.${env.REFRESH_TOKEN_SECRET}`);
    const { client, transaction } = fakeRefreshClient(refreshRow(tokenHash));
    vi.mocked(database).mockReturnValue(client as unknown as Client);

    const response = await refresh(env, "request-refresh", { refreshToken, sessionId, deviceId });
    const body = await response.json() as { tokens: { refreshToken: string; accessToken: string } };

    expect(response.status).toBe(200);
    expect(body.tokens.refreshToken).not.toBe(refreshToken);
    expect(body.tokens.accessToken.split(".")).toHaveLength(3);
    expect(transaction.execute).toHaveBeenCalledOnce();
    expect(String(transaction.execute.mock.calls[0]?.[0]?.sql)).toContain("used_at_utc");
    expect(JSON.stringify(transaction.batch.mock.calls[0]?.[0])).toContain("INSERT INTO refresh_tokens");
    expect(transaction.commit).toHaveBeenCalledOnce();
    expect(transaction.close).toHaveBeenCalledOnce();
    expect(client.close).toHaveBeenCalledOnce();
  });

  it("revokes the token family and session when an old refresh token is reused", async () => {
    const tokenId = "10000000-0000-4000-8000-000000000001";
    const sessionId = "10000000-0000-4000-8000-000000000002";
    const deviceId = "10000000-0000-4000-8000-000000000006";
    const refreshToken = `${tokenId}.abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV`;
    const tokenHash = await sha256(`${refreshToken}.${env.REFRESH_TOKEN_SECRET}`);
    const { client } = fakeRefreshClient(refreshRow(tokenHash, {
      used_at_utc: "2026-07-19T00:00:00.000Z",
    }));
    vi.mocked(database).mockReturnValue(client as unknown as Client);

    await expect(refresh(env, "request-reuse", { refreshToken, sessionId, deviceId }))
      .rejects.toMatchObject({ code: "REFRESH_TOKEN_REUSE" });

    const revocations = JSON.stringify(client.batch.mock.calls[0]?.[0]);
    expect(revocations).toContain("WHERE family_id = ?");
    expect(revocations).toContain("revoke_reason = 'refresh_reuse'");
    expect(client.close).toHaveBeenCalledOnce();
  });

  it("does not renew a session for a disabled organization", async () => {
    const tokenId = "10000000-0000-4000-8000-000000000001";
    const sessionId = "10000000-0000-4000-8000-000000000002";
    const deviceId = "10000000-0000-4000-8000-000000000006";
    const refreshToken = `${tokenId}.abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV`;
    const tokenHash = await sha256(`${refreshToken}.${env.REFRESH_TOKEN_SECRET}`);
    const { client, transaction } = fakeRefreshClient(refreshRow(tokenHash, { organization_active: 0 }));
    vi.mocked(database).mockReturnValue(client as unknown as Client);

    await expect(refresh(env, "request-disabled-organization", { refreshToken, sessionId, deviceId }))
      .rejects.toMatchObject({ code: "ORGANIZATION_DISABLED" });
    expect(transaction.execute).not.toHaveBeenCalled();
    expect(client.close).toHaveBeenCalledOnce();
  });

  it("logout revokes both the selected server session and its refresh tokens", async () => {
    const client = {
      execute: vi.fn().mockResolvedValue({ rows: [], rowsAffected: 1 }),
      batch: vi.fn().mockResolvedValue([]),
      close: vi.fn(),
    };
    vi.mocked(database).mockReturnValue(client as unknown as Client);
    const context: AuthContext = {
      claims: {
        sub: "10000000-0000-4000-8000-000000000004",
        tid: "10000000-0000-4000-8000-000000000005",
        sid: "10000000-0000-4000-8000-000000000002",
        did: "10000000-0000-4000-8000-000000000006",
        role: "admin", permissions: [], pv: 1, iat: 1, exp: 9_999_999_999,
        iss: "posapp-cloud", aud: "posapp-desktop",
      },
      requestId: "request-logout",
    };

    const response = await logout(context, env, { revokeAllDeviceSessions: false });
    const revocations = JSON.stringify(client.batch.mock.calls[0]?.[0]);

    expect(response.status).toBe(200);
    expect(revocations).toContain("UPDATE login_sessions");
    expect(revocations).toContain("UPDATE refresh_tokens");
    expect(revocations).toContain(context.claims.sid);
    expect(client.close).toHaveBeenCalledOnce();
  });
});
