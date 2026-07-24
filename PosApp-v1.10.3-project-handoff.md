# PosApp Project Handoff Summary (v1.10.3)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.3-checkout-ledger-fix.zip`
- **Continue versioning from:** **v1.10.3**

## v1.10.3 Changes

- The **Require an open register before selling** setting is now authoritative. When disabled, cash and non-cash checkout may complete without an open register.
- Checkout and item-refund stock ledger rows are inserted once with final sale/item foreign keys. They are no longer inserted and then edited, so append-only validation does not reject normal stock sales.
- Checkout remains transactional: if sale, stock, payment, promotion, or ledger persistence fails, the entire operation rolls back.
- No SQLite or Turso migration is required.
- No image upload, storage, or synchronization was added.

## Validation Boundary

- Source-level regression checks, XML/XAML/project parsing, Worker smoke tests, version consistency, and ZIP integrity passed.
- Windows compilation and live checkout require GitHub Actions and a Windows test installation.
