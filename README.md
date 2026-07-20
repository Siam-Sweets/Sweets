# PosApp 2.0 - Offline-first online Point of Sale

A feature-rich Windows POS built with C# and WPF on .NET 8. Version **2.0.12** preserves the complete v1.4.24 local application and adds optional secure multi-device synchronization through a Cloudflare Worker and Turso/libSQL. SQLite remains the operational database, so checkout, lookup, printing, reports, and normal back-office work continue when the network or cloud service is unavailable.

## Offline-first boundary

The desktop never connects directly to Turso and never contains a Turso token, JWT secret, or Cloudflare credential. It sends bounded HTTPS JSON batches to the versioned Worker API. Every local business change and its outbox operation are committed in one SQLite transaction; synchronization runs asynchronously and never becomes a prerequisite for operating the register. Online accounts are optional, and a disconnected installation retains the original local POS behavior.

See [Architecture](docs/ARCHITECTURE.md), [Cloud setup](docs/CLOUD-SETUP.md), [Sync protocol](docs/SYNC-PROTOCOL.md), [Security](docs/SECURITY.md), [free-plan guidance](docs/FREE-PLAN.md), and [Troubleshooting](docs/TROUBLESHOOTING.md).

Version 2.0 evolves the existing v1.4.24 PosApp codebase in place. Its operational entities, UI, reports, printing, localization, migrations, updater, installer, and release history remain intact; online accounts and synchronization are additive layers around the established local application.

## Features

| Module              | Highlights |
|---------------------|------------|
| **POS / Checkout**  | Full-screen receipt-first register, responsive F3 product search panel with readable scaling-aware cards, barcode/SKU scan, F2 line discount and saved promotions, F4 exact quantity/weight/volume, F7 open sales, F9 save sale, F10 payment workflow with cash/card/bank-transfer options, customer, service type, comments, automatic measured-price totals, custom refund navigation, and void order |
| **Products & Inventory** | Full CRUD for products and categories, per-item/weight/volume/length sale modes, g/kg/mL/L/m pricing units, SKU/barcode tracking, stock levels in the selected unit, low-stock alerts, stock adjustments, physical inventory counts, stock movement history, stock valuation at cost, CSV catalog import/export |
| **Purchases & Suppliers** | Supplier directory, posted purchase documents, supplier invoice references, multi-line receiving, tax totals, automatic stock increases, moving-average cost, purchase history, and printable filtered purchase/supplier summaries |
| **Cash Register** | Opening float, paid-in / paid-out movements with reasons, live expected cash, payment breakdown, printable page summaries and X reports, manager-only close, counted cash, variance, and final Z reports |
| **Customers & Suppliers** | Unified contact directory with Customer/Supplier selection, type-aware editing, customer sales history, supplier purchase integration, and search by type/name/phone/email |
| **Sales / Transactions** | Filter by date and status, print the filtered sales-history page, view and reprint receipts, void (restores stock), repeatable partial refunds by item/quantity/payment method, refund tracking, suspend/recall, CSV export |
| **Users & Roles**   | PIN-based login, three roles (Cashier, Manager, Admin) with sidebar access gated by role, last-admin protection, PIN reset |
| **Reports & Dashboard** | Management dashboard with an inclusive From/To date filter, range KPIs, today KPI, daily sales, top products, hourly activity, payment breakdown, detailed reports, and printable page summaries |
| **Taxes & Discounts** | Per-product tax rate, reusable offline promotions with codes/dates/use limits, and percentage or fixed line discounts at the register |
| **Management workspace** | Slide-over terminal menu, role-aware back-office navigation, documents/sales, products, stock, purchases, customers/suppliers, reporting, promotions, users/security, payment/tax/company settings |
| **Settings** | Sectioned General, Order & Payment, Products, Documents, Email/offline boundary, Print, Database, Update & Recovery, and About workflow; live English/বাংলা and Light/Dark switching |
| **Keyboard workflow** | Global Enter-to-next-field navigation for single-line text, password/PIN, date, and selection fields; existing scanner/search/payment Enter actions and multiline editors retain their specialized behavior |
| **Reliable checkboxes** | User activation, product weighted status, promotion activation, setup, and settings checkboxes use consistent two-state controls; grid changes persist immediately |
| **Receipt Printer** | ESC/POS thermal printer via raw spooler (58/80mm) AND fallback Windows PrintDocument path for any printer |
| **Hardware**        | Barcode scanner (HID keyboard + serial) and receipt printer — both degrade safely when unavailable |
| **Data Safety**     | Consistent SQLite backups on startup/exit, manual backup, retention control, validated staged restore, automatic pre-restore safety copy, and pre-migration safe-update snapshots |
| **Online account & sync** | Organization/store isolation, username or email login, rotating sessions, DPAPI-protected tokens, registered devices, background push/pull, tombstones, conflict review, migration from existing SQLite, and explicit post-restore reconciliation |

## Tech Stack

- **.NET 8 (LTS)** + **WPF** (XAML and code-behind)
- **EF Core 8** + **SQLite** (single-file local DB at `%LOCALAPPDATA%\PosApp\posapp.db`)
- **Cloudflare Workers** versioned HTTPS API (optional online service)
- **Turso/libSQL** multi-tenant cloud database accessed only by the Worker
- **System.IO.Ports** for serial barcode scanners
- **System.Drawing.Common** for Windows receipt and report printing
- Original UI styling (flat, modern, Material-inspired) with EN + BN bilingual support

## Project Structure

```
posapp/
├── .github/workflows/              # Windows release build + separate Worker deployment
├── cloud/
│   ├── migrations/                 # Ordered Turso/libSQL schema migrations
│   └── worker/                     # Cloudflare Worker API and Vitest suite
├── docs/                            # Architecture, deployment, protocol, security, limits, troubleshooting
├── installer/                      # Branded Inno Setup wizard, license, and artwork
├── scripts/Build-Installer.ps1     # Publish app + compile installer locally
├── PosApp.sln
├── src/
│   ├── PosApp.Core/                # Entities, enums, interfaces, DTOs
│   ├── PosApp.Data/                # AppDbContext, EF Core, seeder, and schema upgrades
│   ├── PosApp.Services/            # Business logic (Auth, Inventory, Sales, Customers, Reports, Settings)
│   ├── PosApp.Hardware/            # Receipt-printer and barcode-scanner integrations
│   ├── PosApp.Localization/        # Strings.en.xaml + Strings.bn.xaml + LocalizationManager
│   └── PosApp.Wpf/                 # App, MainWindow, all views, styles, converters
├── tests/PosApp.Sync.Tests/         # SQLite outbox/protocol/localization tests
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

Before login is shown, a one-time setup wizard offers two safe paths. A local-only installation enters its store identity, currency, receipt footer, appearance, backup preference, optional sample products, and administrator username/PIN. A computer joining PosApp Online can instead sign in or create an organization immediately; it registers the device, creates its device-only offline PIN, and downloads the authorized organization without first inventing a throwaway local account. Online setup is two-phase: the device-local completed marker is written only after the initial migration or cursor-zero download succeeds. An interrupted setup resumes the same safe path after restart, and neither setup marker is synchronized.

Local setup creates the administrator only after the owner chooses a username and PIN; there are no built-in or known default credentials. The database seeds:

- 6 default categories (Beverages, Snacks, Groceries, Household, Personal Care, Produce)
- 15 sample products (mix of fixed-price and weighted) only when the setup toggle is left on
- Default tax rates and discounts

When a new online organization is created from first-run setup, these local templates are uploaded under the protected cloud-empty migration lease. When the computer joins an existing organization, bootstrap templates are removed before the initial pull so the cloud catalog remains authoritative. If the final migration response is lost, the next startup verifies the completed lease and server counts rather than uploading the snapshot again.

When an already-configured v1.x database is linked, PosApp first creates a verified backup and pauses all push/pull activity. The administrator must explicitly upload that local snapshot to a server-verified empty organization or replace the local synchronized working copy with server data. It never silently combines populated local and cloud datasets.

### Publish a Single-File EXE Locally
```powershell
dotnet publish src/PosApp.Wpf/PosApp.Wpf.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PosAppCloudApiBaseUrl=https://your-worker.example.workers.dev `
  -o ./publish
```

`PosAppCloudApiBaseUrl` is embedded into the executable. The online-account forms do not ask users for a Worker address. Supply only the Worker origin, without `/api/v1`, `/api/v1/meta`, query parameters, or a trailing API path.

The organization-creation dialog validates every field before contacting the Worker. Online passwords must contain 10–128 characters with at least one letter and one number, and the device-local offline PIN must contain 4–12 digits. Validation, network, and server failures are shown in a visible dialog instead of being limited to a footer message.

The output is a single `PosApp.exe` (~150 MB) that runs on any Windows 10/11 x64 machine — no .NET install required. Drop it on a USB stick and run it on the POS terminal when a portable copy is preferred.

### Build the Guided Windows Installer

Install [Inno Setup 6](https://jrsoftware.org/isdl.php), set the Worker origin, then run:

```powershell
$env:POSAPP_CLOUD_API_BASE_URL = "https://your-worker.example.workers.dev"
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

The output is `artifacts\installer\PosApp-2.0.12-Setup.exe`. The branded wizard provides:

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

Development installers retain the real application version in their filename and Windows metadata, for example `PosApp-2.0.12-dev.27-Setup.exe` with resource version `2.0.12.27`. This allows an installed older release to recognize the rolling development installer as a genuine upgrade. Legacy `PosApp-0.0.0-dev.*-Setup.exe` packages should not be used for in-app updates.

1. **Push to `main`** — builds and uploads the installer, portable exe, and zip as CI artifacts (retained 90 days).
2. **Tag push `v*`** (e.g. `v2.0.12`) — publishes a GitHub Release with `PosApp-<ver>-Setup.exe`, `PosApp-<ver>.exe`, and `PosApp-<ver>.zip` attached.
3. **Manual dispatch** from the Actions tab — optional `version` input; if provided, also creates a release.
4. **Pull request to `main`** — verify-only build (no artifact release).

### To release a new version

```bash
git tag v2.0.12
git push origin v2.0.12
```

The workflow will build the guided installer, portable exe, and zip, then create a public Release at `https://github.com/<you>/<repo>/releases/tag/v2.0.12`.

`.github/workflows/deploy-worker.yml` independently type-checks and tests the Worker, validates all ordered Turso migrations, performs a Wrangler dry run, applies every pending migration to the selected Turso database, verifies schema version 4 and required tables/columns, uploads the required Turso and authentication bindings from protected GitHub secrets, and deploys the selected `development` or `production` environment. After deployment it calls the public `/api/v1/diagnostics` endpoint and fails unless password hashing, token signing, schema inspection, the complete atomic organization-provisioning batch and forced rollback verification all pass. Opening the Worker base URL now displays the same status in a readable page instead of returning `AUTH_REQUIRED`. Follow [Cloud setup](docs/CLOUD-SETUP.md) before enabling deployment.

Configure these GitHub Actions repository secrets before building distributable desktop artifacts:

- `POSAPP_CLOUD_API_BASE_URL` — the deployed Worker origin, for example `https://posapp-cloud-api-production.example.workers.dev`. The workflow rejects API paths, query parameters, fragments, and non-HTTPS public endpoints.
- `WINDOWS_SIGNING_CERTIFICATE_BASE64` — the Base64-encoded PFX used for the PosApp publisher signature.
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` — the PFX password.

The Worker URL is build configuration rather than an authentication credential and can be recovered from a distributed executable; storing it as a GitHub secret prevents accidental repository hard-coding but does not make the URL confidential. Main, tag, and manually dispatched builds fail when `POSAPP_CLOUD_API_BASE_URL` is missing. Pull-request verification builds remain compilable without repository secrets, but their online-account controls are disabled.

The workflow signs and verifies both `PosApp.exe` and the guided setup installer. Builds still complete when the signing secrets are absent, but the in-app safe updater intentionally rejects unsigned artifacts.

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


## Hardware Wiring Notes

| Device | Connection | Notes |
|--------|------------|-------|
| Barcode scanner | USB (HID keyboard mode) | Most USB scanners work out of the box — they "type" the code + Enter. The app detects fast key sequences and fires the onScan callback. |
| Receipt printer | USB (Windows printer driver) | Set the printer name in Settings. Raw ESC/POS bytes are sent via the Windows spooler (Winspool `WritePrinter`). Falls back to GDI printing if the printer is not ESC/POS-compatible. |

When an optional scanner or printer is missing, PosApp degrades safely so the register remains usable.

## Security Notes

- PINs are hashed with PBKDF2-SHA256 using a random salt and 120,000 iterations. Existing legacy SHA-256 hashes are transparently upgraded after a successful login.
- The SQLite database under `%LOCALAPPDATA%\PosApp\` is always the local working copy and should still be backed up regularly. Cloud synchronization is not a substitute for verified backups.
- Access and refresh tokens are encrypted for the current Windows user with DPAPI. No token is written to normal settings or SQLite.
- Online passwords use PBKDF2-HMAC-SHA-256 with a unique random salt and 310,000 iterations. Refresh tokens are stored only as keyed hashes in Turso and rotate after every use.
- Every Worker query is parameterized and protected endpoints re-check the active session, device, tenant, role, and permission against Turso.
- Role-based access:
  - **Cashier**: POS, Register, and Sales
  - **Manager**: + Products, Inventory, Purchases, Customers, Reports, and register close/Z report
  - **Admin**: + Users and Settings

## Future Roadmap

- Optional managed cloud backup/export retention beyond synchronized operational data
- Email/SMS receipts
- Purchase orders and supplier stock returns (posted purchase receiving is included)
- Loyalty tier rules
- Barcode label printing

## License

This project is provided as-is for your personal/commercial use. The architecture, code, and UI are original works. "Aronium" is a trademark of its respective owners; this project is not affiliated with or endorsed by them and does not use any of their proprietary code or assets.

## Troubleshooting

**Build fails on `dotnet restore`** — make sure you have the .NET 8 SDK installed: `dotnet --version` should report `8.x.x`.

**Database upgrade errors** — do not delete the database. Review `%LOCALAPPDATA%\PosApp\posapp.log`; update recovery copies are under `%LOCALAPPDATA%\PosApp\Backups\Updates`. Reinstall the previous PosApp version if necessary, then use **Settings → Database → Restore** with the newest `posapp-before-update-*.db` or `posapp-before-startup-*.db` file.

**Receipt doesn't print** — in Settings, ensure the printer name matches exactly what's shown in `Control Panel → Devices and Printers`. Try the **Test Print** button.

**Barcode scanner doesn't fire** — most USB HID scanners work without configuration. If yours is serial-based, switch to the `SerialBarcodeScanner` driver in `App.xaml.cs`.
