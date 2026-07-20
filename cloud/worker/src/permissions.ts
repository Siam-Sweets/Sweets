import { ApiError } from "./errors";
import type { AccessClaims, Role } from "./types";

export const ROLE_PERMISSIONS: Readonly<Record<Role, readonly string[]>> = {
  cashier: ["sales.create", "sales.read", "customers.read", "products.read", "register.use", "sync.use"],
  manager: [
    "sales.create", "sales.read", "sales.refund", "sales.void", "customers.read", "customers.manage",
    "suppliers.read", "suppliers.manage", "products.read", "products.manage", "inventory.manage",
    "purchases.manage", "discounts.manage", "reports.read", "register.use", "register.manage", "sync.use",
  ],
  admin: ["*"],
};

export const KNOWN_PERMISSIONS = new Set<string>([
  ...ROLE_PERMISSIONS.cashier,
  ...ROLE_PERMISSIONS.manager,
  "users.manage", "settings.manage", "stores.manage", "sessions.revoke",
]);

export const ENTITY_WRITE_PERMISSION: Readonly<Record<string, string>> = {
  categories: "products.manage",
  product_units: "products.manage",
  products: "products.manage",
  customers: "customers.manage",
  suppliers: "suppliers.manage",
  users: "users.manage",
  discounts: "discounts.manage",
  promotions: "discounts.manage",
  taxes: "settings.manage",
  settings: "settings.manage",
  sales: "sales.create",
  sale_items: "sales.create",
  payments: "sales.create",
  refunds: "sales.refund",
  voided_sales: "sales.void",
  open_sales: "sales.create",
  purchases: "purchases.manage",
  purchase_items: "purchases.manage",
  inventory_adjustments: "inventory.manage",
  inventory_movements: "inventory.manage",
  expenses: "register.manage",
  register_sessions: "register.use",
  cash_movements: "register.use",
};

export function effectivePermissions(role: Role, custom: string[] = []): string[] {
  const validatedCustom = custom.filter((permission) => KNOWN_PERMISSIONS.has(permission));
  return Array.from(new Set([...ROLE_PERMISSIONS[role], ...validatedCustom])).sort();
}

export function validateCustomPermissions(value: unknown): string[] {
  if (value == null) return [];
  if (!Array.isArray(value))
    throw new ApiError(400, "VALIDATION_ERROR", "permissions must be an array.");
  const permissions = value.map((item) => {
    if (typeof item !== "string" || item.length > 80 || !KNOWN_PERMISSIONS.has(item))
      throw new ApiError(400, "VALIDATION_ERROR", "A requested permission is not supported.");
    return item;
  });
  return Array.from(new Set(permissions)).sort();
}

export function hasPermission(claims: AccessClaims, permission: string): boolean {
  return claims.role === "admin" || claims.permissions.includes("*") || claims.permissions.includes(permission);
}

export function requirePermission(claims: AccessClaims, permission: string): void {
  if (!hasPermission(claims, permission)) {
    throw new ApiError(403, "PERMISSION_DENIED", "Your account does not have permission for this action.");
  }
}

export function requireEntityWrite(claims: AccessClaims, entityType: string): void {
  const permission = ENTITY_WRITE_PERMISSION[entityType];
  if (!permission) throw new ApiError(400, "UNSUPPORTED_ENTITY", "This record type cannot be synchronized.");
  requirePermission(claims, permission);
}
