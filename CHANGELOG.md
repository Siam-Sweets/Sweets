# Changelog

## 1.1.0 — Offline operations pack

- Added supplier records and supplier-linked posted purchase documents.
- Added multi-line stock receiving with tax totals and moving-average cost updates.
- Added cash-register sessions with opening float, cash in/out reasons, live expected cash, counted cash, variance, printable X reports, and manager-only Z close.
- Added physical inventory counting with atomic stock adjustments and ledger entries.
- Added CSV product export and three guarded import modes: catalog update, inventory count, and stock receipt.
- Added consistent local SQLite backup, automatic startup/exit backup, retention control, manual backup, validated staged restore, and pre-restore safety copy.
- Added idempotent in-place schema upgrades so existing products, sales, users, customers, and settings are retained.
- Added Purchases and Register navigation with role-based access.
- Updated user deletion protection to retain users referenced by purchase/register history.
- Kept all new functionality offline; no network client, cloud service, telemetry, or hosted API was added.
- Replaced oversized exception dialogs with concise messages while preserving full technical details in `posapp.log`.
