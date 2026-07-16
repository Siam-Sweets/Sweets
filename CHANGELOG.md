# Changelog

## 1.3.5 — Rolling release record lookup correction

- Fixed the repeated "a release with the same tag name already exists" failure when `gh release view dev-build` cannot resolve an existing rolling release after its tag moves.
- Resolves releases from the paginated REST collection, updates them by numeric release ID, and verifies the published tag and all three assets.
- Replaces matching release assets through their REST asset IDs and the GitHub upload endpoint, making partial upload failures safe to retry.
- Cancels an older in-progress main-branch build when a newer commit arrives, preventing concurrent rolling-release asset replacement and stale tag rollback.
- Retains generated notes for new stable releases and preserves existing notes on stable-release reruns.

## 1.3.4 — Resilient GitHub release publishing

- Replaced the third-party Node-based GitHub Release action with the GitHub CLI already provided by GitHub-hosted runners.
- Upgraded the official checkout, .NET setup, artifact upload, and artifact download actions to their current Node 24-compatible major versions.
- Added bounded retries for transient GitHub API, tag-push, and asset-upload failures such as the GitHub "Unicorn" service response.
- Made rolling `dev-build` publishing idempotent: the tag advances to the build commit, release metadata is updated, and matching installer, EXE, and ZIP assets are safely replaced on reruns.
- Kept versioned tag and manual releases rerunnable while preserving generated release notes for new stable releases.

## 1.3.3 — WPF safe-update build correction

- Fixed Release compilation of the Update &amp; Recovery page by explicitly importing `System.IO` for `Path` and `Directory` in the WPF XAML temporary project.
- Kept the v1.3.2 safe-update behavior and data-preservation workflow unchanged.

## 1.3.2 — Safe offline updates and automatic upgrade recovery

- Added **Settings → Update & recovery** for selecting a newer local PosApp setup installer without adding any online update dependency.
- Validates the setup filename, Windows executable header, PosApp product metadata, and newer version; records a SHA-256 digest and prevents a changed package from launching.
- Creates and health-checks a complete SQLite snapshot before the installer starts, then retains the recovery backup after a successful update.
- Detects direct installer and portable-EXE version changes and creates a verified pre-migration backup before EF Core can modify the database schema.
- Records pending, completed, cancelled/not-applied, and failed update status with installer logs and recovery paths.
- Makes the installer explicitly reuse the existing application folder, Start Menu group, shortcut choices, and language while keeping `%LOCALAPPDATA%\PosApp` outside uninstall/update scope.

## 1.3.1 — Receipt totals binding fix

- Fixed the POS startup crash caused by WPF attempting to write into the calculated, read-only `DiscountTotal` property.
- Made all calculated receipt totals and other read-only receipt display bindings explicitly one-way to prevent equivalent write-back failures.

## 1.3.0 — Receipt-first register and management workspace

- Rebuilt the POS as a full-screen, receipt-first register with a compact command rail and stable product-search overlay.
- Added complete keyboard flow for F2 discount, F3 search, F4 quantity, F7 open sales, F8 new sale, F9 save sale, F10 payment, F12 cash, and Delete.
- Added exact card/check tenders, customer selection, service type, sale comments, weighing, cash drawer, refund navigation, lock/sign-out, and confirmed order voiding.
- Added reusable offline promotions with optional codes, date ranges, use limits, management CRUD, and direct selection from the register discount dialog.
- Added a management slide-over and role-aware back-office shell with a new live dashboard for monthly/daily sales, profit, transactions, top products, hourly sales, and payment breakdown.
- Reworked Settings into General, Order & Payment, Products, Documents, Weighing Scale, Customer Display, Email boundary, Print, Database, and About sections.
- Added register-required checkout and configurable default service type, void confirmation, startup register prompt, search-grid preferences, receipt width, and local UI preferences.
- Expanded English/Bengali localization for the new register, management, dashboard, promotions, and settings workflow.
- Preserved the local-only boundary: no runtime network client, telemetry, cloud sync, or remote dependency was added.

## 1.2.4 — Development installer version fix

- Fixed Inno Setup compilation for development builds whose display version contains a suffix such as `0.0.0-dev.9`.
- Added a separate four-part numeric Windows resource version (`0.0.0.9` for that example) while preserving the readable development version in filenames and installer text.
- Applied the same numeric-version conversion to the local Windows installer build script.

## 1.2.3 — CI build correction and nullable cleanup

- Fixed WPF setup-wizard compilation by changing the PIN text-input event handler from static to instance-bound.
- Removed all six warnings reported by the Release build: safe opening-stock reuse, explicit required EF navigation handling, guarded serial-scale access, and an unused scanner field.
- Kept the v1.2.2 logo and guided offline installer unchanged.

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
