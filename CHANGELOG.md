# Changelog

## 1.2.0 — Aronium-style checkout workflow

- Added **F10 Payment** for the advanced payment window and **F12 Cash** for an immediate exact-cash sale.
- Focused the received-amount field automatically so a cashier can type an amount and press Enter without selecting the field first.
- Added a touch number pad, exact/rounded cash suggestions, live remaining/received/change totals, and cash-only overpayment validation.
- Added partial and split tender entry across cash, card, mobile wallet, and bank transfer, with removable entries before completion.
- Separated payment applied to the sale from gross cash received, keeping register and payment reports net while preserving correct customer change.
- Corrected both receipt printer paths to show cash tendered and change for single and split payments.
- Added received, change, and applied-payment details to the sale detail window.
- Kept the checkout and all payment processing entirely offline.

## 1.1.1 — POS interface and appearance fixes

- Removed the broken search-icon button; live product filtering now needs no extra button and Escape clears the query.
- Prevented stale delayed searches from replacing newer results.
- Replaced the stretching four-column product grid with fixed-height wrapping tiles.
- Fixed English/Bengali and Light/Dark radio handlers to apply the button actually selected and persist immediately.
- Completed localization of the visible backup/settings panel and customer page labels.
- Added explicit theme-aware foregrounds for headings and ordinary text.
- Removed loyalty points and store credit from active customer and sale workflows while preserving database compatibility.
- Replaced the missing-font logout square with a vector logout control.
- Kept the update fully offline with no network, cloud, or telemetry dependency.

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
