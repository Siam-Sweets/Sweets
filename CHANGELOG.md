# Changelog

## 2.0.12 — Atomic Turso signup batch and transparent deployment diagnostics

- Replaced organization creation's long-lived interactive Turso transaction with one non-interactive `client.batch(..., "write")` transaction, eliminating the repeated remote round trips that could expire before provisioning completed.
- Preserved atomic all-or-nothing creation of the organization, store, administrator, assignment, device, session, refresh token, synchronized user record, sync change, and audit events.
- Added failed batch-statement mapping so `ORGANIZATION_PROVISIONING_FAILED` still identifies the exact safe provisioning stage.
- Reworked the public organization preflight to use the same atomic batch mechanism and an intentional final primary-key conflict as a rollback sentinel, then verifies that no diagnostic organization remains.
- Changed `/api/v1/diagnostics` to always return readable JSON with HTTP 200; readiness remains explicit in `ready`, `accountCreationReady`, and each check's status.
- Updated GitHub Actions to wait for the expected deployed Worker version, capture diagnostics without `curl --fail`, print every check and failed stage, and report the request ID instead of repeating an opaque HTTP 503 twelve times.
- Updated the status page and cloud documentation to describe the atomic batch preflight and machine-readable readiness contract.
- Updated application, installer, cloud client, Worker package, tests, README, and release metadata to version 2.0.12.

## 2.0.11 — Public Worker diagnostics and reliable organization provisioning

- Replaced the authenticated root API response with a public, responsive Worker status page at `/` that shows deployment, database, schema, authentication, and organization-creation readiness without exposing secrets or user data.
- Added `/api/v1/diagnostics`, including production password hashing, JWT verification, required table-and-column inspection, and a complete organization/account/device/session/sync/audit write transaction that is always rolled back and verified.
- Replaced the opaque organization-creation SQL batch with ordered transaction steps so remote libSQL failures identify the exact safe provisioning stage while preserving atomic rollback.
- Added `ORGANIZATION_PROVISIONING_FAILED` responses with a request ID, sanitized stage, and provider code instead of a generic internal error.
- Tightened unique-constraint detection so foreign-key and other database constraints are no longer mislabeled as an existing username or email.
- Updated the deployment workflow to require the environment-specific `POSAPP_CLOUD_API_BASE_URL`, call the deployed diagnostic endpoint, verify `accountCreationReady`, and fail the deployment when the public status page does not pass.
- Added real SQLite/libSQL integration coverage for the public status page, rollback preflight, and production organization-signup transaction; all 38 Worker tests pass.
- Updated application, installer, cloud client, Worker package, README, and release metadata to version 2.0.11.

## 2.0.10 — Automated Turso migration and schema readiness

- Added an idempotent Turso migration runner that applies pending reviewed SQL files in order and verifies schema version 4, required tables, and the `registered_devices.assigned_store_id` column.
- Updated GitHub Actions to migrate and verify the selected development or production database before deploying the Worker, so an empty Turso database can no longer produce a reachable but unusable API.
- Changed `/api/v1/meta` to verify actual database reachability and migration state instead of reporting readiness from secret presence alone.
- Added `DATABASE_SCHEMA_NOT_READY` handling, localized desktop guidance, and sanitized request-ID logging without SQL, payloads, URLs, tokens, or credentials.
- Added Worker tests for missing and outdated Turso schemas and validated the migration runner against a local libSQL database twice to confirm idempotency.
- Updated application, installer, cloud client, Worker package, tests, README, and release metadata to version 2.0.10.

## 2.0.9 — Worker runtime-secret deployment validation

- Added the required Turso and authentication secret declarations to Wrangler configuration.
- Updated GitHub Actions to validate and upload `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`, `JWT_SIGNING_SECRET`, and `REFRESH_TOKEN_SECRET` before deploying the selected Worker environment.
- Split database and authentication configuration failures into actionable API error codes and desktop messages.
- Added non-sensitive Worker readiness flags to `/api/v1/meta` so deployment configuration can be verified in a browser.
- Updated application, installer, cloud client, Worker package, tests, README, and release metadata to version 2.0.9.

## 2.0.8 — Visible online-account validation and responsive creation dialog

- Added immediate field validation before online sign-in or organization creation, including the Worker-compatible username, email, password, offline PIN, and device-name rules.
- Displayed password requirements directly below the organization password fields: 10–128 characters with at least one letter and one number.
- Replaced silent footer-only failures with an owner-bound warning or error dialog, while retaining a high-contrast status message and request ID in the account window.
- Forced the busy indicator to render before network work begins so a slow DNS, TLS, Worker, or Turso request no longer appears as an unresponsive button.
- Constrained the online-account window to the Windows working area, made it resizable, and enabled mouse-wheel, touchpad, and touch scrolling at high display scaling.
- Aligned desktop password validation with the Cloudflare Worker password policy and added Bengali localization for all new validation feedback.
- Updated application, installer, cloud client, Worker package, tests, README, and release metadata to version 2.0.8.

## 2.0.7 — Build-configured cloud endpoint

- Removed the Cloudflare Worker address fields from online sign-in and organization creation; users now enter only their account, password, offline PIN, device, organization, and store details.
- Added the `POSAPP_CLOUD_API_BASE_URL` GitHub Actions secret, strict HTTPS-origin validation, and build-time assembly metadata so the Windows executable receives one administrator-controlled Worker endpoint.
- Made the embedded endpoint authoritative for existing cached cloud sessions, automatically replacing a previously stored endpoint after an application update.
- Replaced the visible endpoint on the account-management page with a generic configured-service status so the deployment address is no longer part of normal UI workflow.
- Kept pull-request verification builds possible when repository secrets are unavailable while requiring the endpoint secret for main, tag, and manually dispatched distributable builds.
- Updated application, installer, cloud client, Worker package, tests, README, and cloud deployment documentation to version 2.0.7.

## 2.0.6 — Responsive, scrollable login window

- Wrapped the complete cashier login layout in a vertical `ScrollViewer` so mouse-wheel, touchpad, touchscreen, and scrollbar navigation work when the content exceeds the available height.
- Constrained the login window to the active Windows working area, including high-DPI display scaling and the taskbar.
- Allowed the login window to be resized while retaining safe minimum dimensions and horizontal layout constraints.
- Preserved access to online-account, exit, offline-help, and footer controls on smaller displays.

## 2.0.5 — WPF localized message helper build fix

- Imported `PosApp.Wpf.Helpers` in `MainWindow.xaml.cs`, resolving CS0103 for its unqualified `LocalizedMessageBox` calls, including the terminal cloud-session warning at line 279.
- Normalized the existing fully qualified `LocalizedMessageBox` calls in `MainWindow` to use the same namespace import.
- Updated application, installer, cloud client, Worker package, tests, and README version metadata to 2.0.5.

## 2.0.4 — Immutable sale composition capture fix

- Counted sale items, sale payments, and purchase items with the permanent local key bound after `SaveChanges(false)` instead of the entity CLR `Id`, which can still expose its pre-save default value during atomic outbox capture.
- Restored accurate `expectedItemCount` and `expectedPaymentCount` values for newly completed sales, allowing immutable financial composition validation to pass.
- Updated application, installer, cloud client, Worker package, workflow examples, tests, and README version metadata to 2.0.4.

## 2.0.3 — Synchronization runtime and test reliability fixes

- Bound outbox capture to permanent post-save SQLite keys so added, edited, and deleted records retain one stable UUID identity and one compacted pending operation.
- Reloaded already-tracked sync identities and outbox rows after the atomic metadata write, preventing stale server versions, tombstones, and payloads in long-lived application contexts.
- Made settings lookup explicitly branch-aware so two stores can safely retain the same synchronized setting key without overwriting one another.
- Disabled SQLite pooling in file-backed synchronization tests, closed test connections deterministically, and removed database, WAL, and shared-memory files without Windows runner lock failures.
- Corrected product test fixtures to include their required category relationship and added an explicit single-threaded xUnit runner configuration for the process-wide synchronization scope.
- Updated application, installer, cloud client, Worker package, workflow examples, and README version metadata to 2.0.3.

## 2.0.2 — Synchronization test build fix

- Added a project-wide xUnit namespace import so `Fact`, `Theory`, `InlineData`, `Assert`, and `IAsyncLifetime` compile across every synchronization test file.
- Replaced interpolated table-identifier deletion commands in restore reconciliation with a reviewed fixed-SQL statement list, eliminating EF1002 without suppressing the analyzer.
- Updated application, installer, cloud client, Worker package, workflow examples, and README version metadata to 2.0.2.

## 2.0.1 — Build and deployment compatibility fixes

- Rewrote EF Core pull and status queries to use expression-tree-compatible comparisons and captured scalar parameters, fixing CS8122 and CS8110 during the Windows synchronization-test build.
- Enforced a non-null cloud user identity while capturing local outbox operations, removing nullable assignments and preserving audit attribution.
- Replaced the obsolete EF Core check-constraint configuration and executed validated internal SQLite index DDL through database commands, removing the reported EF warnings without accepting untrusted identifiers.
- Updated the Cloudflare deployment action to its Node 24-compatible v4 release, pinned Wrangler 4.112.0, and added explicit credential preflight errors for missing GitHub secrets.

## 2.0.0 — Secure multi-device offline-first synchronization

- Preserved the complete v1.4.24 WPF application and added optional organization accounts, stores, registered devices, secure online login, session management, and administrator-created online users.
- Added a transactional SQLite outbox, UUID identity map, incremental cursors, bounded push/pull batches, retry with jittered exponential backoff, tombstones, explicit conflict records, and live background synchronization without blocking the register.
- Deferred event-triggered uploads until their outer checkout/refund/void/purchase/register/import/inventory SQLite transaction has committed, preventing pre-commit reads while still starting an immediate online sync after important operations.
- Added a versioned Cloudflare Worker REST API with PBKDF2 password hashing, short-lived signed access tokens, rotating hashed refresh tokens, device/session revocation, persistent hashed-key brute-force protection, structured errors, request IDs, decompressed input limits, parameterized Turso queries, audit events, and server-side role/tenant/store validation.
- Added ordered Turso/libSQL migrations covering organizations, stores, users, devices, sessions, synchronized operational entities, operation deduplication, change cursors, tombstones, and audit logs.
- Added immutable financial-transition checks, versioned catalog-price validation, cumulative refund limits, related-record and arithmetic validation, unique business-source protections, and append-only inventory movement synchronization so completed payments, sales, purchases, refunds, voids, and stock deductions cannot be silently overwritten or replayed.
- Preserved suspended-sale identity through recall and checkout, staged draft lines before finalization, declared immutable child counts with server-side aggregate reconciliation, cancelled never-uploaded create/delete pairs, and added explicit purchase document/item ledger links with deterministic legacy backfill.
- Added initial migration safeguards with pre-sync detection of unlinked local records, a verified safety backup and explicit local/server reconciliation gate, an atomic server-side cloud-empty lease, dependency-ordered resumable upload, UUID conversion, operation idempotency, count verification, and explicit reconciliation after restoring an older local backup.
- Added the Account & Sync UI, connection status bar, manual sync/retry, pending/conflict counts, device sessions, store selector, migration flow, conflict decisions, restore reconciliation, secure logout, password changes, and matching English/Bengali resources.
- Added direct online sign-in/organization creation to first-run setup, removed known bootstrap credentials, kept device-local setup state out of synchronization, and made a new device clear template-only data before downloading its organization.
- Made first-run online setup two-phase and restart-safe: organization creation retains its protected initial-upload decision until verified completion, while an existing organization cannot be marked ready before its cursor-zero pull succeeds. Lost migration completion responses recover from server lease history and count verification.
- Made one local SQLite working copy branch-aware by separating identical store-scoped catalog/settings identifiers and open registers across authorized branches; added matching server-side normalized identifiers and a shared SKU/barcode namespace.
- Made branch switching reload the selected store's receipt, currency, printer, language, and theme settings and explicitly clear the warned-about unsaved cart, preventing a cached cart or settings from crossing stores.
- Added per-operation Turso savepoints so a partial constraint, cursor, or audit failure cannot survive as a rejected synchronization result.
- Capped push requests at two operations and pipelined independent Turso reads and savepoint recovery statements, retaining transactional batching while leaving headroom beneath the Cloudflare Workers Free external-subrequest ceiling.
- Added schema-v4 financial composition staging: finalized sale/purchase headers and children remain private during interrupted multi-batch uploads, then publish atomically through a cursor-ordered completion replay only after immutable counts and monetary totals reconcile.
- Persisted the installation UUID before the first network request, isolated cached PIN users from one another's cloud tokens on shared terminals, and continued capturing correctly attributed offline changes after secure online logout.
- Locked the active WPF session back to the sign-in window after an explicit user/device/session/organization/store revocation or terminal refresh expiry, with localized notification while retaining the documented cached-offline sign-in policy.
- Made expired initial-migration leases resumable only by their original tenant/store/user/device, retained the verified migration-backup path across restart, and preserved device-local `app:` settings during server-authoritative restore reconciliation.
- Restricted server-stored custom permissions to an explicit allow-list and rejected wildcard or unknown client-supplied grants.
- Added DPAPI-protected desktop token storage; no Turso credential, Worker secret, access token, or refresh token is stored in ordinary configuration or SQLite.
- Added Worker authentication/rotation/revocation and synchronization tests, transactional SQLite outbox tests, localization parity checks, a separate Cloudflare deployment workflow, and complete architecture/setup/security/protocol/free-plan/troubleshooting documentation.

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
