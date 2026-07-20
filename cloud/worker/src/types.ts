export interface Env {
  TURSO_DATABASE_URL: string;
  TURSO_AUTH_TOKEN: string;
  JWT_SIGNING_SECRET: string;
  REFRESH_TOKEN_SECRET: string;
  PASSWORD_PEPPER_SECRET: string;
  DEPLOYMENT_VERSION?: string;
  API_VERSION?: string;
  SCHEMA_VERSION?: string;
  MINIMUM_CLIENT_SCHEMA_VERSION?: string;
  ACCESS_TOKEN_TTL_SECONDS?: string;
  REFRESH_TOKEN_TTL_DAYS?: string;
  MAX_REQUEST_BYTES?: string;
  MAX_SYNC_BATCH?: string;
}

export type Role = "cashier" | "manager" | "admin";

export interface AccessClaims {
  sub: string;
  tid: string;
  sid: string;
  did: string;
  role: Role;
  permissions: string[];
  pv: number;
  iat: number;
  exp: number;
  iss: "posapp-cloud";
  aud: "posapp-desktop";
}

export interface AuthContext {
  claims: AccessClaims;
  requestId: string;
}

export interface DeviceInput {
  id: string;
  name: string;
  operatingSystem?: string;
  machineName?: string;
}

export interface SyncOperation {
  operationId: string;
  idempotencyKey: string;
  entityType: string;
  recordId: string;
  storeId?: string | null;
  operation: "upsert" | "delete";
  baseVersion: number;
  payload: Record<string, unknown>;
  clientTimestampUtc: string;
}

export interface SyncPushBody {
  deviceId: string;
  storeId: string;
  clientSchemaVersion: number;
  operations: SyncOperation[];
}

export interface SyncOperationResult {
  operationId: string;
  recordId: string;
  accepted: boolean;
  duplicate: boolean;
  serverVersion: number;
  errorCode?: string;
  message?: string;
  serverPayload?: Record<string, unknown>;
  serverStoreId?: string | null;
  serverUpdatedAtUtc?: string;
  serverDeletedAtUtc?: string | null;
  serverLastModifiedDeviceId?: string;
}

export interface ServerRecord {
  id: string;
  tenant_id: string;
  store_id: string | null;
  payload_json: string;
  version: number;
  created_at_utc: string;
  updated_at_utc: string;
  deleted_at_utc: string | null;
  created_by_user_id: string;
  updated_by_user_id: string;
  last_modified_device_id: string;
}
