-- Query, tenant-isolation, and validated JSON-identifier indexes. Only fields
-- used for integrity or a concrete relationship query are indexed.
CREATE INDEX IF NOT EXISTS ix_stores_tenant_active ON stores (tenant_id, is_active, name);
CREATE INDEX IF NOT EXISTS ix_users_tenant_active_role ON users (tenant_id, is_active, role);
CREATE INDEX IF NOT EXISTS ix_user_store_access ON user_store_assignments (tenant_id, user_id, is_active, store_id);
CREATE INDEX IF NOT EXISTS ix_devices_tenant_status ON registered_devices (tenant_id, status);
CREATE INDEX IF NOT EXISTS ix_sessions_tenant_user ON login_sessions (tenant_id, user_id, revoked_at_utc);
CREATE INDEX IF NOT EXISTS ix_sessions_device ON login_sessions (tenant_id, device_id, revoked_at_utc);
CREATE INDEX IF NOT EXISTS ix_refresh_session ON refresh_tokens (session_id, revoked_at_utc, expires_at_utc);
CREATE INDEX IF NOT EXISTS ix_refresh_family ON refresh_tokens (family_id, revoked_at_utc);
CREATE INDEX IF NOT EXISTS ix_audit_tenant_time ON audit_logs (tenant_id, timestamp_utc DESC);
CREATE INDEX IF NOT EXISTS ix_audit_affected ON audit_logs (tenant_id, affected_type, affected_id);
CREATE INDEX IF NOT EXISTS ix_sync_changes_pull ON sync_changes (tenant_id, cursor, store_id);
CREATE INDEX IF NOT EXISTS ix_sync_changes_record ON sync_changes (tenant_id, entity_type, record_id, version);
CREATE INDEX IF NOT EXISTS ix_sync_operations_device ON sync_operations (tenant_id, device_id, applied_at_utc);

CREATE INDEX IF NOT EXISTS ix_categories_scope ON categories (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_product_units_scope ON product_units (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_products_scope ON products (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_customers_scope ON customers (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_suppliers_scope ON suppliers (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_user_sync_scope ON user_sync_records (tenant_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_discounts_scope ON discounts (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_promotions_scope ON promotions (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_taxes_scope ON taxes (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_settings_scope ON application_settings (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_sales_scope ON sales (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_sale_items_scope ON sale_items (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_payments_scope ON payments (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_refunds_scope ON refunds (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_voided_sales_scope ON voided_sales (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_open_sales_scope ON open_sales (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_purchases_scope ON purchases (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_purchase_items_scope ON purchase_items (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_inventory_adjustments_scope ON inventory_adjustments (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_inventory_movements_scope ON inventory_movements (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_expenses_scope ON expenses (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_register_sessions_scope ON register_sessions (tenant_id, store_id, deleted_at_utc, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_cash_movements_scope ON cash_movements (tenant_id, store_id, deleted_at_utc, updated_at_utc);

-- Immutable business identifiers and ledger sources remain unique even when a
-- buggy or hostile client retries with fresh operation UUIDs.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_receipt
ON sales (tenant_id, store_id, json_extract(payload_json, '$.receiptNumber'))
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.receiptNumber') IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_purchases_document_number
ON purchases (tenant_id, store_id, json_extract(payload_json, '$.documentNumber'))
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.documentNumber') IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_register_sessions_one_open
ON register_sessions (tenant_id, store_id)
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.closedAt') IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_inventory_sale_item_source
ON inventory_movements (
    tenant_id,
    store_id,
    json_extract(payload_json, '$.saleItemRecordId'),
    json_extract(payload_json, '$.type')
)
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.saleItemRecordId') IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_inventory_purchase_item_source
ON inventory_movements (tenant_id, store_id, json_extract(payload_json, '$.purchaseItemRecordId'))
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.purchaseItemRecordId') IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_payments_sale_record
ON payments (tenant_id, store_id, json_extract(payload_json, '$.saleRecordId'))
WHERE deleted_at_utc IS NULL;

-- Editable master-data identifiers are unique inside one branch but may be
-- reused by another branch in the same organization. Normalizing case and
-- surrounding whitespace makes the server rule match the desktop checks.
CREATE UNIQUE INDEX IF NOT EXISTS ux_categories_name
ON categories (tenant_id, store_id, lower(trim(json_extract(payload_json, '$.name'))))
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.name') IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_products_sku
ON products (tenant_id, store_id, lower(trim(json_extract(payload_json, '$.sku'))))
WHERE deleted_at_utc IS NULL
  AND json_extract(payload_json, '$.sku') IS NOT NULL
  AND trim(json_extract(payload_json, '$.sku')) <> '';

CREATE UNIQUE INDEX IF NOT EXISTS ux_products_barcode
ON products (tenant_id, store_id, lower(trim(json_extract(payload_json, '$.barcode'))))
WHERE deleted_at_utc IS NULL
  AND json_extract(payload_json, '$.barcode') IS NOT NULL
  AND trim(json_extract(payload_json, '$.barcode')) <> '';

CREATE UNIQUE INDEX IF NOT EXISTS ux_discounts_code
ON discounts (tenant_id, store_id, lower(trim(json_extract(payload_json, '$.code'))))
WHERE deleted_at_utc IS NULL
  AND json_extract(payload_json, '$.code') IS NOT NULL
  AND trim(json_extract(payload_json, '$.code')) <> '';

CREATE UNIQUE INDEX IF NOT EXISTS ux_promotions_code
ON promotions (tenant_id, store_id, lower(trim(json_extract(payload_json, '$.code'))))
WHERE deleted_at_utc IS NULL
  AND json_extract(payload_json, '$.code') IS NOT NULL
  AND trim(json_extract(payload_json, '$.code')) <> '';

CREATE UNIQUE INDEX IF NOT EXISTS ux_application_settings_key
ON application_settings (tenant_id, store_id, lower(trim(json_extract(payload_json, '$.key'))))
WHERE deleted_at_utc IS NULL AND json_extract(payload_json, '$.key') IS NOT NULL;

-- SKU and barcode share one namespace in PosApp. Separate unique indexes do
-- not catch a SKU equal to another product's barcode, so these triggers close
-- that cross-field gap for inserts and edits as well.
CREATE TRIGGER IF NOT EXISTS tr_products_identifier_namespace_insert
BEFORE INSERT ON products
WHEN NEW.deleted_at_utc IS NULL
BEGIN
  SELECT CASE WHEN EXISTS (
    SELECT 1 FROM products AS candidate
    WHERE candidate.tenant_id = NEW.tenant_id
      AND candidate.store_id = NEW.store_id
      AND candidate.deleted_at_utc IS NULL
      AND (
        (trim(COALESCE(json_extract(NEW.payload_json, '$.sku'), '')) <> '' AND
          lower(trim(COALESCE(json_extract(NEW.payload_json, '$.sku'), ''))) IN (
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.sku'), ''))),
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.barcode'), '')))
          ))
        OR
        (trim(COALESCE(json_extract(NEW.payload_json, '$.barcode'), '')) <> '' AND
          lower(trim(COALESCE(json_extract(NEW.payload_json, '$.barcode'), ''))) IN (
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.sku'), ''))),
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.barcode'), '')))
          ))
      )
  ) THEN RAISE(ABORT, 'unique product identifier namespace') END;
END;

CREATE TRIGGER IF NOT EXISTS tr_products_identifier_namespace_update
BEFORE UPDATE OF tenant_id, store_id, payload_json, deleted_at_utc ON products
WHEN NEW.deleted_at_utc IS NULL
BEGIN
  SELECT CASE WHEN EXISTS (
    SELECT 1 FROM products AS candidate
    WHERE candidate.id <> NEW.id
      AND candidate.tenant_id = NEW.tenant_id
      AND candidate.store_id = NEW.store_id
      AND candidate.deleted_at_utc IS NULL
      AND (
        (trim(COALESCE(json_extract(NEW.payload_json, '$.sku'), '')) <> '' AND
          lower(trim(COALESCE(json_extract(NEW.payload_json, '$.sku'), ''))) IN (
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.sku'), ''))),
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.barcode'), '')))
          ))
        OR
        (trim(COALESCE(json_extract(NEW.payload_json, '$.barcode'), '')) <> '' AND
          lower(trim(COALESCE(json_extract(NEW.payload_json, '$.barcode'), ''))) IN (
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.sku'), ''))),
            lower(trim(COALESCE(json_extract(candidate.payload_json, '$.barcode'), '')))
          ))
      )
  ) THEN RAISE(ABORT, 'unique product identifier namespace') END;
END;

INSERT OR IGNORE INTO schema_migrations (version, name, applied_at_utc)
VALUES (2, 'tenant and synchronization indexes', CURRENT_TIMESTAMP);
