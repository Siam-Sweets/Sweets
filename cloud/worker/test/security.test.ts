import type { Client } from "@libsql/client/web";
import { describe, expect, it, vi } from "vitest";
import { ApiError } from "../src/errors";
import { requireDatabaseSchema } from "../src/db";
import { hashPassword, signAccessToken, verifyAccessToken, verifyPassword } from "../src/crypto";
import { effectivePermissions, requirePermission, validateCustomPermissions } from "../src/permissions";
import {
  enforceImmutableTransition,
  isCompositionCursorPublishable,
  requireOperationWrite,
  shouldStageFinancialComposition,
  validateOperationScope,
} from "../src/sync";
import { validateRecordPayload, validateSyncPush } from "../src/validation";
import type { AccessClaims, Env } from "../src/types";

const env: Env = {
  TURSO_DATABASE_URL: "libsql://example.invalid",
  TURSO_AUTH_TOKEN: "test",
  JWT_SIGNING_SECRET: "jwt-test-secret-that-is-at-least-32-characters-long",
  REFRESH_TOKEN_SECRET: "refresh-test-secret-at-least-32-characters-long",
  ACCESS_TOKEN_TTL_SECONDS: "600",
  MAX_SYNC_BATCH: "2",
  SCHEMA_VERSION: "4",
  MINIMUM_CLIENT_SCHEMA_VERSION: "4",
};

describe("database schema readiness", () => {
  it("returns a deployment-specific error when Turso has not been migrated", async () => {
    const client = {
      execute: vi.fn().mockRejectedValue(new Error("SQLITE_ERROR: no such table: schema_migrations")),
    } as unknown as Client;

    await expect(requireDatabaseSchema(client, 4)).rejects.toMatchObject({
      status: 503,
      code: "DATABASE_SCHEMA_NOT_READY",
      details: { expectedSchemaVersion: 4, currentSchemaVersion: 0 },
    });
  });

  it("rejects an older recorded schema version before organization creation", async () => {
    const client = {
      execute: vi.fn().mockResolvedValue({ rows: [{ version: 3 }] }),
    } as unknown as Client;

    await expect(requireDatabaseSchema(client, 4)).rejects.toMatchObject({
      status: 503,
      code: "DATABASE_SCHEMA_NOT_READY",
      details: { expectedSchemaVersion: 4, currentSchemaVersion: 3 },
    });
  });
});

describe("authentication primitives", () => {
  it("hashes passwords with unique PBKDF2 salts and verifies without plaintext storage", async () => {
    const first = await hashPassword("correct-horse-42");
    const second = await hashPassword("correct-horse-42");
    expect(first).not.toBe(second);
    expect(await verifyPassword("correct-horse-42", first)).toBe(true);
    expect(await verifyPassword("wrong-password-9", first)).toBe(false);
  });

  it("signs tenant, session, device, permission, and password-version claims", async () => {
    const signed = await signAccessToken({
      sub: crypto.randomUUID(), tid: crypto.randomUUID(), sid: crypto.randomUUID(),
      did: crypto.randomUUID(), role: "manager", permissions: ["sync.use"], pv: 3,
    }, env);
    const claims = await verifyAccessToken(signed.token, env);
    expect(claims.role).toBe("manager");
    expect(claims.permissions).toContain("sync.use");
    expect(claims.pv).toBe(3);
  });

  it("rejects a tampered access token", async () => {
    const signed = await signAccessToken({
      sub: crypto.randomUUID(), tid: crypto.randomUUID(), sid: crypto.randomUUID(),
      did: crypto.randomUUID(), role: "cashier", permissions: [], pv: 1,
    }, env);
    const parts = signed.token.split(".");
    parts[2] = `${parts[2]![0] === "A" ? "B" : "A"}${parts[2]!.slice(1)}`;
    await expect(verifyAccessToken(parts.join("."), env)).rejects.toMatchObject({
      code: "INVALID_ACCESS_TOKEN",
    });
  });
});

describe("authorization and tenant isolation", () => {
  const claims: AccessClaims = {
    sub: "u", tid: "tenant-a", sid: "s", did: "d", role: "cashier",
    permissions: effectivePermissions("cashier"), pv: 1, iat: 1, exp: 9_999_999_999,
    iss: "posapp-cloud", aud: "posapp-desktop",
  };

  it("enforces backend permissions", () => {
    expect(() => requirePermission(claims, "products.manage")).toThrowError(ApiError);
    expect(() => requirePermission(claims, "sales.create")).not.toThrow();
  });

  it("rejects wildcard or unknown client-supplied custom permissions", () => {
    expect(() => validateCustomPermissions(["sales.read", "*"]))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateCustomPermissions(["not.a.real.permission"]))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(validateCustomPermissions(["sales.read", "sales.read"])).toEqual(["sales.read"]);
    expect(effectivePermissions("cashier", ["*"])).not.toContain("*");
  });

  it("rejects cross-tenant records even when the client supplies the record ID", () => {
    expect(() => validateOperationScope("tenant-a", "store-a", "store-a", "tenant-b"))
      .toThrowError(expect.objectContaining({ code: "CROSS_TENANT_ACCESS_DENIED" }));
  });

  it("rejects writes to a different store", () => {
    expect(() => validateOperationScope("tenant-a", "store-b", "store-a"))
      .toThrowError(expect.objectContaining({ code: "STORE_ACCESS_DENIED" }));
  });

  it("requires an explicit matching store on store-scoped writes", () => {
    expect(() => validateOperationScope("tenant-a", null, "store-a"))
      .toThrowError(expect.objectContaining({ code: "STORE_ACCESS_DENIED" }));
  });

  it("rejects mutation of a record owned by another store", () => {
    expect(() => validateOperationScope("tenant-a", "store-a", "store-a", "tenant-a", "store-b"))
      .toThrowError(expect.objectContaining({ code: "STORE_ACCESS_DENIED" }));
  });

  it("allows cashier stock deductions only when linked to a sale", () => {
    const context = { claims, requestId: "request-1" };
    const base = {
      operationId: crypto.randomUUID(), idempotencyKey: crypto.randomUUID(),
      entityType: "inventory_movements", recordId: crypto.randomUUID(), storeId: null,
      operation: "upsert" as const, baseVersion: 0, clientTimestampUtc: new Date().toISOString(),
    };
    expect(() => requireOperationWrite(context, {
      ...base, payload: { type: 0, quantity: -1, saleRecordId: crypto.randomUUID() },
    })).not.toThrow();
    expect(() => requireOperationWrite(context, {
      ...base, payload: { type: 3, quantity: 100 },
    })).toThrowError(expect.objectContaining({ code: "PERMISSION_DENIED" }));
  });

  it("requires refund or void permissions for corrective sale states", () => {
    const context = { claims, requestId: "request-1" };
    const base = {
      operationId: crypto.randomUUID(), idempotencyKey: crypto.randomUUID(), entityType: "sales",
      recordId: crypto.randomUUID(), storeId: null, operation: "upsert" as const,
      baseVersion: 1, clientTimestampUtc: new Date().toISOString(),
    };
    expect(() => requireOperationWrite(context, { ...base, payload: { status: 2 } }))
      .toThrowError(expect.objectContaining({ code: "PERMISSION_DENIED" }));
    expect(() => requireOperationWrite(context, { ...base, payload: { status: 3 } }))
      .toThrowError(expect.objectContaining({ code: "PERMISSION_DENIED" }));
  });
});

describe("synchronization validation", () => {
  it("accepts a bounded UUID-based operation batch", () => {
    const value = validateSyncPush({
      deviceId: crypto.randomUUID(), storeId: crypto.randomUUID(), clientSchemaVersion: 4,
      operations: [{
        operationId: crypto.randomUUID(), idempotencyKey: crypto.randomUUID(), entityType: "products",
        recordId: crypto.randomUUID(), storeId: null, operation: "upsert", baseVersion: 0,
        payload: { name: "Tea", price: 10 }, clientTimestampUtc: new Date().toISOString(),
      }],
    }, env);
    expect(value.operations).toHaveLength(1);
  });

  it("rejects invalid inventory movements", () => {
    expect(() => validateRecordPayload("inventory_movements", { quantity: 0 }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("requires immutable purchase sources for purchase stock receipts", () => {
    expect(() => validateRecordPayload("inventory_movements", {
      type: 2, quantity: 5, productRecordId: crypto.randomUUID(),
    })).toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("inventory_movements", {
      type: 2, quantity: 5, productRecordId: crypto.randomUUID(),
      purchaseDocumentRecordId: crypto.randomUUID(), purchaseItemRecordId: crypto.randomUUID(),
    })).not.toThrow();
  });

  it("rejects sale totals that do not reconcile", () => {
    expect(() => validateRecordPayload("sales", {
      status: 0, subtotal: 10, discountTotal: 1, taxTotal: 0, rounding: 0,
      total: 9, amountPaid: 10, change: 0, expectedItemCount: 1, expectedPaymentCount: 1,
    })).toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("requires immutable child counts on financial headers", () => {
    expect(() => validateRecordPayload("sales", {
      status: 0, receiptNumber: "SALE-COUNT", subtotal: 10, discountTotal: 0,
      taxTotal: 0, rounding: 0, amountPaid: 10, change: 0,
    })).toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("sales", {
      status: 0, receiptNumber: "SALE-COUNT", subtotal: 10, discountTotal: 0,
      taxTotal: 0, rounding: 0, amountPaid: 10, change: 0,
      expectedItemCount: 1, expectedPaymentCount: 1,
    })).not.toThrow();
  });

  it("stages a new finalized sale and a suspended-sale completion", () => {
    expect(shouldStageFinancialComposition("sales", {}, { status: 0 }, false)).toBe(true);
    expect(shouldStageFinancialComposition("sales", { status: 1 }, { status: 0 }, true)).toBe(true);
    expect(shouldStageFinancialComposition("sales", {}, { status: 1 }, false)).toBe(false);
    expect(shouldStageFinancialComposition("sales", { status: 0 }, { status: 2 }, true)).toBe(false);
    expect(shouldStageFinancialComposition("purchases", {}, { status: 0 }, false)).toBe(true);
  });

  it("keeps interrupted compositions private and publishes only the completion replay", () => {
    expect(isCompositionCursorPublishable(10, "pending", null, true)).toBe(false);
    expect(isCompositionCursorPublishable(11, "pending", null, false)).toBe(false);
    expect(isCompositionCursorPublishable(10, "complete", 20, true)).toBe(false);
    expect(isCompositionCursorPublishable(19, "complete", 20, false)).toBe(false);
    expect(isCompositionCursorPublishable(20, "complete", 20, true)).toBe(true);
    expect(isCompositionCursorPublishable(21, "complete", 20, false)).toBe(true);
  });

  it("validates product and customer master-data boundaries", () => {
    expect(() => validateRecordPayload("products", {
      name: "Tea", price: 10, costPrice: 6, taxRate: 5, unit: 0,
    }))
      .not.toThrow();
    expect(() => validateRecordPayload("products", { name: "", price: 10 }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("customers", { name: "Amina", phone: "01700000000", isActive: true }))
      .not.toThrow();
    expect(() => validateRecordPayload("customers", { name: "x".repeat(101) }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("rejects type-confused, nested, and invalid-date payload values", () => {
    expect(() => validateRecordPayload("products", { name: "Tea", price: "10" }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("customers", { name: "Amina", metadata: { admin: true } }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("customers", { name: "Amina", updatedAt: "tomorrow" }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("keeps device-local application settings out of the cloud", () => {
    expect(() => validateRecordPayload("settings", { key: "app:setup-complete", value: "true" }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("settings", { key: "store:config", value: "{}" }))
      .not.toThrow();
  });

  it("requires reconciled register totals and positive cash movements", () => {
    expect(() => validateRecordPayload("register_sessions", {
      openedAt: new Date().toISOString(), openingFloat: 100,
      closedAt: new Date().toISOString(), expectedCash: 140, countedCash: 145, variance: 5,
    })).not.toThrow();
    expect(() => validateRecordPayload("register_sessions", {
      openedAt: new Date().toISOString(), openingFloat: 100,
      closedAt: new Date().toISOString(), expectedCash: 140, countedCash: 145, variance: 4,
    })).toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
    expect(() => validateRecordPayload("cash_movements", { type: 0, amount: 0, description: "Float" }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("accepts a reconciled refund and rejects zero payments", () => {
    expect(() => validateRecordPayload("sales", {
      status: 3, receiptNumber: "REF-1", subtotal: -10, discountTotal: 0,
      taxTotal: 0, rounding: 0, total: -10, amountPaid: -10, change: 0,
      refundedSaleRecordId: crypto.randomUUID(), expectedItemCount: 1, expectedPaymentCount: 1,
    })).not.toThrow();
    expect(() => validateRecordPayload("payments", {
      saleRecordId: crypto.randomUUID(), method: 0, amount: 0,
    })).toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("rejects a purchase whose total does not match subtotal and tax", () => {
    expect(() => validateRecordPayload("purchases", {
      status: 0, documentNumber: "PUR-1", subtotal: 10, taxTotal: 2, total: 11,
      expectedItemCount: 1,
    })).toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("validates sale-line arithmetic and relationship IDs", () => {
    const payload = {
      saleRecordId: crypto.randomUUID(), productRecordId: crypto.randomUUID(),
      productName: "Tea", quantity: 2, unitPrice: 5, costPrice: 3, taxRate: 10, discountAmount: 1,
      lineTotal: 9, lineTax: 0.9, catalogVersion: 1, legacyPriceSnapshot: false,
    };
    expect(() => validateRecordPayload("sale_items", payload)).not.toThrow();
    expect(() => validateRecordPayload("sale_items", { ...payload, lineTax: 9 }))
      .toThrowError(expect.objectContaining({ code: "VALIDATION_ERROR" }));
  });

  it("rejects duplicate operation identifiers before a batch reaches Turso", () => {
    const operationId = crypto.randomUUID();
    const idempotencyKey = crypto.randomUUID();
    const operation = {
      operationId, idempotencyKey, entityType: "customers", recordId: crypto.randomUUID(),
      storeId: null, operation: "upsert", baseVersion: 0, payload: { name: "A" },
      clientTimestampUtc: new Date().toISOString(),
    };
    expect(() => validateSyncPush({
      deviceId: crypto.randomUUID(), storeId: crypto.randomUUID(), clientSchemaVersion: 4,
      operations: [operation, { ...operation, recordId: crypto.randomUUID() }],
    }, env)).toThrowError(expect.objectContaining({ code: "DUPLICATE_OPERATION_IN_BATCH" }));
  });

  it("prevents overwriting an immutable payment", () => {
    expect(() => enforceImmutableTransition("payments", { amount: 10 }, { amount: 11 }))
      .toThrowError(expect.objectContaining({ code: "IMMUTABLE_TRANSACTION" }));
  });

  it("permits draft-line replacement only in the suspended-sale transition batch", () => {
    const saleId = crypto.randomUUID();
    const current = { saleRecordId: saleId, quantity: 1, unitPrice: 10 };
    const next = { ...current, quantity: 2 };
    expect(() => enforceImmutableTransition("sale_items", current, next, new Set([saleId])))
      .not.toThrow();
    expect(() => enforceImmutableTransition("sale_items", current, next, new Set()))
      .toThrowError(expect.objectContaining({ code: "IMMUTABLE_TRANSACTION" }));
  });

  it("allows a completed sale to become voided only when financial facts are unchanged", () => {
    const sale = {
      status: 0, receiptNumber: "R-1", saleDate: "2026-07-19T00:00:00Z", subtotal: 10,
      discountTotal: 0, taxTotal: 0, rounding: 0, amountPaid: 10, change: 0,
    };
    expect(() => enforceImmutableTransition("sales", sale, { ...sale, status: 2 })).not.toThrow();
    expect(() => enforceImmutableTransition("sales", sale, { ...sale, status: 2, subtotal: 9 }))
      .toThrowError(expect.objectContaining({ code: "IMMUTABLE_TRANSACTION" }));
    expect(() => enforceImmutableTransition("sales", sale, { ...sale, customerRecordId: crypto.randomUUID() }))
      .toThrowError(expect.objectContaining({ code: "IMMUTABLE_TRANSACTION" }));
    expect(() => enforceImmutableTransition("sales", sale, { ...sale, updatedAt: new Date().toISOString() }))
      .not.toThrow();
  });
});
