# PosApp - Offline-First Point of Sale (WPF / .NET 8)

A feature-rich, **offline-first** Point of Sale desktop application for Windows, built with C# and WPF on .NET 8. Version 1.10.10 fixes the setup-sign-in Release build reference while retaining online-only first-run onboarding, multi-store operations, consolidated reporting, and auditable stock transfers.

## Offline-First Cloud Boundary

Version 1.10.10 keeps local SQLite authoritative for checkout and retains all-store owner reporting, per-store inventory visibility, and the draft → dispatch → receive/cancel stock-transfer workflow. Transfer movements are append-only ledger records and participate in the existing conflict-safe cloud synchronization model.

A fresh Windows device can restore the latest full snapshots after creating an automatic local backup. Access and refresh tokens are protected with Windows DPAPI; Turso credentials and JWT secrets stay only in Worker secrets. Product and user image paths remain excluded, and no image files are uploaded.

First run is online-only. The setup window offers **Sign in** for an existing organization and **Create organization** for a new one. Existing-account sign-in accepts email/password and restores the complete cloud snapshot before onboarding completes. New organizations upload their complete initial store snapshot before the device-local completion marker is written. Device-local `app:`, `cloud:`, and `device:` settings never enter cloud snapshots or the incremental outbox.

This is an original POS implementation inspired by the publicly known feature set of POS systems in general (sales, inventory, customers, receipts, hardware integration, reports, etc.). The codebase, UI, and architecture are written from scratch.

## Features

| Module              | Highlights |
|---------------------|------------|
| **POS / Checkout**  | Full-screen receipt-first register, responsive F3 product search panel with readable scaling-aware cards, barcode/SKU scan, F2 line discount and saved promotions, F4 exact quantity/weight/volume, F7 open sales, F9 save sale, F10 payment workflow with cash/card/bank-transfer options, customer, service type, comments, automatic measured-price totals, custom refund navigation, and void order |
| **Products & Inventory** | Full CRUD for products and categories, per-item/weight/volume/length sale modes, g/kg/mL/L/m pricing units, SKU/barcode tracking, stock levels in the selected unit, low-stock alerts, stock adjustments, physical inventory counts, stock movement history, stock valuation at cost, CSV catalog import/export, and all-store inventory visibility |
| **Purchases & Suppliers** | Supplier directory, posted purchase documents, supplier invoice references, multi-line receiving, tax totals, automatic stock increases, moving-average cost, purchase history, and printable filtered purchase/supplier summaries |
| **Cash Register** | Opening float, paid-in / paid-out movements with reasons, live expected cash, payment breakdown, printable page summaries and X reports, manager-only close, counted cash, variance, and final Z reports |
| **Customers & Suppliers** | Unified contact directory with Customer/Supplier selection, type-aware editing, customer sales history, supplier purchase integration, and search by type/name/phone/email |
| **Sales / Transactions** | Filter by date and status, print the filtered sales-history page, view and reprint receipts, void (restores stock), repeatable partial refunds by item/quantity/payment method, refund tracking, suspend/recall, CSV export |
| **Users & Roles**   | PIN-based login, three roles (Cashier, Manager, Admin) with sidebar access gated by role, last-admin protection, PIN reset |
| **Multiple Stores** | Admin-only store management, create/edit/activate/deactivate/switch workflows, per-store sales, inventory, purchases, users, register sessions, settings, receipt numbers, combined owner reporting, and draft/dispatch/receive/cancel stock transfers with an audit trail |
| **Offline-First Cloud Sync** | Required first-run owner sign-in or organization creation, automatic outbox push/cursor pull, retries, idempotency, tombstones, conflict review/merge, device diagnostics, sync history, manual Sync Now, full snapshots, and fresh-device restore |
| **Reports & Dashboard** | Store or All Stores scope for administrators, inclusive From/To date filter, KPIs, daily sales, store-performance table, top products, hourly activity, payment breakdown, detailed reports, and printable page summaries |
| **Taxes & Discounts** | Per-product tax rate, reusable offline promotions with codes/dates/use limits, and percentage or fixed line discounts at the register |
| **Management workspace** | Slide-over terminal menu, role-aware back-office navigation, documents/sales, products, stock, purchases, customers/suppliers, reporting, promotions, users/security, payment/tax/company settings |
| **Settings** | Sectioned General, Order & Payment, Products, Documents, Email/offline boundary, Cloud, Print, Database, Update & Recovery, and About workflow; live English/বাংলা and Light/Dark switching |
| **Keyboard workflow** | Global Enter-to-next-field navigation for single-line text, password/PIN, date, and selection fields; existing scanner/search/payment Enter actions and multiline editors retain their specialized behavior |
| **Reliable checkboxes** | User activation, product weighted status, promotion activation, setup, and settings checkboxes use consistent two-state controls; grid changes persist immediately |
| **Receipt Printer** | ESC/POS thermal printer via raw spooler (58/80mm) AND fallback Windows PrintDocument path for any printer |
| **Hardware**        | Barcode scanner (HID keyboard + serial) and receipt printer — both degrade safely when unavailable |
| **Data Safety**     | Consistent SQLite backups on startup/exit, manual backup, retention control, validated staged restore, automatic pre-restore safety copy, and pre-migration safe-update snapshots |

## Multi-Store Transfer Workflow

1. Create a draft from the active source store and select an active destination store. Transfer numbers include a timestamp and random suffix so separate offline devices do not reuse the same number.
2. Dispatch deducts source stock and records linked negative stock-ledger entries.
3. Receive must be completed while signed into the destination store; it adds destination stock and linked positive ledger entries.
4. Cancelling a dispatched transfer restores source stock with compensating ledger entries. Received transfers cannot be cancelled.
5. If the destination lacks the product, PosApp creates matching catalog/category records without copying any image path or image file.

Version 1.10.10 retains the Stock Transfers Dark-mode fix. Both tabs use theme-aware headers and content surfaces instead of Windows default white tab chrome. The inventory store selector renders the store name rather than the `StoreFilterOption` object representation, while transfer and cross-store inventory data remain unchanged.

Version 1.10.10 also retains the Cloudflare owner-signup fix by using the Workers-compatible PBKDF2 limit of 100,000 iterations for cloud passwords. Local desktop user PIN hashing remains at 120,000 iterations. It retains the comprehensive integrity and synchronization hardening release. It makes business operations idempotent and atomic, protects stock and promotion counters with optimistic concurrency, enforces an append-only stock ledger, validates coherent multi-store backup sets before restore, quarantines invalid remote rows, hardens device authentication/logout, and closes store-authorization and historical-reporting gaps. Product/user image paths and image files remain excluded from cloud payloads.

Version 1.10.10 retains category usability on smaller/high-DPI displays. Category Management and the category editor scroll correctly, the editor includes a live color preview, and saved category colors appear in management, POS category filters, and product-card accents.

Version 1.10.10 retains the dark-mode surface cleanup. Programmatically created dialogs, management popups, and Windows caption bars follow the active theme so purchases, supplier entry, register dialogs, and similar windows no longer show bright white areas while Dark mode is enabled.

Version 1.10.10 keeps the Store Details editor resizable and vertically scrollable, keeping Code, Store Name, Address, Phone, and the action buttons reachable on smaller or display-scaled screens.

Version 1.10.10 keeps the **Require an open register before selling** option authoritative for cash and non-cash checkout. Stock-tracked checkout and item refunds insert immutable ledger rows only after final sale/item IDs exist, eliminating the append-only validation failure while retaining all-or-nothing database transactions.

Version 1.10.10 retains the hardened second checkout phase by resolving persisted sale and sale-item rows through permanent sync identifiers before inserting stock-ledger records. This prevents SQLite from receiving stale or temporary numeric references and keeps the reference-integrity triggers enabled. Trigger errors identify the exact invalid product, sale, sale-item, transfer, transfer-item, or user reference.

## Tech Stack

- **.NET 8 (LTS)** + **WPF** (XAML and code-behind)
- **EF Core 8** + **SQLite** (single offline database at `%LOCALAPPDATA%\PosApp\posapp.db`, partitioned by store)
- **Cloudflare Worker** API + **Turso/libSQL** cloud database for first-run accounts, snapshots, and incremental synchronization
- Windows **DPAPI** for access/refresh-token protection
- **System.IO.Ports** for serial barcode scanners
- **System.Drawing.Common** for Windows receipt and report printing
- Original UI styling (flat, modern, Material-inspired) with EN + BN bilingual support

## Project Structure

```
posapp/
├── .github/workflows/build.yml     # CI: build single-file exe on push/tag/manual
├── installer/                      # Branded Inno Setup wizard, license, and artwork
├── cloud/worker/                   # Self-hosted Worker API, Turso schema, and deployment guide
├── scripts/Build-Installer.ps1     # Publish app + compile installer locally
├── PosApp.sln
├── src/
│   ├── PosApp.Core/                # Entities, enums, interfaces, DTOs
│   ├── PosApp.Data/                # AppDbContext, EF Core, seeder, and schema upgrades
│   ├── PosApp.Services/            # Business logic (Auth, Inventory, Sales, Customers, Reports, Settings)
│   ├── PosApp.Hardware/            # Receipt-printer and barcode-scanner integrations
│   ├── PosApp.Localization/        # Strings.en.xaml + Strings.bn.xaml + LocalizationManager
│   └── PosApp.Wpf/                 # App, MainWindow, all views, styles, converters
└── README.md
```

## Getting Started (Local Development)

### Prerequisites
- Windows 10/11
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- (Optional) Visual Studio 2022 17.8+ or JetBrains Rider

### Build & Run
```powershell
git clone <your-repo-url>
cd posapp
dotnet restore
dotnet run --project src/PosApp.Wpf/PosApp.Wpf.csproj `
  -p:PosAppCloudApiUrl=https://your-worker.example.workers.dev
```

On first run, the app creates `%LOCALAPPDATA%\PosApp\posapp.db`.

Before the cashier login is shown, the online setup window requires one of two paths:

1. **Sign in** — enter the email and password for an existing owner account. PosApp registers the device, backs up the unused local cache, and restores the latest complete multi-store snapshot. The app closes after restore so the next launch can initialize the restored store selection safely.
2. **Create organization** — enter the owner email/password and registration key, store details, local administrator username/PIN, appearance, backup preference, and optional sample-products choice. PosApp uploads the complete initial snapshot and only then marks setup complete.

The setup completion/preparation markers are device-local and excluded from cloud synchronization. After onboarding, users sign in locally with the restored or newly created username/PIN, and normal checkout continues offline when needed.

The database also seeds:

- The administrator account whose name, username, and PIN are finalized by the setup wizard
- No shared starter cashier credential is uploaded; create cashier/manager accounts from **Users** with business-specific PINs
- 6 default categories (Beverages, Snacks, Groceries, Household, Personal Care, Produce)
- 15 sample products (mix of fixed-price and weighted) only when the setup toggle is left on
- Default tax rates and discounts

### Publish a Single-File EXE Locally
```powershell
dotnet publish src/PosApp.Wpf/PosApp.Wpf.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PosAppCloudApiUrl=https://your-worker.example.workers.dev `
  -o ./publish
```

The output is a single `PosApp.exe` (~150 MB) that runs on any Windows 10/11 x64 machine — no .NET install required. Drop it on a USB stick and run it on the POS terminal when a portable copy is preferred.

### Build the Guided Windows Installer

Install [Inno Setup 6](https://jrsoftware.org/isdl.php), then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

The output is `artifacts\installer\PosApp-1.10.10-Setup.exe`. The branded wizard provides:

1. License review and acceptance.
2. Installation-folder selection (default: `Program Files\PosApp`).
3. Start Menu folder selection.
4. A checked-by-default **Create a desktop shortcut** option.
5. A ready-to-install summary and progress page.
6. A completion page with an optional **Run PosApp** action.

The installer contains the self-contained offline app and does not download components during installation. Uninstalling removes program files and shortcuts but intentionally leaves the local database under `%LOCALAPPDATA%\PosApp` so business data is not silently deleted.

### Safe Offline Update

1. Download or copy a newer digitally signed `PosApp-<version>-Setup.exe` onto the POS computer.
2. In PosApp, open **Settings → Update & recovery → Choose Update Installer**.
3. PosApp asks Windows to verify the Authenticode signature, requires its publisher to match the installed PosApp publisher, validates the product and complete build version, calculates its SHA-256 digest, and asks for confirmation.
4. Before Setup opens, PosApp creates a SQLite snapshot under `%LOCALAPPDATA%\PosApp\Backups\Updates` and runs `PRAGMA quick_check` plus PosApp table validation against it.
5. Setup upgrades only the program files under `Program Files\PosApp`; the live database stays under the Windows user profile.
6. On the first successful launch, PosApp records the completed update and keeps the recovery backup. If startup fails, the error dialog shows the backup path so the previous app version can be reinstalled and the backup restored from **Settings → Database**.

This protection also runs before database migration when a newer installer is launched directly or a portable `PosApp.exe` is replaced. The updater does not contact a server. Unsigned builds, invalid signatures, unknown publishers, and installers signed by a publisher different from the running app are rejected. An unsigned development copy can still be upgraded by closing PosApp and manually running a trusted signed installer.

## CI / GitHub Actions

The workflow at `.github/workflows/build.yml` triggers on:

Development installers now retain the real application version in their filename and Windows metadata, for example `PosApp-1.10.10-dev.27-Setup.exe` with resource version `1.10.10.27`. This allows an installed older release to recognize the rolling development installer as a genuine upgrade. Legacy `PosApp-0.0.0-dev.*-Setup.exe` packages should not be used for in-app updates.

1. **Push to `main`** — builds and uploads the installer, portable exe, and zip as CI artifacts (retained 90 days).
2. **Tag push `v*`** (e.g. `v1.10.10`) — publishes a GitHub Release with `PosApp-<ver>-Setup.exe`, `PosApp-<ver>.exe`, and `PosApp-<ver>.zip` attached.
3. **Manual dispatch** from the Actions tab — optional `version` input; if provided, also creates a release.
4. **Pull request to `main`** — verify-only build (no artifact release).

### To release a new version

```bash
git tag v1.10.10
git push origin v1.10.10
```

The workflow will build the guided installer, portable exe, and zip, then create a public Release at `https://github.com/<you>/<repo>/releases/tag/v1.10.10`.

For in-app updates, configure these GitHub Actions repository secrets:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64` — the Base64-encoded PFX used for the PosApp publisher signature.
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` — the PFX password.

The workflow signs and verifies both `PosApp.exe` and the guided setup installer. Builds still complete when these secrets are absent, but the in-app safe updater intentionally rejects unsigned artifacts.

## Configuration

All settings persist in the SQLite database and are editable from the in-app **Settings** screen:

- **General and store**: name, address, phone, email, tax ID, currency symbol, language, theme, interface scale, and message duration
- **Order and payment**: default service type/tax, optional open-register requirement, and void confirmation
- **Product search and documents**: touch-grid preferences, virtual-keyboard preference, receipt footer, and receipt width
- **Hardware**: receipt printer name (dropdown of installed Windows printers) and test printing; completed receipts are printed on demand from Sales History
- **Language**: English (default) or বাংলা (Bengali) — switches live
- **Theme**: Light / Dark
- **Theme-aware date controls**: DatePicker fields and calendar popups use readable light/dark surfaces, headers, navigation arrows, weekday labels, date cells, and selection states
- **High-contrast boundaries**: Cards, inputs, dropdowns, dialogs, side panels, command tiles, and tables use stronger theme-aware borders; data grids show clear horizontal and vertical cell separators
- **Data safety**: automatic backups on startup and/or exit, retention count, manual backup, validated restore, and backup-folder access
- **Update and recovery**: newer local installer selection, version/hash validation, pre-update database verification, recovery backup location, and last-update status



## Required Cloud Account Deployment

The cloud component is self-hosted and is required for first-run onboarding. After setup, the local SQLite cache keeps checkout available during an outage.

1. Create a Turso database and apply `cloud/worker/schema.sql`.
2. Copy `cloud/worker/wrangler.toml.example` to `wrangler.toml`.
3. From `cloud/worker`, configure `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`, `JWT_SECRET`, and `REGISTRATION_KEY` with `wrangler secret put`.
4. Run `npm run check` and `npm run deploy`.
5. Add the deployed HTTPS Worker URL as the GitHub Actions repository variable `POSAPP_CLOUD_API_URL`, then run **Build PosApp**. The URL is embedded in the app build.
6. On first launch, choose **Sign in** or **Create organization**. Existing accounts restore their complete snapshots automatically; newly created organizations upload their complete initial snapshot automatically. PosApp subsequently synchronizes changes in the background; **Sync Now** forces an immediate cycle.

The registration key is required only when creating the owner account. Keep it private. A full snapshot is limited to 15 MB per store, and the service retains the latest three versions. Existing v1.6.0 deployments must first apply `cloud/worker/migrations/v1.7.0.sql`. v1.10.10 has no new cloud schema migration. Deploy the Worker directly; installations upgrading from before v1.10.0 still need `cloud/worker/migrations/v1.10.0.sql` only when automatic schema initialization is disabled. See `cloud/worker/README.md` for scope.

## Hardware Wiring Notes

| Device | Connection | Notes |
|--------|------------|-------|
| Barcode scanner | USB (HID keyboard mode) | Most USB scanners work out of the box — they "type" the code + Enter. The app detects fast key sequences and fires the onScan callback. |
| Receipt printer | USB (Windows printer driver) | Set the printer name in Settings. Raw ESC/POS bytes are sent via the Windows spooler (Winspool `WritePrinter`). Falls back to GDI printing if the printer is not ESC/POS-compatible. |

When an optional scanner or printer is missing, PosApp degrades safely so the register remains usable.

## Security Notes

- PINs are hashed with PBKDF2-SHA256 using a random salt and 120,000 iterations. Existing legacy SHA-256 hashes are transparently upgraded after a successful login.
- The SQLite database remains authoritative for checkout. Cloud synchronization is optional, automatic, and retryable; full snapshot restore creates a local backup before replacing data.
- Role-based access:
  - **Cashier**: POS, Register, and Sales
  - **Manager**: + Products, Inventory, Purchases, Customers, Reports, and register close/Z report
  - **Admin**: + Users and Settings

## Future Roadmap

- Chunked snapshots for stores exceeding 15 MB
- Email/SMS receipts
- Purchase orders and supplier stock returns (posted purchase receiving is included)
- Loyalty tier rules
- Barcode label printing

## License

This project is provided as-is for your personal/commercial use. The architecture, code, and UI are original works. "Aronium" is a trademark of its respective owners; this project is not affiliated with or endorsed by them and does not use any of their proprietary code or assets.

## Troubleshooting

**Build fails on `dotnet restore`** — make sure you have the .NET 8 SDK installed: `dotnet --version` should report `8.x.x`.

**Build or release reports `Invalid ... version: V...`** — use the v1.10.10 workflow. Manual versions may be entered as `1.10.10`, `v1.10.10`, or `V1.10.10`; both Windows and Linux jobs remove one leading `v`/`V` before validation.

**Cloud deployment fails** — use the v1.10.10 workflow. It runs Node.js 24 directly and does not use `cloudflare/wrangler-action` secret uploading. Ensure `CLOUDFLARE_ACCOUNT_ID` belongs to the same account selected by a token created from the **Edit Cloudflare Workers** template. Do not enable the insecure Node 20 compatibility variable.


**Category editor does not scroll or its color is not visible** — install v1.10.10 or later. Category windows expose scrollbars and saved `#RRGGBB` colors are rendered as live previews and POS accents.

**Checkout says an open register is required while the setting is disabled, or reports `Stock ledger rows are append-only and cannot be edited`** — install v1.10.10 or later. The checkout service respects the stored register option and inserts sale/refund ledger rows once with final links. Keep the existing database; no reset or migration is required.

**Checkout fails with `SQLite Error 19: Stock transaction contains an invalid reference`** — install v1.10.10 or later. Checkout and item refunds re-read the persisted sale/item identifiers before writing the append-only stock ledger. The existing database is repaired at startup by recreating the reference guards; no reset or migration is required.

**Create Account returns `Internal server error` with `Pbkdf2 ... above 100000` in Worker logs** — deploy v1.10.10 or later. Cloud account passwords use 100,000 PBKDF2 iterations; local user PIN hashing remains unchanged.

**Dark mode still shows white dialog/title-bar areas** — install v1.10.10 or later. Window surfaces, programmatically created dialogs, and Windows 10/11 caption bars are re-themed when Dark mode is active.

**Store Details fields are clipped and the form does not scroll** — install v1.10.10 or later. The editor is resizable, the form body scrolls vertically, and Save/Cancel remain fixed.

**Startup reports `No active store is available`** — install v1.10.10 or later. The app retains the v1.9.8 startup repair that creates the first `MAIN` store automatically and reactivates a valid store when necessary.

**Database upgrade errors** — do not delete the database. Review `%LOCALAPPDATA%\PosApp\posapp.log`; update recovery copies are under `%LOCALAPPDATA%\PosApp\Backups\Updates`. Reinstall the previous PosApp version if necessary, then use **Settings → Database → Restore** with the newest `posapp-before-update-*.db` or `posapp-before-startup-*.db` file.

**Receipt doesn't print** — in Settings, ensure the printer name matches exactly what's shown in `Control Panel → Devices and Printers`. Try the **Test Print** button.

**Barcode scanner doesn't fire** — most USB HID scanners work without configuration. If yours is serial-based, switch to the `SerialBarcodeScanner` driver in `App.xaml.cs`.

## Phone-only cloud deployment

The repository includes **Actions → Deploy PosApp Cloud**, which provisions/reuses Turso and deploys the Cloudflare Worker from GitHub variables and secrets. After deployment, save the Worker URL in the required repository variable `POSAPP_CLOUD_API_URL` and run **Build PosApp**. The build embeds that URL and uses the automatically detected Windows device name. Release builds fail clearly when the variable is missing so an unusable online-only installer cannot be published. No local PC, Turso CLI, Node.js, or Wrangler installation is required. See `cloud/worker/MOBILE_DEPLOY.md`.
