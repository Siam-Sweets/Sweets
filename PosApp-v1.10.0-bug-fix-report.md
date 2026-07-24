# PosApp v1.10.0 — 42-Item Bug-Fix Report

All items below were addressed in source. “Validated” means static checks, SQLite simulations, or Worker smoke tests passed in this environment. A Windows/.NET build and live two-device test remain required.

| # | Audited defect | Resolution |
|---:|---|---|
| 1 | EF state remained accepted after failed transaction | Two-phase save uses `acceptAllChanges=false`; rollback clears tracking; external commit/rollback helpers finalize state. |
| 2 | Concurrent stock changes overwrote each other | `Product.StockVersion` is an EF concurrency token; stock-changing services use transactions and conflict handling. |
| 3 | Financial/stock operation could run twice | Durable `OperationId`/`OperationKey`, unique indexes, retry-safe service commands, and atomic cloud operation groups. |
| 4 | Restore integer-ID collisions and wrong relationships | Restore creates local keys and resolves all foreign references from permanent sync IDs. |
| 5 | One invalid pull row blocked synchronization forever | Dependency retry plus conflict/quarantine retention advances the cursor without silently applying invalid data. |
| 6 | Inventory ledger was editable/deletable | EF validation, SQLite append-only triggers, restricted product deletion, and immutable cloud handling. |
| 7 | Cloud push batch was partly committed | Worker preflights the complete operation and commits all changes in one Turso transaction. |
| 8 | Snapshot export could mix points in time | Every store snapshot is captured under one SQLite read transaction. |
| 9 | Multi-store restore mixed snapshot timestamps | Backup-set ID and common capture time are required and validated before restore. |
| 10 | Snapshot hash/compatibility ignored | Exact payload SHA-256, row count, schema, application major version, IDs, and capture metadata are verified. |
| 11 | Conflicting snapshot schema versions | Request and payload use schema 5 and must match. |
| 12 | Concurrent snapshot version race | Version allocation and insert occur under `BEGIN IMMEDIATE`. |
| 13 | Automatic Worker init did not upgrade old tables | PRAGMA-based column checks add missing v1.10.0 columns and indexes idempotently. |
| 14 | Conflict resolution used stale remote revision | Resolution fetches the current remote record and resolves only the selected matching conflict. |
| 15 | Revoked devices could use protected routes | Every protected endpoint validates an active device. |
| 16 | Disconnect was local-only; login revived revoked device | Logout revokes the server refresh token; local credentials are cleared only after server confirmation; revoked devices stay revoked. |
| 17 | Cloud operational tables grew forever | Opportunistic retention removes expired/revoked tokens, old rate limits, idempotency rows, and safely consumed changes. |
| 18 | Transfer matching ignored shared SKU/barcode namespace | Matching and database triggers compare both identifiers as one per-store namespace. |
| 19 | Destination product compatibility unchecked | Transfer receipt validates stock tracking, unit, identity, and active-state compatibility. |
| 20 | Transfer/ledger relationships lacked protection | Fresh schemas have FKs; upgraded databases receive equivalent insert/update integrity triggers. |
| 21 | Open-register requirement existed only in UI | `SaleService` enforces the setting and validates the active cash session. |
| 22 | Full refund mishandled split payments | Refunds allocate across original payment methods and support explicit split refund tenders. |
| 23 | Promotion use limit was race-prone | `Discount.UsageVersion` concurrency token and transactional increment/rollback. |
| 24 | New store copied every user/PIN | Only one administrator is seeded for a new store; other assignments are explicit. |
| 25 | Store row and settings diverged | Store settings and `Stores` metadata update atomically in both directions. |
| 26 | Cross-store authorization relied on UI | Report and transfer services enforce current role/store scope. |
| 27 | Sync signal fired before outer transaction commit | External transaction helper raises the outbox event only after commit. |
| 28 | Historical category reports changed after recategorization | `SaleItem.CategoryName` snapshots category at sale time and legacy rows are backfilled. |
| 29 | All-store top products merged unrelated products | Grouping includes store and permanent product identity. |
| 30 | Catalog-only import created stock | New catalog-only rows never create opening inventory or stock ledger entries. |
| 31 | Count/purchase imports mutated catalog | Existing catalog fields remain unchanged in inventory-count and purchase modes. |
| 32 | Late CSV collision discarded valid work without diagnosis | Whole-file preflight detects duplicate/cross-field identifiers with row-specific errors before transaction start. |
| 33 | Invalid settings JSON silently reset | Corrupt settings now raise a visible error instead of being overwritten with defaults. |
| 34 | Concurrent sign-up returned 500 | Worker maps owner-email uniqueness races to HTTP 409. |
| 35 | Malformed JWT could return 500 | Signature/claims parsing is guarded and returns HTTP 401. |
| 36 | Device last-seen became stale | Every protected request refreshes `last_seen_at`. |
| 37 | Corrupt credential file enabled cloud capture | Cloud enablement requires a successfully decrypted and validated credential. |
| 38 | Store selection memory/disk could diverge | Selection is persisted atomically before the in-memory active store changes. |
| 39 | Snapshot row count was trusted | Worker and desktop independently recount payload rows. |
| 40 | Managers could select other-store transfer inventory | UI and service scope managers to the active store; only admins receive cross-store access. |
| 41 | Unknown protected URL returned authentication error | Routing returns 404 before token validation for unknown endpoints. |
| 42 | Failed push marked all rows failed after partial commit | Server operation pushes are atomic and client status follows the `committed` result. |

## Validation boundary

Worker syntax/smoke tests, SQLite schema/migration/trigger simulations, source structure, resources, XAML/XML/JSON/YAML parsing, and package integrity were checked. Windows compilation, installer execution, live Cloudflare/Turso deployment, and true simultaneous device testing were not available here.
