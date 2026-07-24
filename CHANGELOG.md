# Changelog

## 1.10.6 — Store dialog scrolling fix

- Replaced the fixed Store Details form area with a vertical `ScrollViewer` so every field remains reachable under Windows display scaling and on smaller screens.
- Made the Store Details window resizable with practical minimum dimensions.
- Kept the Save and Cancel buttons fixed while only the form body scrolls.
- Added an internal scrollbar to the multiline address field.
- No database schema, cloud protocol, localization, or image-handling changes are required.

## 1.10.5 — Dark mode surface cleanup

- Fixed dark mode windows and dialogs still showing light client-area surfaces in several management flows.
- Added a global window-theme helper that reapplies PosApp theme brushes to every WPF window, including programmatically created dialogs.
- Added best-effort immersive dark title bars for Windows 10/11 so the main window and modal dialogs no longer show bright caption bars in dark mode.
- Applied theme refresh to already-open windows when the user changes appearance settings.
- No database schema, synchronization protocol, or cloud API changes are required.

## 1.10.4 — Category scrolling and color visibility fix

- Added explicit scrolling to Category Management and a resizable, vertically scrollable category editor.
- Added a live `#RRGGBB` color preview and color swatches in category management.
- Displayed category colors in POS category filters and product-card accents.
- Added English/Bengali localization for the color preview label.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions to 1.10.4.
- No database or cloud schema migration is required.

## 1.10.3 — Checkout register-setting and immutable-ledger fix

- Fixed cash checkout requiring an open register even when **Require an open register before selling** was disabled.
- Made the stored register setting authoritative for checkout; an available open session is still attached when one exists.
- Fixed stock-tracked checkout failing because newly inserted ledger rows were edited afterward to attach sale/item IDs.
- Fixed the same append-only ledger issue in item-level refunds.
- Sale/refund rows and line items are now saved first inside the existing transaction, then ledger rows are inserted once with final foreign keys.
- No SQLite schema, Turso schema, synchronization protocol, cloud endpoint, localization, or image-handling changes.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions to 1.10.3.

## 1.10.2 — Cloudflare PBKDF2 runtime compatibility fix

- Fixed owner-account signup failing with `NotSupportedError` because Cloudflare Workers rejects PBKDF2 iteration counts above 100,000.
- Changed cloud-account password hashing from 120,000 to 100,000 PBKDF2-HMAC-SHA256 iterations while retaining a unique random salt per owner.
- Added an explicit Worker-side iteration guard so unsupported stored parameters return a controlled service error instead of an unhandled runtime exception.
- Added a Worker smoke-test assertion that signup persists exactly 100,000 iterations.
- Local PosApp user PIN hashing remains unchanged at 120,000 iterations.
- No SQLite, Turso schema, synchronization protocol, desktop UI, or image-handling changes.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions to 1.10.2.

## 1.10.1 — Read-only DataGrid checkbox binding fix

- Fixed the Stores page throwing dispatcher exceptions when displaying the read-only `IsActive` and `IsCurrent` checkbox columns.
- Explicitly changed display-only checkbox bindings to `Mode=OneWay` and marked those columns read-only.
- Applied the same preventive correction to Sync Center device status and cross-store low-stock checkbox columns.
- No database schema, cloud synchronization protocol, Worker endpoint, or image-handling changes.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions to 1.10.1.

## 1.10.0 — Comprehensive integrity, synchronization, and authorization fixes

- Reworked EF Core save/outbox transactions so rollbacks clear invalid tracked state and cloud notifications occur only after commit.
- Added optimistic stock and promotion concurrency, durable operation IDs, deterministic stock-ledger keys, and idempotent sales, refunds, voids, purchases, counts, adjustments, and transfers.
- Enforced append-only stock history and added missing transfer, user, sale, product, and ledger relationship guards for existing SQLite databases.
- Added cumulative partial-refund quantities, split-tender refund allocation, service-level open-register enforcement, and promotion usage rollback safety.
- Made Worker operation pushes atomic, added current-record conflict checks, malformed-token handling, active-device enforcement, server-side logout, last-seen updates, and safe retention cleanup.
- Made multi-store snapshots one coherent backup set with transactional capture, exact payload hash/row/schema/version validation, sync-ID relationship rebuilding, and additive Worker schema upgrades.
- Added retry ordering and quarantine for invalid downloaded records so one corrupt remote row cannot block future synchronization.
- Hardened store authorization, manager transfer visibility, store/settings consistency, user seeding, historical category reports, and all-store product grouping.
- Hardened CSV preflight validation and separated catalog-only, inventory-count, and purchase-import behavior.
- Continued excluding image paths and image files from cloud payloads.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions to 1.10.0.

## 1.9.8 — Fresh-install default-store startup fix

- Fixed fresh installations failing at startup with `No active store is available.`
- The schema upgrader now supplies every required sync column when creating the initial `MAIN` store instead of silently ignoring the insert.
- Added a defensive startup repair that creates `MAIN` when no stores exist and reactivates the oldest store if all stores are inactive.
- Startup repair suppresses cloud outbox capture until the local store context is valid.
- No Turso schema, synchronization protocol, cloud Worker endpoint, desktop layout, or image-handling changes.

## 1.9.7 — Linux release version normalization

- Fixed the GitHub Release job rejecting uppercase version prefixes such as `V1.9.7`.
- Normalizes one leading `v` or `V` in both Windows build and Linux release jobs.
- Passes the manual release version through an environment variable before Bash validation.
- Preserves lowercase release tags such as `v1.9.7` regardless of whether the input/tag used `v` or `V`.
- No database schema, synchronization protocol, cloud Worker behavior, desktop UI, or image-handling changes.

## 1.9.6 — Case-insensitive build version normalization

- Fixed manual GitHub Actions builds rejecting uppercase version prefixes such as `V1.9.6`.
- Normalizes one leading `v` or `V` before validating the semantic version.
- Added support for both lowercase `v*` and uppercase `V*` release-tag triggers.
- Passes the workflow-dispatch version through an environment variable instead of embedding it directly in PowerShell.
- No database schema, synchronization protocol, cloud deployment, desktop UI, or image-handling changes.

## 1.9.5 — Cloudflare Node 24 and atomic secret deployment fix

- Replaced `cloudflare/wrangler-action@v3` in the cloud deployment workflow with explicit Node.js 24 setup and Wrangler 4.81.0.
- Deploys Worker code and `POSAPP_CLOUD_CONFIG` together through Wrangler's `--secrets-file` option instead of the action's separate secret-upload phase.
- Added a Cloudflare authentication preflight and secure temporary-secret-file cleanup.
- Accepts Turso database creation HTTP 200, 201, or existing-database HTTP 409 responses.
- No database schema, synchronization protocol, desktop UI, or image-handling changes.

## 1.9.4 — Optional cloud build configuration

- Made the `POSAPP_CLOUD_API_URL` GitHub Actions repository variable optional.
- Builds without the variable now complete normally and operate as local-only PosApp installations.
- Builds with the variable still validate HTTPS format and embed the normalized endpoint.
- Updated English/Bengali cloud guidance and phone-only deployment documentation.
- No database, synchronization protocol, Worker schema, UI field, or image-handling changes.

## 1.9.3 — Automatic cloud endpoint and device registration

- Removed the editable Worker URL field from the Cloud settings screen.
- Added the `POSAPP_CLOUD_API_URL` GitHub Actions repository variable and embedded it into Windows builds.
- Added build validation so releases cannot be produced with a missing or invalid cloud endpoint.
- Removed the device-name field and now register `Environment.MachineName` automatically.
- Redirected credentials saved by v1.9.2 to the endpoint embedded in the current build.
- Updated English/Bengali guidance and phone-only deployment documentation.
- Preserved local SQLite, multi-store sync, conflict handling, stock transfers, and image exclusion.

## 1.9.2 — Mobile cloud deployment and variable-based setup

- Added a phone-friendly GitHub Actions workflow that can provision/reuse a Turso database and deploy the Cloudflare Worker without a local PC.
- Added a single encrypted `POSAPP_CLOUD_CONFIG` JSON secret option while preserving the four existing Worker variable/secret names.
- Added optional automatic idempotent Turso schema initialization through `AUTO_INITIALIZE_SCHEMA=true`.
- Added a committed Wrangler configuration, mobile deployment guide, and dashboard-variable examples.
- Kept local SQLite, cloud sync payloads, and image exclusion behavior unchanged.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions.

## 1.9.1 — NuGet dependency restore fix

- Resolved `NU1605` during `dotnet restore` by aligning `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.DependencyInjection.Abstractions` to 8.0.2.
- Kept Entity Framework Core SQLite at 8.0.11 and made no database-schema or cloud-protocol changes.
- Bumped application, assembly, file, informational, installer, Worker, README, and changelog versions.

## 1.9.0 — Multi-store operations and stock transfers

- Added an administrator **All Stores** scope to dashboard and report KPIs, daily trends, products, categories, payments, and printable summaries.
- Added per-store performance rows and an all-store inventory view with store, product, quantity, unit, and low-stock status.
- Added auditable stock transfers with Draft, Dispatched, Received, and Cancelled states.
- Dispatch and receipt create linked append-only inventory-ledger entries; cancelling a dispatched transfer creates compensating source-store entries.
- Automatically creates missing destination catalog/category records while explicitly leaving `ImagePath` null and transferring no image files.
- Added transfer and transfer-item cloud payloads, dependency resolution, snapshots, restore ordering, conflict protection, Worker entity validation, and sync priorities.
- Added collision-resistant transfer numbers for independently operating offline devices and atomic pull application with destination store/category/product placeholders when related cloud changes arrive out of order.
- Added additive local tables/indexes and transfer links on stock transactions; no Turso schema migration is required from v1.7.0 or v1.8.0.
- Preserved role isolation: administrators can consolidate stores; managers remain scoped to the active store.
- Added English/Bengali resources and bumped application, assembly, file, informational, installer, Worker, README, and changelog versions.

## 1.8.0 — Conflict center and multi-device hardening

- Added a dedicated **Synchronization Center** with unresolved conflicts, per-store status, failed queue items, registered devices, and the latest 100 sync runs.
- Added explicit **Keep Local**, **Use Cloud**, and field-level merge resolution for editable records.
- Prevented field-level merging of sales, payments, stock transactions, purchases, and register ledger records; those require choosing one complete version.
- Added deterministic conflict re-queueing against the latest cloud revision, related-conflict closure, resolved-conflict cleanup, and manual retry for non-conflict failures.
- Added local sync-run history with success/failure diagnostics and automatic retention.
- Added the authenticated Worker device-list endpoint and multi-device smoke coverage for sequential edits, cursor pulls, idempotency, and revision conflicts.
- Added additive v1.7.0-to-v1.8.0 local database upgrades for conflict operations, resolution metadata, and sync history. No Turso schema migration is required from v1.7.0.
- Continued excluding `ImagePath` and all image files from cloud payloads.
- Added English/Bengali UI resources and bumped application, assembly, file, informational, installer, Worker, README, and changelog versions.

## 1.7.0 — Offline-first incremental synchronization

- Added durable local outbox capture after a cloud account is connected, with per-record sync IDs, local revisions, cloud revisions, retry metadata, and store isolation.
- Added automatic synchronization after startup, network reconnection, local commits, and a periodic one-minute check, without blocking local checkout.
- Added idempotent Worker push, cursor-based pull, per-record atomic transactions, deletion tombstones, and cloud revision conflict detection.
- Added conflict retention in local SQLite so concurrent edits are never silently overwritten; Settings displays pending and conflict counts.
- Added manual **Sync Now** and latest-snapshot restore controls.
- Added fresh-device restore with a pre-restore local backup, relational validation, per-store cursor restoration, and mandatory restart after completion.
- Added Turso v1.7.0 migration/schema changes and Worker smoke coverage for push, duplicate replay, conflict, and pull.
- Continued excluding product/user image paths and all image files from snapshots and incremental payloads.
- Added English/Bengali UI text and bumped application, assembly, file, informational, installer, Worker, README, and changelog versions.
- Full snapshots remain limited to 15 MB per store; conflict resolution UI is deferred to the next milestone.

## 1.6.0 — Cloud account API and initial snapshots

- Added an optional self-hosted Cloudflare Worker API backed by Turso/libSQL.
- Added owner sign-up/sign-in, private registration-key protection, rotating refresh tokens, device registration, and authentication rate limiting.
- Added a Settings → Cloud workflow for connection testing, account creation, sign-in, disconnect, and initial all-store snapshot upload.
- Protects desktop access/refresh tokens with Windows DPAPI for the current Windows user; passwords are never stored by the desktop app.
- Added Turso schema and endpoints for owners, devices, stores, snapshots, and per-device sync cursors.
- Initial snapshots include all scalar POS data while explicitly excluding product/user image paths and all image files.
- Retains offline-first checkout and keeps incremental outbox capture disabled until the v1.7.0 push/pull engine is available.
- Added English and Bengali localization, cloud deployment documentation, Worker syntax validation in GitHub Actions, and version updates.

## 1.5.0 — Multi-store foundation and sync-ready data

- Added admin-only Store Management with create, edit, activate/deactivate, and store switching.
- Isolated products, categories, customers, users, sales, payments, stock, purchases, register sessions, promotions, taxes, and settings by store.
- Migrates all existing v1.4.25 data into a default `MAIN` store without deleting working records.
- Added per-store uniqueness for usernames, product identifiers, categories, receipt numbers, purchase numbers, discounts, and settings.
- Added stable sync IDs, revisions, update timestamps, per-store sync state, and an outbox schema for later cloud synchronization; capture remains disabled until cloud setup exists.
- New stores receive administrator/user access plus starter categories, taxes, discounts, and independent settings.
- Added English and Bengali localization for store management.
- Cloud upload/download remains disabled until the Cloudflare Worker and Turso account/API milestone is deployed and configured.

## 1.4.25 — Product category management

- Added a Categories button to the Products page.
- Added a category manager with New Category, Edit, and Delete actions.
- Added category name, description, and color editing with existing validation rules.
- Added English and Bengali localization for the new category-management UI.
- Product filters refresh immediately after category changes.

## 1.4.24 — Higher-contrast borders and table grid lines

- Increased the global light- and dark-theme border contrast so cards, fields, dropdowns, dialogs, drawers, sidebar panels, and command tiles remain visually separated from their surrounding surfaces.
- Added a dedicated strong-border theme resource for dense data surfaces without making every standard control equally heavy.
- Enabled both horizontal and vertical DataGrid grid lines and applied the stronger brush to table outlines and column headers.
- Preserved primary focus, hover, selection, success, warning, and danger states while improving boundary visibility throughout the app.

## 1.4.23 — Theme-aware date picker calendar

- Replaced the default system-white `CalendarItem` popup template with a complete PosApp theme-aware calendar template.
- Applied dynamic dark/light resources to the calendar surface, border, month/year header, weekday headings, navigation arrows, day cells, and month/year selection cells.
- Added readable hover, pressed, keyboard-focus, today, selected, inactive, blackout, and disabled states in both themes.
- Preserved DatePicker navigation between month, year, and decade views while keeping the popup readable at Windows display scaling levels.

## 1.4.22 — Responsive product-search cards

- Increased the minimum product-card height so names, identifiers, price, unit, and stock remain readable without clipping.
- Moved price and stock onto separate lines with a consistent divider and spacing, preventing the values from overlapping at narrow widths.
- Added two-line product-name handling, single-line ellipsis for long SKU/barcode values, and full-value tooltips.
- Made the product-search panel and card dimensions respond to the actual available WPF viewport instead of fixed 940×650 and 900-pixel assumptions.
- Automatically reduces the number of visible columns when the requested grid density would make cards unreadable at the current window size or Windows display scaling.

## 1.4.21 — Controlled management scrolling and dashboard date filter

- Fixed Products, Inventory, Purchases & Suppliers, Cash Register, Customers & Suppliers, and Dashboard wheel gestures jumping directly to the end of long grids.
- Updated nested scrolling so DataGrid surfaces move by configured rows while page ScrollViewers retain bounded pixel scrolling.
- Added wheel-delta accumulation and large-delta clamping for high-resolution mice and touchpads.
- Added an inclusive From/To date filter to Management Dashboard and applied it to range KPIs, daily sales, top products, payment breakdown, hourly activity, and printed dashboard output.
- Preserved the separate Today KPI while custom dashboard ranges are active.

## 1.4.20 — Stable selected sidebar hover state

- Fixed every management-sidebar item becoming visually blank when the pointer hovered over the currently selected page.
- Added a dedicated active-button template so the selected primary background and white label remain visible during hover and mouse press states.
- Preserved the existing hover appearance for unselected sidebar items in both light and dark themes.

## 1.4.19 — Printable management pages

- Added page-level Print actions to Reports & Dashboard, Management Dashboard, Cash Register, Purchases & Suppliers, and Sales History.
- Printed reports use the currently selected date range, status filter, supplier search, loaded KPIs, and visible page data.
- Added compact multi-page text formatting that works with the configured Windows printer and remains usable on receipt-width printers.
- Preserved existing individual receipt printing, register X reports, and final Z reports.

## 1.4.18 — Optional sample products during first-run setup

- Added an on/off switch to the first-run setup wizard for the built-in sample product catalog.
- Kept the switch enabled by default to preserve the previous onboarding behavior while allowing a clean, empty product catalog.
- Moved sample-product creation out of unconditional startup seeding and into the atomic setup-completion transaction.
- Preserved default categories, taxes, discounts, users, and store settings regardless of the sample-product choice.
- Added matching English and Bengali setup text.

## 1.4.17 — Centered register command captions

- Removed the redundant parenthesized `(F10)` text from the Payment caption while preserving the separate F10 shortcut label.
- Centered the Delete, Quantity, Discount, Save sale, Payment, and Open sales captions independently within their command tiles.
- Preserved the shortcut labels in the upper-left corner of each affected tile.
- Updated both English and Bengali payment-caption resources.

## 1.4.16 — Visible text-input caret

- Added an explicit theme-aware caret brush to all standard text and password fields.
- Ensured the main receipt barcode/product search field always shows its insertion cursor when focused in both light and dark themes.
- Preserved the existing placeholder behavior: the hint hides on focus while the caret remains visible.

## 1.4.15 — Simplified activation controls

- Removed redundant Restore/Deactivate controls from Products, Customers & Suppliers, and Promotions.
- Kept the Active checkbox as the single control for activating or deactivating products, customers, suppliers, and promotions.
- Reduced the affected Actions columns and removed the now-unused activation-action converter, handlers, service method, and localization entries.

## 1.4.14 — Hardware project build fixes

- Added the missing `System.IO` namespace import required by serial barcode-scanner `IOException` handling.
- Corrected receipt-printer page-position arithmetic to use a floating-point Y coordinate, matching the measured font line height.
- Restored successful compilation of the `PosApp.Hardware` project under the release GitHub Actions build.

## 1.4.13 — Reliability and data-integrity hardening

- Corrected partial-refund allocation when a receipt contains duplicate product lines, including legacy refunds without an original-line link.
- Prevented stale saved-sale identifiers from reusing receipt numbers or creating duplicate suspended orders during checkout.
- Hardened malformed local PIN records, case-insensitive login, scanner lifecycle callbacks, printer-spooler failures, and invalid entity identifiers.
- Made register movements refresh deterministically and strengthened product, customer, supplier, promotion, refund, and numeric input validation.
- Protected exported CSV values from spreadsheet formula execution while preserving safe import/export round trips.
- Kept invalid report ranges and CSV export failures inside their owning screens instead of escalating them to application-level error dialogs.
- Made startup seeding recover missing built-in categories and store settings safely in partially restored local databases.
- Removed redundant refund, export, dialog, and purchase-editor code left behind by earlier iterations.

## 1.4.12 — Codebase cleanup

- Removed the unused generic repository abstraction and implementation; all active services already use the scoped EF Core context directly.
- Removed an unused command helper, obsolete theme/language enums, orphaned SVG source artwork, and empty placeholder directories.
- Removed unused WPF, hosting, logging, behavior, HID-discovery, design-time EF, and dependency-abstraction package references while preserving the packages required by active features.
- Removed redundant project references and stopped copying the embedded application manifest as a loose publish file.
- Replaced a no-op HID scanner branch with the intended inter-key timeout behavior and removed stale startup state that was never read.
- Tightened nullable report/import queries to eliminate known compiler-warning sources without changing database or report behavior.

## 1.4.11 — Complete measured-price prompts

- Prevents weight, volume, and length prompts from clipping the numeric unit price at common Windows display scaling levels.
- Shows a compact, unambiguous unit-price label such as `৳ 80.00 / kg` in both add and quantity-adjustment dialogs.
- Allows measured-quantity prompts to wrap and grow vertically for long product names in English and Bengali.

## 1.4.10 — Product search binding stability
- Displays computed product units and sale modes using explicit one-way bindings, preventing the product finder from trying to write into read-only properties.
- Prevents a single UI failure from recursively opening a stack of identical error dialogs.

## 1.4.9 — Reliable status checkboxes
- Makes the Active checkboxes in Customers & Suppliers update customer and supplier status immediately and persist it locally.
- Makes promotion Active checkboxes reliably activate or deactivate discount rules and refresh their row state.
- Uses explicit click handling so read-only management grids no longer swallow checkbox changes.

## 1.4.8 — Visible product names

- Keeps the product-name column at a readable width instead of allowing it to collapse after the sale-mode and unit columns are added.
- Freezes the product-name column while the remaining catalog columns scroll horizontally.

## 1.4.7 — Weight and volume pricing

- Added explicit per-item, weight, volume, and length sale modes to the product editor.
- Added compatible pricing units including kg, g, L, mL, m, piece, and pack, with price/cost/stock labels that follow the selected unit.
- Prompted the cashier for the exact measured amount when a weight-, volume-, or length-based product is added, and allowed F4 to adjust it later.
- Calculated line totals automatically as measured amount multiplied by the stored price per selected unit.
- Stored the unit on every completed sale line so receipts, history, refunds, and reprints retain the original measurement.
- Extended product CSV import/export with a backward-compatible SaleMode column and measurement validation.
- Added English and Bengali text for measurement modes, units, prompts, and validation.
- Added an upgrade-safe sale-item unit snapshot migration without changing or deleting existing business data.

## 1.4.6 — Reliable product and custom-refund checkboxes

- Made custom-refund item selection respond on the first click instead of being consumed by DataGrid cell selection.
- Restored editing for custom-refund quantities while keeping receipt facts read-only.
- Replaced the product Weighted and Active cells with direct single-click controls.
- Added a focused product-status persistence operation so Active changes save without rewriting unrelated product fields.
- Restored the previous visual value automatically if any product checkbox update fails.

## 1.4.5 — Custom refunds, quieter checkout, and reliable scrolling

- Removed the Lock command from the receipt-screen home actions.
- Added item- and quantity-based custom refunds with repeatable partial returns, payment-method selection, stock restoration, and over-refund protection.
- Changed checkout completion to save the sale without opening a printer/file dialog; receipts remain printable from Sales History.
- Replaced the one-refund-per-sale database constraint with line-level refund links and an upgrade-safe non-unique lookup index.
- Preserved legacy refund repair while preventing it from rewriting modern partial-refund tender or rounding values on later starts.
- Fixed mouse-wheel routing across nested management pages, grids, combo boxes, and containing scroll viewers.
- Updated English and Bengali labels for the new refund and print-from-history workflow.

## 1.4.4 — Data-integrity, recovery, update, and localization hardening

- Bound every sale and refund to its owning register session so later activity cannot rewrite a previously closed Z report.
- Cleared stale EF tracking before stock-changing sale operations and protected void/refund workflows with register-aware validation.
- Corrected cash-refund register requirements and zero-value refund transaction counting.
- Made database schema upgrades transactional and added an idempotent legacy register-ownership migration.
- Made restore staging and startup replacement durable, validated, and atomic while retaining a pre-restore safety copy.
- Added full development-build version comparison for offline updates.
- Required Windows Authenticode verification and installed-publisher continuity before an update installer can launch.
- Expanded live Bengali localization to code-generated dialogs, errors, headers, and dynamic windows.
- Corrected inactive-administrator deletion protection without weakening the last-active-admin rule.

## 1.4.3 — Streamlined POS drawer and reliable full screen

- Removed the redundant Settings action from the receipt-screen drawer; Settings remains available through Management.
- Reworked Full screen to transition through a normal window state before applying borderless maximization, ensuring Windows applies the chrome change.
- Preserved and restored the previous window style, size, state, resize mode, and always-on-top setting when leaving full screen.
- Added F11 to toggle full screen, Escape to leave it, and a localized **Exit full screen** label while the mode is active.

## 1.4.2 — Focus-aware POS search placeholder

- Converted the main barcode/code/product instruction into a focus-aware placeholder instead of persistent field text.
- The placeholder now disappears immediately when the scanner/search field receives keyboard focus.
- The placeholder returns after focus leaves only when the field is still empty, without changing typed or scanned input.

## 1.4.1 — Offline installer version detection fix

- Fixed rolling GitHub development installers being published as version `0.0.0.<run>`, which caused the in-app updater to reject them as older than an installed release.
- Development installers now use the real project version plus a prerelease run label, such as `1.4.1-dev.27`, and a Windows resource version such as `1.4.1.27`.
- The updater now reads the semantic product version from the verified installer filename and validates it against the Windows version resource.
- Installer build/run revisions are ignored when confirming the application version after restart, preventing a successful development update from being reported as not applied.
- Release builds now fail early when the requested tag or manual version does not match the version in `PosApp.Wpf.csproj`.
- Improved the rejection message so it reports the installed and selected versions accurately.

## 1.4.0 — Comprehensive reliability, accounting, and data-integrity repair

- Removed the remaining build-breaking weighted-product recall reference and completed the suspended-sale hydration path.
- Reworked refunds into a single signed reversal transaction with reverse payments, stock audit links, promotion-use rollback, and consistent cash-register/report treatment.
- Corrected local-date boundaries, inclusive end dates, local hourly grouping, decimal weighted quantities, historical cost snapshots, and distinct payment transaction counts across reports.
- Added collision-resistant sale and purchase document numbering plus database protections for duplicate refunds, case-insensitive identifiers, and multiple open register sessions.
- Hardened users and authentication with PBKDF2-SHA256, legacy-hash upgrades, consistent PIN rules, case-insensitive login, last-admin/self-protection, safer deletion, and route authorization.
- Fixed detached EF Core tracking conflicts in users, promotions, products, categories, customers, and suppliers.
- Added strict product, category, supplier, customer, purchase, tax, promotion, and CSV-import validation; stock changes now create auditable ledger entries.
- Preserved SKU and barcode separately, enforced case-insensitive uniqueness, added inactive product/customer/supplier recovery, and made promotion limits and product discount permissions effective.
- Corrected zero-total checkout, payment references, Enter-key payment behavior, receipt print failure reporting, local timestamps, currency precision, and RFC-compatible CSV escaping.
- Made Settings updates transactional and thread-safe, removed fake startup actions, eliminated receipt-printer sync-over-async behavior, and converted refresh workflows to awaitable tasks.
- Expanded English/Bengali localization across management views and standardized status/action labels.
- Added schema-upgrade repair logic for legacy duplicate identifiers, refund links, open sessions, and missing historical sale-item cost data.

## 1.3.29 — Simplified hardware controls and reliable checkboxes

- Removed the **Cash drawer** command tile, COM-port settings, automatic drawer opening, test controls, hardware drivers, service interfaces, and dependency registration.
- Removed the placeholder **Customer display** section from Settings.
- Removed the **Weigh** command from the register and the entire weighing-scale settings/hardware path. Weighted products remain available and accept decimal quantities through the normal Quantity workflow.
- Made every grid checkbox a true single-click control: user Active, product Weighted, and promotion Active states now persist immediately and restore their previous state when saving fails.
- Standardized every application checkbox as a two-state control with a consistent clickable area and visible theme-aware mark.
- Simplified Print settings to receipt-printer selection and test printing only.
- Removed obsolete English and Bengali localization resources and updated documentation.

## 1.3.28 — Customer and supplier contact type selection

- Added a **Customer / Supplier** selector when creating a new contact.
- Combined active customers and suppliers in the **Customers & Suppliers** directory with a visible Type column.
- Added supplier creation and editing directly from the shared contact page while retaining customer purchase history actions.
- Supplier removal now safely deactivates the record so existing purchase documents remain intact.
- Updated customer and supplier saves to modify tracked database records by key, preventing EF Core duplicate-tracking errors during repeated edits.
- Updated English and Bengali contact-page labels for the combined workflow.

## 1.3.27 — Functional user active-status control

- Replaced the read-only Active column with an interactive checkbox in **Users & Roles**.
- Saves activation and deactivation changes immediately to the local database.
- Keeps the grid state synchronized with an already tracked user entity to avoid stale EF Core values.
- Prevents deactivating the currently signed-in account.
- Prevents deactivating the last active administrator account.
- Restores the previous checkbox state and displays an error if persistence fails.

## 1.3.26 — Weighted products in recalled sales

- Fixed recalled/suspended receipt lines losing their weighted-product status.
- Rehydrates each recalled line from the current product catalog so the **Weigh** command recognizes products marked as weighted.
- Rechecks the catalog before showing the “Add a weighted product first” message, repairing carts created from stale product data.
- Uses the selected weighted receipt line when available, otherwise the most recently added weighted line.
- Rejects zero or negative scale readings instead of replacing the receipt quantity with an invalid value.

## 1.3.25 — Simplified receipt controls

- Removed the redundant **New sale** tile from the POS register command rail.
- Removed the F8 new-sale shortcut and its confirmation/clear-cart handler.
- Expanded **Delete** and **Quantity** evenly across the top command row.
- Kept receipt clearing available through the existing void/cancel workflow rather than exposing a duplicate action.
- Removed the unused English and Bengali `POS_NewSale` localization resources.

## 1.3.24 — Reliable F10 payment shortcut

- Fixed the F10 payment shortcut being ignored when WPF reports it as the Windows system/menu key.
- Normalized `Key.System` to its effective `SystemKey`, allowing F10 to open the payment dialog consistently.
- Marks recognized POS shortcuts handled before asynchronous operations begin, preventing Windows menu-mode processing from consuming F10.
- Routed both the Payment tile and F10 shortcut through the same checkout method so their behavior remains identical.

## 1.3.23 — Consolidated product search access

- Removed the duplicate **Search** command tile from the POS register action rail.
- Kept the top **Find products (F3)** control as the single visible product-search entry point.
- Preserved the F3 keyboard shortcut and the existing product-search overlay, filters, barcode/SKU matching, and scanner workflow.
- Expanded the remaining Delete, Quantity, and New sale tiles evenly across the released row.

## 1.3.22 — Consolidated register overflow navigation

- Removed the redundant **Menu** button from the register receipt header.
- Kept **More** as the single entry point for the same management and overflow actions.
- Simplified the receipt-header layout and removed the now-unused English and Bengali `POS_Menu` localization resources.

## 1.3.21 — Global Enter-key form navigation

- Added application-wide Enter-to-next-field navigation for single-line text boxes, password/PIN fields, editable and standard ComboBoxes, and DatePicker text inputs.
- Registered the behavior once at the Window level so it also applies to dynamically created forms and dialogs without duplicating handlers in every view.
- Preserved existing controls that intentionally handle Enter, including barcode/product search, cashier PIN submission, payment completion, and other specialized workflows.
- Preserved Enter-generated line breaks in multiline notes, addresses, comments, and description fields.
- Uses the existing WPF tab order, disabled state, and focusability rules when selecting the next control.

## 1.3.20 — Functional weighing-scale connection

- Replaced the permanently registered `NullWeighingScale` with a configurable serial-scale driver that uses the COM port saved in Settings.
- Added an explicit serial-port connection probe, so **Test scale** now opens the configured port instead of always reporting that the scale is unavailable.
- Applied scale-port changes immediately without requiring an application restart.
- Added serialized connection/read access and safe port disposal when the configured COM port changes or the device disconnects.
- Improved serial commands, timeout handling, invariant weight parsing, and Test Scale diagnostics.

## 1.3.19 — Reliable rolling GitHub releases

- Fixed rolling `dev-build` publication failing with HTTP 404 after the tag was force-moved while its previous release record was still active.
- Deletes the old rolling release before moving the `dev-build` tag, then creates or reuses a fresh release tied to the new build commit.
- Uses the release-specific `upload_url` returned by GitHub instead of reconstructing an upload endpoint from a potentially stale release ID.
- Fixed the retry wrapper so failed API calls and asset uploads propagate their exit status instead of being masked by Bash conditional-function `errexit` behavior.
- Validates each upload response and verifies only fully uploaded assets before allowing the release job to pass.
- Re-resolves the release by tag if the initially published numeric ID is temporarily unreadable.

## 1.3.18 — Reliable weighted-product updates

- Fixed the EF Core tracking conflict that prevented weighted-product checkboxes from being updated when multiple products shared the same category.
- Stopped attaching detached product/category graphs from the Products grid to the long-lived application DbContext.
- Added a field-specific weighted-status update that changes only `IsWeighted` and `UpdatedAt`, avoiding stale stock or pricing overwrites from the grid row.
- Reused the single tracked product instance for full product edits and copied only editable scalar values before saving.
- Applied the same safe persistence path to product edits and soft deletion, preventing equivalent duplicate-key tracking failures outside the weighted checkbox.

## 1.3.17 — Consolidated payment workflow

- Removed the direct **Cash**, **Card**, and **Check** payment buttons from the POS home command rail.
- Removed the F12 exact-cash shortcut so checkout methods are selected only from the F10 payment dialog.
- Preserved cash, card, and bank-transfer payment handling inside the dedicated payment workflow.
- Expanded the remaining command area to use the space released by the duplicate payment row.

## 1.3.16 — Exclusive sidebar selection

- Fixed the Dashboard item remaining highlighted when another management page, such as Documents & sales, was selected.
- Changed every management navigation button to start in the inactive style.
- Reset all sidebar button styles before applying the active style to the current page, ensuring only one item can appear selected.

## 1.3.15 — Simplified settings navigation

- Removed the duplicate **Payment types**, **Country & currency**, **Tax rates**, and **My company** entries from the management sidebar.
- Kept all four configuration areas available from the main **Settings** page.
- Removed obsolete sidebar-only localization resources and role-visibility references.

## 1.3.14 — Simplified product navigation

- Removed the duplicate **Price lists** item from the management sidebar because it opened the same Products workspace.
- Removed the obsolete role-visibility reference and unused English/Bengali localization keys for that navigation item.
- Kept **Products** as the single catalog and pricing-management entry point.

## 1.3.13 — Reliable local-date Sales History

- Fixed Sales History returning no or incomplete transactions because UTC sale timestamps were compared directly with local DatePicker values.
- Converted the inclusive local From/To calendar range into correct UTC boundaries before querying SQLite.
- Included the complete selected To date by using an exclusive next-day boundary instead of stopping at midnight.
- Changed the default To date from tomorrow to today and added clear validation for reversed date ranges.
- Aligned daily receipt-number counting with the same local-business-day UTC boundaries.

## 1.3.12 — Focus-aware product search placeholder

- Hid the product-search placeholder whenever the search field has keyboard focus, preventing the caret from appearing on top of the hint text.
- Kept the placeholder visible only while the search field is empty and unfocused.
- Removed the obsolete view-model placeholder visibility property so the behavior is controlled directly by the actual TextBox state.

## 1.3.11 — Predictable product-name search

- Made product-name filtering case-insensitive, so equivalent searches such as `Ban`, `BAN`, and `ban` return the same products.
- Changed product-name filtering to prefix matching, so `ba` finds names beginning with `ba` without incorrectly matching text buried later in a name such as `25bag`.
- Applied case-insensitive matching to product-code and barcode filters and to exact scanner/code lookup while preserving their existing identifier behavior.

## 1.3.10 — DatePicker template build correction

- Fixed WPF XAML compilation error `MC3011` by removing the inaccessible `DatePickerTextBox.Watermark` template binding.
- Added a compile-safe localized empty-date placeholder for English and Bengali.
- Preserved the theme-aware `DatePickerTextBox` layout, text colors, padding, and empty-state visibility behavior.

## 1.3.9 — Non-failing CI build summary

- Fixed the GitHub Actions summary step so it no longer throws a second `Get-Item` error when an earlier build or publish step fails.
- Checks the executable, portable ZIP, and installer independently before reporting their status.
- Keeps the summary available on failed runs while directing developers to the original failed step, preserving the actionable error.

## 1.3.8 — Complete Light-mode overlays

- Replaced the remaining hard-coded black drawer and product-search scrims with theme-aware overlay resources.
- Added a dedicated Light/Dark palette for the management drawer background, text, dividers, hover states, and border.
- Reduced Light-mode background dimming to a subtle tint while preserving click-outside-to-close behavior.
- Republish theme brushes whenever appearance changes or the drawer opens, preventing stale Dark resources from remaining on screen.

## 1.3.7 — Checkout compatibility and complete theme controls

- Added a safe, idempotent in-place database upgrade for existing stores so checkout gains the current payment table, cash-change field, discount reason, and sale-line stock link without deleting or replacing business data.
- Kept checkout transactional and now displays the underlying database error plus log location if a sale still cannot be saved.
- Replaced the display-only **Weighted** product checkbox with a clickable control that saves immediately and restores its previous value if persistence fails.
- Made promotion and user status checkboxes explicitly one-way, preventing WPF from writing into calculated read-only properties.
- Added theme-aware CheckBox, RadioButton, DatePicker/calendar, and ComboBox templates so marks, dates, selected values, and drop-down rows remain visible in both Dark and Light modes.
- Removed hard-coded dark colors from the POS command rail, management sidebar, user card, and slide-over so the complete home screen follows the selected theme.

## 1.3.6 — Discount dialog and searchable product fields

- Replaced the Windows-default combo box rendering with a theme-aware Light/Dark template so selected values remain readable, including the line-discount promotion selector.
- Reworked the line-discount dialog with visible field labels, localized text, reliable Enter-to-confirm and Escape-to-cancel behavior, and unchanged amount/percentage safety limits.
- Added explicit **All fields**, **Product name**, **Product code**, and **Barcode** filters to the F3 product search panel.
- Kept barcode scanner entry independent from the selected search filter and preserved exact code/barcode auto-add behavior.
- Added product code and barcode details to stable-sized search result cards in both English and Bengali.

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
