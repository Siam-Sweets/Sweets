# PosApp 2.1 - Offline-first multi-organization Point of Sale

A feature-rich Windows POS built with C# and WPF on .NET 8. Version **2.1.0** preserves the complete v1.4.24 desktop application, adds safely isolated organization profiles, and requires secure online account onboarding through a Cloudflare Worker and Turso/libSQL. SQLite remains the operational working database after onboarding, so checkout, lookup, printing, reports, and normal back-office work continue when the network or cloud service is temporarily unavailable.

## Offline-first boundary

The desktop never connects directly to Turso and never contains a Turso token, JWT secret, or Cloudflare credential. It sends bounded HTTPS JSON batches to the versioned Worker API. Every local business change and its outbox operation are committed in one SQLite transaction. Internet access is required to sign in or create the organization and complete the initial full synchronization; afterward the register remains offline-first and queues changes safely during temporary outages.

See [Architecture](docs/ARCHITECTURE.md), [Cloud setup](docs/CLOUD-SETUP.md), [Sync protocol](docs/SYNC-PROTOCOL.md), [Security](docs/SECURITY.md), [free-plan guidance](docs/FREE-PLAN.md), and [Troubleshooting](docs/TROUBLESHOOTING.md).

Version 2.0 evolves the existing v1.4.24 PosApp codebase in place. Its operational entities, UI, reports, printing, localization, migrations, updater, installer, and release history remain intact; an online organization is now the required source of identity and synchronization, while SQLite remains the local operational cache.

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
| **Reliable checkboxes** | User activation, product weighted status, promotion activation and settings checkboxes use consistent two-state controls; grid changes persist immediately |
| **Receipt Printer** | ESC/POS thermal printer via raw spooler (58/80mm) AND fallback Windows PrintDocument path for any printer |
| **Hardware**        | Barcode scanner (HID keyboard + serial) and receipt printer — both degrade safely when unavailable |
| **Data Safety**     | Consistent SQLite backups on startup/exit, manual backup, retention control, validated staged restore, automatic pre-restore safety copy, and pre-migration safe-update snapshots |
| **Online account & sync** | Multiple isolated organization profiles, organization/store isolation, username or email login, rotating sessions, profile-specific DPAPI-protected tokens and device IDs, registered devices, background push/pull, tombstones, conflict review, migration from existing SQLite, and explicit post-restore reconciliation |

## Tech Stack

- **.NET 8 (LTS)** + **WPF** (XAML and code-behind)
- **EF Core 8** + **SQLite** (one operational database per organization profile; an upgraded installation keeps `%LOCALAPPDATA%\PosApp\posapp.db`, while additional profiles use `%LOCALAPPDATA%\PosApp\Profiles\<profile-id>\posapp.db`)
- **Cloudflare Workers** versioned HTTPS API (required for first-run account onboarding and synchronization)
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

On first run, the app creates only the device-local SQLite schema at `%LOCALAPPDATA%\PosApp\posapp.db`, then opens the online account window. There is no local/offline setup wizard and no independent local administrator. The user must either **Sign in** to an existing organization or **Create organization**. The account form also creates the device-only offline PIN used for cached login after onboarding.

PosApp does not open the cashier login until the complete initial synchronization succeeds. Both a newly created organization and an existing organization start from a clean device cache and download the authorized store from cursor zero, including users, store settings, catalog, customers, suppliers, inventory, purchases, sales, payments, register data, discounts, taxes, and synchronization metadata. Store details entered during organization creation are then saved through the normal synchronized settings channel.

Online onboarding is resumable and two-phase: a device-local preparation marker is written first, while `app:setup-complete` is written only after the full download and any initial synchronized settings upload finish with no pending operations or conflicts. Neither marker is synchronized. Before setup completes, local business rows are treated only as disposable cache state; interrupted migration/outbox state from an earlier build is cleared automatically so it cannot block sign-in.

PosApp no longer offers first-run local database migration or offline setup. Turso is authoritative during onboarding, and the local SQLite database becomes the offline working cache only after the organization snapshot has been verified.

### Add or switch organizations safely

PosApp 2.1 allows one Windows installation to use any number of online organizations without reusing a tenant database or cloud device identity:

1. Open **Online account & sync** and select **Add organization**. The account window offers the same control before cashier login and during first-run recovery.
2. PosApp attempts one final sync, stops the background worker, creates a random local profile ID, and restarts. A network failure does not discard anything; the former profile's pending outbox remains in its own SQLite database.
3. In the empty profile, choose **Sign in** for another existing organization or **Create organization** for a new one.
4. To return later, select the saved organization under **Organization profiles**, choose **Switch**, and let PosApp restart into that cache.

Each profile has a separate SQLite database, backup folder, staged-restore file, globally unique device ID, cached users/PINs, sync cursor/outbox, and DPAPI-encrypted token file. The small `%LOCALAPPDATA%\PosApp\Profiles\profiles.json` selector contains friendly organization metadata only—never passwords, PINs, access tokens, refresh tokens, or cloud secrets. Restart-based switching is deliberate: the running EF Core container stays bound to one database for its entire lifetime, preventing a transaction from crossing tenant boundaries. Existing installations are registered as the `legacy` profile without moving or rewriting `posapp.db` or `Security\cloud-session.dat`.

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

Each online sign-in or organization-creation attempt also receives a diagnostic ID. If the complete initial synchronization cannot finish, the dialog shows that ID, a sync-state/count/cursor summary, the exact `%LOCALAPPDATA%\PosApp\Logs\cloud-sync.jsonl` path, and an **Open log folder** button. The rotating JSON-lines log follows authentication, local-cache preparation, server compatibility, push, pull, settings upload, and final verification. It records structured error codes and sanitized exception metadata but excludes passwords, PINs, tokens, authorization headers, request payloads, SQL, usernames/emails, and customer contact data. See [Troubleshooting](docs/TROUBLESHOOTING.md) for correlation instructions.

The output is a single `PosApp.exe` (~150 MB) that runs on any Windows 10/11 x64 machine — no .NET install required. Drop it on a USB stick and run it on the POS terminal when a portable copy is preferred.

### Build the Guided Windows Installer

Install [Inno Setup 6](https://jrsoftware.org/isdl.php), set the Worker origin, then run:

```powershell
$env:POSAPP_CLOUD_API_BASE_URL = "https://your-worker.example.workers.dev"
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

The output is `artifacts\installer\PosApp-2.1.0-Setup.exe`. The branded wizard provides:

1. License review and acceptance.
2. Installation-folder selection (default: `Program Files\PosApp`).
3. Start Menu folder selection.
4. A checked-by-default **Create a desktop shortcut** option.
5. A ready-to-install summary and progress page.
6. A completion page with an optional **Run PosApp** action.

The installer contains the self-contained desktop app and does not download components during installation. Internet access is required for initial online account onboarding and later cloud synchronization/authentication. Uninstalling removes program files and shortcuts but intentionally leaves the local database under `%LOCALAPPDATA%\PosApp` so business data is not silently deleted.

### Safe Offline Update

1. Download or copy a newer digitally signed `PosApp-<version>-Setup.exe` onto the POS computer.
2. In PosApp, open **Settings → Update & recovery → Choose Update Installer**.
3. PosApp asks Windows to verify the Authenticode signature, requires its publisher to match the installed PosApp publisher, validates the product and complete build version, calculates its SHA-256 digest, and asks for confirmation.
4. Before Setup opens, PosApp creates and validates a SQLite snapshot for the active organization. The legacy profile uses `%LOCALAPPDATA%\PosApp\Backups\Updates`; additional organizations use `%LOCALAPPDATA%\PosApp\Profiles\<profile-id>\Backups\Updates`. Each profile keeps its own successful-version marker, so its first launch after an upgrade receives its own pre-migration backup.
5. Setup upgrades only the program files under `Program Files\PosApp`; the live database stays under the Windows user profile.
6. On the first successful launch, PosApp records the completed update and keeps the recovery backup. If startup fails, the error dialog shows the backup path so the previous app version can be reinstalled and the backup restored from **Settings → Database**.

This protection also runs before database migration when a newer installer is launched directly or a portable `PosApp.exe` is replaced. The updater does not contact a server. Unsigned builds, invalid signatures, unknown publishers, and installers signed by a publisher different from the running app are rejected. An unsigned development copy can still be upgraded by closing PosApp and manually running a trusted signed installer.

## CI / GitHub Actions

The workflow at `.github/workflows/build.yml` triggers on:

Development installers retain the real application version in their filename and Windows metadata, for example `PosApp-2.1.0-dev.27-Setup.exe` with resource version `2.1.0.27`. This allows an installed older release to recognize the rolling development installer as a genuine upgrade. Legacy `PosApp-0.0.0-dev.*-Setup.exe` packages should not be used for in-app updates.

1. **Push to `main`** — builds and uploads the installer, portable exe, and zip as CI artifacts (retained 90 days).
2. **Tag push `v*`** (e.g. `v2.1.0`) — publishes a GitHub Release with `PosApp-<ver>-Setup.exe`, `PosApp-<ver>.exe`, and `PosApp-<ver>.zip` attached.
3. **Manual dispatch** from the Actions tab — optional `version` input; if provided, also creates a release.
4. **Pull request to `main`** — verify-only build (no artifact release).

### To release a new version

```bash
git tag v2.1.0
git push origin v2.1.0
```

The workflow will build the guided installer, portable exe, and zip, then create a public Release at `https://github.com/<you>/<repo>/releases/tag/v2.1.0`.

`.github/workflows/deploy-worker.yml` independently type-checks and tests the Worker, validates all ordered Turso migrations, performs a Wrangler dry run, applies every pending migration to the selected Turso database, verifies schema version 4 and required tables/columns, uploads the required Turso and authentication bindings, including the dedicated password pepper, from protected GitHub secrets, and deploys the selected `development` or `production` environment. After deployment it independently waits for `/api/v1/meta`, `/api/v1/diagnostics`, `/status`, and the root account portal to serve the expected Worker version. It fails unless the Free-plan password verifier, token signing, schema inspection, the complete atomic organization-provisioning batch, and forced rollback verification all pass. Opening the Worker base URL displays the account portal; deployment diagnostics remain available at `/status`. Follow [Cloud setup](docs/CLOUD-SETUP.md) before enabling deployment.

### Browser account portal

Open the deployed Worker origin, for example [https://posapp-cloud-api-development.sweets-4c4.workers.dev/](https://posapp-cloud-api-development.sweets-4c4.workers.dev/). Sign in with an active PosApp organization administrator's online username/email and password. The portal shows exact total and active user counts from Turso and a tenant-scoped user table. **Delete** soft-deletes the selected user, revokes all of that user's sessions and refresh tokens, emits a synchronization tombstone and audit event, and preserves financial history. The backend rejects deletion of the signed-in account and the final active administrator. Select **Create another organization** at any time to sign out and open a fresh organization form. The portal creates a separate browser device identity for every new tenant and remembers non-secret device/profile mappings locally; it never reuses one tenant's device ID for another tenant.

There is deliberately no shared portal master password in GitHub Actions. A global password would bypass organization isolation. GitHub stores only infrastructure credentials and cryptographic keys; organization passwords are created through PosApp or the portal, are hashed with the deployment pepper, and are never stored as plaintext. The portal keeps its rotating session tokens only in browser session storage, renews an expired access token, uses a nonce-restricted content security policy, and revokes the server session on sign-out.

Configure these GitHub Actions repository secrets before building distributable desktop artifacts:

- `POSAPP_CLOUD_API_BASE_URL` — the deployed Worker origin, for example `https://posapp-cloud-api-development.sweets-4c4.workers.dev/`. The workflow rejects API paths, query parameters, fragments, and non-HTTPS public endpoints.
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
- Online passwords use a deployment-secret-peppered PBKDF2-HMAC-SHA-256 verifier with a unique 16-byte salt and a bounded 12,000-round work factor. The dedicated pepper is independent from the JWT and refresh-token secrets and is domain-separated for this use, so a Turso-only database leak does not expose independently crackable verifiers. Persistent and in-memory login throttles limit online guessing. Refresh tokens are stored only as keyed hashes in Turso and rotate after every use.
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

**Database upgrade errors** — do not delete the database. Review `%LOCALAPPDATA%\PosApp\posapp.log`; the active organization’s exact update-recovery folder is shown in **Settings → Update & Recovery**. The legacy profile uses `%LOCALAPPDATA%\PosApp\Backups\Updates`, while additional profiles keep it under their own profile folder. Reinstall the previous PosApp version if necessary, then restore the newest `posapp-before-update-*.db` or `posapp-before-startup-*.db` file for that organization.

**Online setup says synchronization did not finish** — note the diagnostic ID and state/count summary in the dialog, select **Open log folder**, and inspect only matching `attemptId` entries in `%LOCALAPPDATA%\PosApp\Logs\cloud-sync.jsonl`. Queue/error summaries and `requestId` show whether the failure occurred during compatibility validation, push, pull, local apply, or final verification. Do not share credentials, `cloud-session.dat`, the SQLite database, or unrelated log lines.

**Receipt doesn't print** — in Settings, ensure the printer name matches exactly what's shown in `Control Panel → Devices and Printers`. Try the **Test Print** button.

**Barcode scanner doesn't fire** — most USB HID scanners work without configuration. If yours is serial-based, switch to the `SerialBarcodeScanner` driver in `App.xaml.cs`.
