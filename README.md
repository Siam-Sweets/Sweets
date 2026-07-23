# PosApp - Offline-First Point of Sale (WPF / .NET 8)

A feature-rich, **offline-first** Point of Sale desktop application for Windows, built with C# and WPF on .NET 8. Version 1.9.6 retains multi-store operations, consolidated reporting, and auditable stock transfers while keeping checkout available from local SQLite.

## Offline-First Cloud Boundary

Version 1.9.6 keeps local SQLite authoritative for checkout and retains all-store owner reporting, per-store inventory visibility, and the draft → dispatch → receive/cancel stock-transfer workflow. Transfer movements are append-only ledger records and participate in the existing conflict-safe cloud synchronization model.

A fresh Windows device can restore the latest full snapshots after creating an automatic local backup. Access and refresh tokens are protected with Windows DPAPI; Turso credentials and JWT secrets stay only in Worker secrets. Product and user image paths remain excluded, and no image files are uploaded.

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
| **Offline-First Cloud Sync** | Optional owner account, automatic outbox push/cursor pull, retries, idempotency, tombstones, conflict review/merge, device diagnostics, sync history, manual Sync Now, full snapshots, and fresh-device restore |
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

The v1.9.6 patch fixes cloud deployment by running Wrangler 4.81.0 on Node.js 24 and uploading Worker code plus the encrypted configuration in one deployment. It contains no database-schema changes; existing cloud databases require only a Worker redeploy.

## Tech Stack

- **.NET 8 (LTS)** + **WPF** (XAML and code-behind)
- **EF Core 8** + **SQLite** (single offline database at `%LOCALAPPDATA%\PosApp\posapp.db`, partitioned by store)
- Optional **Cloudflare Worker** API + **Turso/libSQL** cloud database for accounts, snapshots, and incremental synchronization
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
dotnet run --project src/PosApp.Wpf/PosApp.Wpf.csproj
```

On first run, the app creates `%LOCALAPPDATA%\PosApp\posapp.db`.

Before login is shown, a one-time setup wizard asks for the store identity, currency, receipt footer, appearance, backup preference, optional sample products, and administrator username/PIN. The completed state is stored only in the local SQLite database, so setup does not appear again on later starts.

The database also seeds:

- The administrator account whose name, username, and PIN are finalized by the setup wizard
- Starter cashier user: `cashier` / PIN `1111` (change or deactivate it from **Users** before production use)
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
  -o ./publish
```

The output is a single `PosApp.exe` (~150 MB) that runs on any Windows 10/11 x64 machine — no .NET install required. Drop it on a USB stick and run it on the POS terminal when a portable copy is preferred.

### Build the Guided Windows Installer

Install [Inno Setup 6](https://jrsoftware.org/isdl.php), then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

The output is `artifacts\installer\PosApp-1.9.6-Setup.exe`. The branded wizard provides:

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

Development installers now retain the real application version in their filename and Windows metadata, for example `PosApp-1.9.6-dev.27-Setup.exe` with resource version `1.9.6.27`. This allows an installed older release to recognize the rolling development installer as a genuine upgrade. Legacy `PosApp-0.0.0-dev.*-Setup.exe` packages should not be used for in-app updates.

1. **Push to `main`** — builds and uploads the installer, portable exe, and zip as CI artifacts (retained 90 days).
2. **Tag push `v*`** (e.g. `v1.9.6`) — publishes a GitHub Release with `PosApp-<ver>-Setup.exe`, `PosApp-<ver>.exe`, and `PosApp-<ver>.zip` attached.
3. **Manual dispatch** from the Actions tab — optional `version` input; if provided, also creates a release.
4. **Pull request to `main`** — verify-only build (no artifact release).

### To release a new version

```bash
git tag v1.9.6
git push origin v1.9.6
```

The workflow will build the guided installer, portable exe, and zip, then create a public Release at `https://github.com/<you>/<repo>/releases/tag/v1.9.6`.

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



## Optional Cloud Account Deployment

The cloud component is self-hosted and is not required for local POS operation.

1. Create a Turso database and apply `cloud/worker/schema.sql`.
2. Copy `cloud/worker/wrangler.toml.example` to `wrangler.toml`.
3. From `cloud/worker`, configure `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`, `JWT_SECRET`, and `REGISTRATION_KEY` with `wrangler secret put`.
4. Run `npm run check` and `npm run deploy`.
5. Optionally add the deployed HTTPS Worker URL as the GitHub Actions repository variable `POSAPP_CLOUD_API_URL`, then run **Build PosApp**. When supplied, the URL is embedded in the app build; when omitted, PosApp builds normally for local-only use.
6. In PosApp, open **Settings → Cloud**, press **Test**, create or sign in to the owner account, then upload the initial store snapshots. The Windows device name is detected automatically and no endpoint/device-name fields are shown. PosApp will subsequently synchronize changes automatically; **Sync Now** forces an immediate cycle.

The registration key is required only when creating the owner account. Keep it private. A full snapshot is limited to 15 MB per store, and the service retains the latest three versions. Existing v1.6.0 deployments must apply `cloud/worker/migrations/v1.7.0.sql` once before deploying the new Worker. See `cloud/worker/README.md` for commands and scope.

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

- Conflict review and resolution workflow
- Cross-store stock transfer workflow
- Chunked snapshots for stores exceeding 15 MB
- Email/SMS receipts
- Purchase orders and supplier stock returns (posted purchase receiving is included)
- Loyalty tier rules
- Barcode label printing

## License

This project is provided as-is for your personal/commercial use. The architecture, code, and UI are original works. "Aronium" is a trademark of its respective owners; this project is not affiliated with or endorsed by them and does not use any of their proprietary code or assets.

## Troubleshooting

**Build fails on `dotnet restore`** — make sure you have the .NET 8 SDK installed: `dotnet --version` should report `8.x.x`.

**Build reports `Invalid build version: V...`** — use the v1.9.6 workflow. Manual versions may be entered as `1.9.6`, `v1.9.6`, or `V1.9.6`; the workflow removes one leading `v`/`V` before validation.

**Cloud deployment fails** — use the v1.9.6 workflow. It runs Node.js 24 directly and does not use `cloudflare/wrangler-action` secret uploading. Ensure `CLOUDFLARE_ACCOUNT_ID` belongs to the same account selected by a token created from the **Edit Cloudflare Workers** template. Do not enable the insecure Node 20 compatibility variable.

**Database upgrade errors** — do not delete the database. Review `%LOCALAPPDATA%\PosApp\posapp.log`; update recovery copies are under `%LOCALAPPDATA%\PosApp\Backups\Updates`. Reinstall the previous PosApp version if necessary, then use **Settings → Database → Restore** with the newest `posapp-before-update-*.db` or `posapp-before-startup-*.db` file.

**Receipt doesn't print** — in Settings, ensure the printer name matches exactly what's shown in `Control Panel → Devices and Printers`. Try the **Test Print** button.

**Barcode scanner doesn't fire** — most USB HID scanners work without configuration. If yours is serial-based, switch to the `SerialBarcodeScanner` driver in `App.xaml.cs`.

## Phone-only cloud deployment

The repository includes **Actions → Deploy PosApp Cloud**, which provisions/reuses Turso and deploys the Cloudflare Worker from GitHub variables and secrets. After deployment, optionally save the Worker URL in the repository variable `POSAPP_CLOUD_API_URL` and run **Build PosApp**. A configured build uses that embedded URL and the automatically detected Windows device name; an unconfigured build remains local-only. No local PC, Turso CLI, Node.js, or Wrangler installation is required. See `cloud/worker/MOBILE_DEPLOY.md`.

