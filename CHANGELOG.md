# Changelog

## 1.2.2 — Application logo and guided Windows installer

- Added a scalable PosApp logo plus multi-resolution Windows icon artwork.
- Applied the logo to the executable, Windows taskbar/title bars, login screen, setup wizard, and main sidebar.
- Added a branded Inno Setup installer with license acceptance, destination-folder selection, Start Menu selection, a ready-to-install summary, and a completion page.
- Added a checked-by-default **Create a desktop shortcut** option to the install wizard.
- Added an optional **Run PosApp** action on the final installer page.
- Added a local PowerShell installer build script and CI packaging for the setup executable while preserving the portable EXE and ZIP.
- Kept the installed application and all installer payloads offline; store data remains under the local Windows profile.

## 1.2.1 — First-run setup and Light-mode sidebar fix

- Added a one-time setup wizard that opens before login until configuration is completed.
- Added local store name, phone, address, currency, receipt footer, language, theme, backup, and administrator credential setup.
- Replaced the seeded administrator PIN with the PIN selected during setup and persisted the completion state in the local SQLite database.
- Preserved existing store and administrator values as setup defaults when upgrading an older installation.
- Fixed unreadable inactive and active sidebar labels in Light mode by binding every navigation label and icon to its parent button foreground.
- Removed fixed demo credentials from the login screen because administrator credentials are now selected during setup.
- Kept setup and all saved configuration entirely offline.

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
