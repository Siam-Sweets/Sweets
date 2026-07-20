import { ApiError } from "./errors";
import type { AccessClaims, Env } from "./types";

const encoder = new TextEncoder();
const decoder = new TextDecoder();
export const PBKDF2_ITERATIONS = 310_000;

export function base64UrlEncode(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_");
}

export function base64UrlDecode(value: string): Uint8Array {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - (normalized.length % 4)) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

export function randomToken(byteLength = 32): string {
  return base64UrlEncode(crypto.getRandomValues(new Uint8Array(byteLength)));
}

export async function hashPassword(password: string, iterations = PBKDF2_ITERATIONS): Promise<string> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const key = await crypto.subtle.importKey("raw", encoder.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt, iterations },
    key,
    256,
  );
  return `pbkdf2_sha256$${iterations}$${base64UrlEncode(salt)}$${base64UrlEncode(new Uint8Array(bits))}`;
}

export async function verifyPassword(password: string, stored: string): Promise<boolean> {
  const parts = stored.split("$");
  if (parts.length !== 4 || parts[0] !== "pbkdf2_sha256") return false;
  const iterations = Number(parts[1]);
  if (!Number.isInteger(iterations) || iterations < 100_000 || iterations > 1_000_000) return false;
  try {
    const salt = base64UrlDecode(parts[2]!);
    const expected = base64UrlDecode(parts[3]!);
    const key = await crypto.subtle.importKey("raw", encoder.encode(password), "PBKDF2", false, ["deriveBits"]);
    const actual = new Uint8Array(await crypto.subtle.deriveBits(
      { name: "PBKDF2", hash: "SHA-256", salt: toArrayBuffer(salt), iterations }, key, expected.length * 8,
    ));
    return timingSafeEqual(actual, expected);
  } catch {
    return false;
  }
}

export async function sha256(value: string): Promise<string> {
  return base64UrlEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(value))));
}

export async function signAccessToken(
  claims: Omit<AccessClaims, "iat" | "exp" | "iss" | "aud">,
  env: Env,
): Promise<{ token: string; expiresAtUtc: string }> {
  const now = Math.floor(Date.now() / 1000);
  const ttl = clampNumber(env.ACCESS_TOKEN_TTL_SECONDS, 600, 300, 900);
  const payload: AccessClaims = {
    ...claims,
    iat: now,
    exp: now + ttl,
    iss: "posapp-cloud",
    aud: "posapp-desktop",
  };
  const header = base64UrlEncode(encoder.encode(JSON.stringify({ alg: "HS256", typ: "JWT" })));
  const encodedPayload = base64UrlEncode(encoder.encode(JSON.stringify(payload)));
  const input = `${header}.${encodedPayload}`;
  const key = await importHmacKey(env.JWT_SIGNING_SECRET, ["sign"]);
  const signature = new Uint8Array(await crypto.subtle.sign("HMAC", key, encoder.encode(input)));
  return { token: `${input}.${base64UrlEncode(signature)}`, expiresAtUtc: new Date((now + ttl) * 1000).toISOString() };
}

export async function verifyAccessToken(token: string, env: Env): Promise<AccessClaims> {
  const parts = token.split(".");
  if (parts.length !== 3) throw new ApiError(401, "INVALID_ACCESS_TOKEN", "The access token is invalid.");
  try {
    const key = await importHmacKey(env.JWT_SIGNING_SECRET, ["verify"]);
    const valid = await crypto.subtle.verify(
      "HMAC", key, toArrayBuffer(base64UrlDecode(parts[2]!)), encoder.encode(`${parts[0]}.${parts[1]}`),
    );
    if (!valid) throw new Error("signature");
    const claims = JSON.parse(decoder.decode(base64UrlDecode(parts[1]!))) as AccessClaims;
    const now = Math.floor(Date.now() / 1000);
    if (claims.iss !== "posapp-cloud" || claims.aud !== "posapp-desktop") throw new Error("issuer");
    if (!claims.sub || !claims.tid || !claims.sid || !claims.did) throw new Error("claims");
    if (claims.exp <= now) throw new ApiError(401, "ACCESS_TOKEN_EXPIRED", "The access token has expired.");
    return claims;
  } catch (error) {
    if (error instanceof ApiError) throw error;
    throw new ApiError(401, "INVALID_ACCESS_TOKEN", "The access token is invalid.");
  }
}

export function timingSafeEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) difference |= left[index]! ^ right[index]!;
  return difference === 0;
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.slice().buffer as ArrayBuffer;
}

function importHmacKey(secret: string, usages: KeyUsage[]): Promise<CryptoKey> {
  if (!secret || secret.length < 32) throw new ApiError(500, "AUTHENTICATION_CONFIGURATION_ERROR", "The authentication secrets are missing or too short.");
  return crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, usages);
}

export function clampNumber(value: string | undefined, fallback: number, minimum: number, maximum: number): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.min(maximum, Math.max(minimum, parsed)) : fallback;
}
