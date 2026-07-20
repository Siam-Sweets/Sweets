import { ApiError } from "./errors";
import type { DeviceInput, Env, SyncOperation, SyncPushBody } from "./types";
import { clampNumber } from "./crypto";

export function requireObject(value: unknown): Record<string, unknown> {
  if (value == null || typeof value !== "object" || Array.isArray(value)) {
    throw new ApiError(400, "VALIDATION_ERROR", "A JSON object is required.");
  }
  return value as Record<string, unknown>;
}

export function requiredString(
  source: Record<string, unknown>,
  name: string,
  maximum: number,
  minimum = 1,
): string {
  const value = typeof source[name] === "string" ? source[name].trim() : "";
  if (value.length < minimum || value.length > maximum) {
    throw new ApiError(400, "VALIDATION_ERROR", `${name} must contain ${minimum} to ${maximum} characters.`);
  }
  return value;
}

export function optionalString(source: Record<string, unknown>, name: string, maximum: number): string | null {
  if (source[name] == null || source[name] === "") return null;
  if (typeof source[name] !== "string" || source[name].length > maximum) {
    throw new ApiError(400, "VALIDATION_ERROR", `${name} is invalid.`);
  }
  return source[name].trim();
}

export function validateEmail(value: string): string {
  const normalized = value.trim().toLowerCase();
  if (normalized.length > 255 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalized)) {
    throw new ApiError(400, "VALIDATION_ERROR", "Enter a valid email address.");
  }
  return normalized;
}

export function validateUsername(value: string): string {
  const normalized = value.trim().toLowerCase();
  if (!/^[a-z0-9][a-z0-9._-]{2,59}$/.test(normalized)) {
    throw new ApiError(400, "VALIDATION_ERROR", "Username must be 3 to 60 letters, numbers, dots, underscores, or hyphens.");
  }
  return normalized;
}

export function validatePassword(value: unknown): string {
  if (typeof value !== "string" || value.length < 10 || value.length > 128 ||
      !/[A-Za-z]/.test(value) || !/[0-9]/.test(value)) {
    throw new ApiError(400, "WEAK_PASSWORD", "Password must be 10 to 128 characters and include a letter and number.");
  }
  return value;
}

export function validateUuid(value: unknown, field: string): string {
  if (typeof value !== "string" ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value)) {
    throw new ApiError(400, "VALIDATION_ERROR", `${field} must be a UUID.`);
  }
  return value.toLowerCase();
}

export function validateDevice(value: unknown): DeviceInput {
  const source = requireObject(value);
  return {
    id: validateUuid(source.id, "device.id"),
    name: requiredString(source, "name", 160),
    operatingSystem: optionalString(source, "operatingSystem", 160) ?? "Windows",
    machineName: optionalString(source, "machineName", 160) ?? undefined,
  };
}

export function validateSyncPush(value: unknown, env: Env): SyncPushBody {
  const source = requireObject(value);
  const operations = source.operations;
  // A single operation can require several tenant, relationship, immutable
  // ledger, idempotency, and audit statements over Turso's HTTP protocol. Keep
  // the hard ceiling at two so a configured value cannot accidentally exceed
  // the Workers Free external-subrequest limit.
  const maxBatch = clampNumber(env.MAX_SYNC_BATCH, 2, 1, 2);
  if (!Array.isArray(operations) || operations.length < 1 || operations.length > maxBatch) {
    throw new ApiError(400, "VALIDATION_ERROR", `operations must contain 1 to ${maxBatch} records.`);
  }
  const schema = source.clientSchemaVersion;
  if (typeof schema !== "number" || !Number.isInteger(schema) || schema < 1) {
    throw new ApiError(400, "VALIDATION_ERROR", "clientSchemaVersion is invalid.");
  }
  const validated = operations.map(validateSyncOperation);
  const operationIds = new Set<string>();
  const idempotencyKeys = new Set<string>();
  for (const operation of validated) {
    if (operationIds.has(operation.operationId) || idempotencyKeys.has(operation.idempotencyKey)) {
      throw new ApiError(409, "DUPLICATE_OPERATION_IN_BATCH",
        "A synchronization batch cannot contain duplicate operation or idempotency identifiers.");
    }
    operationIds.add(operation.operationId);
    idempotencyKeys.add(operation.idempotencyKey);
  }
  return {
    deviceId: validateUuid(source.deviceId, "deviceId"),
    storeId: validateUuid(source.storeId, "storeId"),
    clientSchemaVersion: schema,
    operations: validated,
  };
}

function validateSyncOperation(value: unknown): SyncOperation {
  const source = requireObject(value);
  const operation = source.operation;
  if (operation !== "upsert" && operation !== "delete") {
    throw new ApiError(400, "VALIDATION_ERROR", "operation must be upsert or delete.");
  }
  const baseVersion = source.baseVersion;
  if (typeof baseVersion !== "number" || !Number.isSafeInteger(baseVersion) || baseVersion < 0) {
    throw new ApiError(400, "VALIDATION_ERROR", "baseVersion is invalid.");
  }
  const entityType = requiredString(source, "entityType", 64).toLowerCase();
  const payload = operation === "upsert" ? requireObject(source.payload) : {};
  const serializedLength = JSON.stringify(payload).length;
  if (serializedLength > 128_000) throw new ApiError(413, "PAYLOAD_TOO_LARGE", "A record payload is too large.");
  const clientTimestamp = requiredString(source, "clientTimestampUtc", 40);
  const parsedTimestamp = Date.parse(clientTimestamp);
  if (!Number.isFinite(parsedTimestamp) || !/T.*(?:Z|[+-]\d{2}:\d{2})$/i.test(clientTimestamp))
    throw new ApiError(400, "VALIDATION_ERROR", "clientTimestampUtc must be a UTC timestamp.");
  return {
    operationId: validateUuid(source.operationId, "operationId"),
    idempotencyKey: requiredString(source, "idempotencyKey", 128, 16),
    entityType,
    recordId: validateUuid(source.recordId, "recordId"),
    storeId: source.storeId == null ? null : validateUuid(source.storeId, "storeId"),
    operation,
    baseVersion,
    payload,
    clientTimestampUtc: new Date(parsedTimestamp).toISOString(),
  };
}

export function validateRecordPayload(entityType: string, payload: Record<string, unknown>): void {
  validateFlatPayload(payload);
  const decimal = (name: string, allowNegative = false): void => {
    if (payload[name] == null) return;
    const value = payload[name];
    if (typeof value !== "number" || !Number.isFinite(value) ||
        (!allowNegative && value < 0) || Math.abs(value) > 1_000_000_000_000) {
      throw new ApiError(400, "VALIDATION_ERROR", `${entityType}.${name} is invalid.`);
    }
  };
  if (entityType === "products") {
    const price = requiredDecimal(payload, "price");
    const costPrice = requiredDecimal(payload, "costPrice");
    const taxRate = requiredDecimal(payload, "taxRate");
    decimal("stockQuantity", true);
    payloadString(payload, "name", 100, true);
    payloadString(payload, "description", 1_000);
    payloadString(payload, "sku", 64);
    payloadString(payload, "barcode", 64);
    requiredInteger(payload, "unit", [0, 1, 2, 3, 4, 5, 6]);
    if (price < 0 || costPrice < 0 || taxRate < 0 || taxRate > 100)
      throw invalidFinancial("Product price, cost, or tax rate is invalid.");
    optionalBoolean(payload, "trackInventory");
    optionalBoolean(payload, "isWeighted");
    optionalBoolean(payload, "isActive");
    optionalBoolean(payload, "allowDiscount");
  }
  if (["sales", "payments", "purchases", "expenses", "cash_movements"].includes(entityType)) {
    for (const name of ["subtotal", "discountTotal", "taxTotal", "amountPaid", "amount", "total"])
      decimal(name, entityType === "sales" || entityType === "payments");
  }
  if (entityType === "inventory_movements") {
    decimal("quantity", true);
    if (Number(payload.quantity) === 0) throw new ApiError(400, "VALIDATION_ERROR", "Inventory movement cannot be zero.");
  }
  if (entityType === "users" && payload.role != null) {
    const role = payload.role;
    if (typeof role !== "number")
      throw new ApiError(400, "VALIDATION_ERROR", "User role is invalid.");
    if (![0, 1, 2].includes(role)) throw new ApiError(400, "VALIDATION_ERROR", "User role is invalid.");
    delete payload.passwordHash;
    delete payload.passwordSalt;
  }

  validateMasterPayload(entityType, payload);

  if (entityType === "sales") validateSalePayload(payload);
  if (entityType === "sale_items") validateSaleItemPayload(payload);
  if (entityType === "payments") validatePaymentPayload(payload);
  if (entityType === "purchases") validatePurchasePayload(payload);
  if (entityType === "purchase_items") validatePurchaseItemPayload(payload);
  if (entityType === "inventory_movements") validateInventoryMovementPayload(payload);
}

function validateMasterPayload(entityType: string, payload: Record<string, unknown>): void {
  if (entityType === "categories") {
    payloadString(payload, "name", 100, true);
    payloadString(payload, "description", 500);
    payloadString(payload, "color", 20);
    optionalInteger(payload, "sortOrder");
    optionalBoolean(payload, "isActive");
  } else if (entityType === "customers") {
    payloadString(payload, "name", 100, true);
    payloadString(payload, "phone", 20);
    payloadString(payload, "email", 100);
    payloadString(payload, "address", 300);
    payloadString(payload, "taxId", 20);
    boundedOptionalDecimal(payload, "loyaltyPoints", true);
    boundedOptionalDecimal(payload, "storeCredit", true);
    boundedOptionalDecimal(payload, "loyaltyRate", false, 100);
    optionalBoolean(payload, "isActive");
  } else if (entityType === "suppliers") {
    payloadString(payload, "name", 100, true);
    payloadString(payload, "phone", 30);
    payloadString(payload, "email", 120);
    payloadString(payload, "address", 300);
    payloadString(payload, "taxId", 40);
    payloadString(payload, "notes", 500);
    optionalBoolean(payload, "isActive");
  } else if (entityType === "discounts" || entityType === "promotions") {
    payloadString(payload, "name", 60, true);
    payloadString(payload, "description", 200);
    payloadString(payload, "code", 40);
    const type = requiredInteger(payload, "type", [0, 1]);
    const value = requiredDecimal(payload, "value");
    if (value < 0 || (type === 0 && value > 100))
      throw invalidFinancial("Discount value is invalid.");
    optionalBoolean(payload, "isActive");
  } else if (entityType === "taxes") {
    payloadString(payload, "name", 60, true);
    const rate = requiredDecimal(payload, "rate");
    if (rate < 0 || rate > 100) throw invalidFinancial("Tax rate cannot exceed 100 percent.");
    optionalBoolean(payload, "isIncluded");
    optionalBoolean(payload, "isDefault");
    optionalBoolean(payload, "isActive");
  } else if (entityType === "settings") {
    const key = payloadString(payload, "key", 64, true)!;
    if (key.toLowerCase().startsWith("app:"))
      throw new ApiError(400, "VALIDATION_ERROR", "Device-local application settings cannot be synchronized.");
    payloadString(payload, "value", 8_192);
    payloadString(payload, "description", 200);
  } else if (entityType === "users") {
    payloadString(payload, "username", 60, true);
    payloadString(payload, "fullName", 100, true);
    payloadString(payload, "email", 255);
    optionalBoolean(payload, "isActive");
  } else if (entityType === "register_sessions") {
    const openingFloat = requiredDecimal(payload, "openingFloat");
    if (openingFloat < 0) throw invalidFinancial("Register opening cash cannot be negative.");
    if (typeof payload.openedAt !== "string" || !Number.isFinite(Date.parse(payload.openedAt)))
      throw invalidFinancial("Register opening time is required.");
    if (payload.closedAt == null) {
      if (payload.closedByUserRecordId != null || payload.expectedCash != null ||
          payload.countedCash != null || payload.variance != null)
        throw invalidFinancial("An open register cannot contain closing totals.");
    } else {
      if (typeof payload.closedAt !== "string" ||
          Date.parse(payload.closedAt) < Date.parse(payload.openedAt))
        throw invalidFinancial("Register closing time cannot precede its opening time.");
      const expectedCash = requiredDecimal(payload, "expectedCash");
      const countedCash = requiredDecimal(payload, "countedCash");
      const variance = requiredDecimal(payload, "variance");
      if (countedCash < 0 || !nearlyEqual(countedCash - expectedCash, variance))
        throw invalidFinancial("Register closing totals do not reconcile.");
    }
    payloadString(payload, "note", 500);
  } else if (entityType === "cash_movements") {
    payloadString(payload, "description", 300, true);
    requiredInteger(payload, "type", [0, 1]);
    const amount = requiredDecimal(payload, "amount");
    if (amount <= 0) throw invalidFinancial("Cash movement amount must be greater than zero.");
  } else if (entityType === "expenses") {
    payloadString(payload, "category", 100, true);
    payloadString(payload, "description", 300, true);
    const amount = boundedOptionalDecimal(payload, "amount");
    if (amount == null || amount <= 0) throw invalidFinancial("Expense amount must be greater than zero.");
    optionalBoolean(payload, "isVoided");
  }
}

function validateSalePayload(payload: Record<string, unknown>): void {
  payloadString(payload, "receiptNumber", 32, true);
  payloadString(payload, "serviceType", 32);
  payloadString(payload, "note", 500);
  const status = requiredInteger(payload, "status", [0, 1, 2, 3]);
  const subtotal = requiredDecimal(payload, "subtotal");
  const discount = requiredDecimal(payload, "discountTotal");
  const tax = requiredDecimal(payload, "taxTotal");
  const rounding = requiredDecimal(payload, "rounding");
  const amountPaid = requiredDecimal(payload, "amountPaid");
  const change = requiredDecimal(payload, "change");
  const expectedItems = requiredCount(payload, "expectedItemCount");
  const expectedPayments = requiredCount(payload, "expectedPaymentCount");
  const total = subtotal - discount + tax + rounding;
  if (expectedItems < 1)
    throw invalidFinancial("A sale must declare at least one immutable line.");
  if (status === 1 && expectedPayments !== 0)
    throw invalidFinancial("A suspended sale cannot contain payments.");
  if (status !== 1 && total !== 0 && expectedPayments < 1)
    throw invalidFinancial("A finalized non-zero sale must declare at least one payment.");
  if (payload.total != null && !nearlyEqual(requiredDecimal(payload, "total"), total))
    throw invalidFinancial("Sale total does not match its subtotal, discount, tax, and rounding.");
  if (status !== 1 && !nearlyEqual(amountPaid - change, total))
    throw invalidFinancial("Sale tender and change do not reconcile to the total.");
  if (status === 3) {
    if (total >= 0 || amountPaid >= 0 || change !== 0)
      throw invalidFinancial("A refund must contain a negative reconciled total and payment.");
    validateUuid(payload.refundedSaleRecordId, "refundedSaleRecordId");
  } else if (subtotal < 0 || discount < 0 || discount > subtotal || tax < 0 || total < 0 ||
             amountPaid < 0 || change < 0) {
    throw invalidFinancial("A sale contains invalid financial values.");
  }
}

function validateSaleItemPayload(payload: Record<string, unknown>): void {
  validateUuid(payload.saleRecordId, "saleRecordId");
  validateUuid(payload.productRecordId, "productRecordId");
  const quantity = requiredDecimal(payload, "quantity");
  const unitPrice = requiredDecimal(payload, "unitPrice");
  const costPrice = requiredDecimal(payload, "costPrice");
  const taxRate = requiredDecimal(payload, "taxRate");
  const discount = requiredDecimal(payload, "discountAmount");
  optionalBoolean(payload, "legacyPriceSnapshot");
  const legacyPriceSnapshot = payload.legacyPriceSnapshot === true;
  const catalogVersion = requiredCount(payload, "catalogVersion");
  if ((legacyPriceSnapshot && catalogVersion !== 0) || (!legacyPriceSnapshot && catalogVersion < 1))
    throw invalidFinancial("A sale line must identify its authoritative catalog snapshot.");
  payloadString(payload, "productName", 100, true);
  payloadString(payload, "sku", 64);
  payloadString(payload, "discountReason", 200);
  if (quantity === 0 || unitPrice < 0 || costPrice < 0 || taxRate < 0 || taxRate > 100)
    throw invalidFinancial("A sale line contains an invalid quantity, price, cost, or tax rate.");
  const gross = unitPrice * quantity;
  if (quantity > 0 && (discount < 0 || discount > gross))
    throw invalidFinancial("A sale-line discount exceeds its gross amount.");
  if (quantity < 0) {
    validateUuid(payload.refundedSaleItemRecordId, "refundedSaleItemRecordId");
    if (discount > 0 || discount < gross)
      throw invalidFinancial("A refund-line discount is invalid.");
  }
  const lineTotal = gross - discount;
  const lineTax = lineTotal * taxRate / 100;
  if (payload.lineTotal != null && !nearlyEqual(requiredDecimal(payload, "lineTotal"), lineTotal))
    throw invalidFinancial("A sale line total is inconsistent.");
  if (payload.lineTax != null && !nearlyEqual(requiredDecimal(payload, "lineTax"), lineTax))
    throw invalidFinancial("A sale line tax is inconsistent.");
}

function validatePaymentPayload(payload: Record<string, unknown>): void {
  validateUuid(payload.saleRecordId, "saleRecordId");
  const amount = requiredDecimal(payload, "amount");
  if (amount === 0) throw invalidFinancial("A payment amount cannot be zero.");
  requiredInteger(payload, "method", [0, 1, 2, 3, 4, 5, 99]);
  payloadString(payload, "reference", 64);
}

function validatePurchasePayload(payload: Record<string, unknown>): void {
  payloadString(payload, "documentNumber", 32, true);
  payloadString(payload, "externalReference", 80);
  payloadString(payload, "note", 500);
  requiredInteger(payload, "status", [0, 1]);
  const subtotal = requiredDecimal(payload, "subtotal");
  const tax = requiredDecimal(payload, "taxTotal");
  const total = requiredDecimal(payload, "total");
  const expectedItems = requiredCount(payload, "expectedItemCount");
  if (expectedItems < 1)
    throw invalidFinancial("A purchase must declare at least one immutable line.");
  if (subtotal < 0 || tax < 0 || total < 0 || !nearlyEqual(subtotal + tax, total))
    throw invalidFinancial("Purchase totals do not reconcile.");
}

function validatePurchaseItemPayload(payload: Record<string, unknown>): void {
  validateUuid(payload.purchaseDocumentRecordId, "purchaseDocumentRecordId");
  validateUuid(payload.productRecordId, "productRecordId");
  const quantity = requiredDecimal(payload, "quantity");
  const unitCost = requiredDecimal(payload, "unitCost");
  const taxRate = requiredDecimal(payload, "taxRate");
  payloadString(payload, "productName", 100, true);
  payloadString(payload, "sku", 64);
  if (quantity <= 0 || unitCost < 0 || taxRate < 0 || taxRate > 100)
    throw invalidFinancial("A purchase line contains an invalid quantity, cost, or tax rate.");
  const subtotal = quantity * unitCost;
  const tax = subtotal * taxRate / 100;
  if (payload.lineSubtotal != null && !nearlyEqual(requiredDecimal(payload, "lineSubtotal"), subtotal))
    throw invalidFinancial("A purchase line subtotal is inconsistent.");
  if (payload.lineTax != null && !nearlyEqual(requiredDecimal(payload, "lineTax"), tax))
    throw invalidFinancial("A purchase line tax is inconsistent.");
  if (payload.lineTotal != null && !nearlyEqual(requiredDecimal(payload, "lineTotal"), subtotal + tax))
    throw invalidFinancial("A purchase line total is inconsistent.");
}

function validateInventoryMovementPayload(payload: Record<string, unknown>): void {
  validateUuid(payload.productRecordId, "productRecordId");
  const type = requiredInteger(payload, "type", [0, 1, 2, 3, 4, 5, 6]);
  const quantity = requiredDecimal(payload, "quantity");
  payloadString(payload, "note", 500);
  boundedOptionalDecimal(payload, "unitCost");
  if (quantity === 0) throw invalidFinancial("Inventory movement cannot be zero.");
  if ((type === 0 || type === 5) && quantity >= 0)
    throw invalidFinancial("Sale and wastage movements must reduce inventory.");
  if ((type === 1 || type === 2 || type === 4) && quantity <= 0)
    throw invalidFinancial("Return, purchase, and opening movements must increase inventory.");
  if (type === 0 || type === 1) {
    validateUuid(payload.saleRecordId, "saleRecordId");
    validateUuid(payload.saleItemRecordId, "saleItemRecordId");
  }
  if (type === 2) {
    validateUuid(payload.purchaseDocumentRecordId, "purchaseDocumentRecordId");
    validateUuid(payload.purchaseItemRecordId, "purchaseItemRecordId");
  }
}

function payloadString(
  payload: Record<string, unknown>,
  name: string,
  maximum: number,
  required = false,
): string | null {
  const raw = payload[name];
  if (raw == null || raw === "") {
    if (required) throw new ApiError(400, "VALIDATION_ERROR", `${name} is required.`);
    return null;
  }
  if (typeof raw !== "string" || raw.length > maximum || (required && raw.trim().length === 0))
    throw new ApiError(400, "VALIDATION_ERROR", `${name} is invalid.`);
  return raw;
}

function optionalBoolean(payload: Record<string, unknown>, name: string): void {
  if (payload[name] != null && typeof payload[name] !== "boolean")
    throw new ApiError(400, "VALIDATION_ERROR", `${name} must be true or false.`);
}

function optionalInteger(
  payload: Record<string, unknown>,
  name: string,
  allowed?: number[],
): number | null {
  if (payload[name] == null) return null;
  const value = payload[name];
  if (typeof value !== "number" || !Number.isSafeInteger(value) ||
      (allowed != null && !allowed.includes(value)))
    throw new ApiError(400, "VALIDATION_ERROR", `${name} is invalid.`);
  return value;
}

function boundedOptionalDecimal(
  payload: Record<string, unknown>,
  name: string,
  allowNegative = false,
  maximum = 1_000_000_000_000,
): number | null {
  if (payload[name] == null) return null;
  const value = payload[name];
  if (typeof value !== "number" || !Number.isFinite(value) ||
      Math.abs(value) > maximum || (!allowNegative && value < 0))
    throw invalidFinancial(`${name} is invalid.`);
  return value;
}

function requiredDecimal(payload: Record<string, unknown>, name: string): number {
  const value = payload[name];
  if (typeof value !== "number" || !Number.isFinite(value) || Math.abs(value) > 1_000_000_000_000)
    throw invalidFinancial(`${name} is missing or invalid.`);
  return value;
}

function requiredInteger(payload: Record<string, unknown>, name: string, allowed: number[]): number {
  const value = payload[name];
  if (typeof value !== "number" || !Number.isInteger(value) || !allowed.includes(value))
    throw new ApiError(400, "VALIDATION_ERROR", `${name} is invalid.`);
  return value;
}

function requiredCount(payload: Record<string, unknown>, name: string): number {
  const value = payload[name];
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0 || value > 100_000)
    throw new ApiError(400, "VALIDATION_ERROR", `${name} is invalid.`);
  return value;
}

const DATE_PAYLOAD_FIELDS = new Set([
  "createdAt", "updatedAt", "saleDate", "documentDate", "stockDate",
  "validFrom", "validTo", "openedAt", "closedAt", "expenseDate", "lastLoginAt",
]);

function validateFlatPayload(payload: Record<string, unknown>): void {
  for (const [name, value] of Object.entries(payload)) {
    if (value != null && typeof value === "object")
      throw new ApiError(400, "VALIDATION_ERROR", `${name} must be a scalar JSON value.`);
    if (typeof value === "number" && !Number.isFinite(value))
      throw new ApiError(400, "VALIDATION_ERROR", `${name} is invalid.`);
    if (DATE_PAYLOAD_FIELDS.has(name) && value != null &&
        (typeof value !== "string" || value.length > 40 || !value.includes("T") ||
         !Number.isFinite(Date.parse(value))))
      throw new ApiError(400, "VALIDATION_ERROR", `${name} must be an ISO date and time.`);
  }
}

function nearlyEqual(left: number, right: number): boolean {
  return Math.abs(left - right) <= 0.0001;
}

function invalidFinancial(message: string): ApiError {
  return new ApiError(400, "VALIDATION_ERROR", message);
}
