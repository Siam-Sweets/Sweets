# Changelog

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
