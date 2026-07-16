# PosApp - Local Point of Sale (WPF / .NET 8)

A feature-rich, **local-only** Point of Sale desktop application for Windows, built with C# and WPF on .NET 8. All data stays in a local SQLite database — no cloud, no server, no internet required.

## Offline Boundary

Version 1.2 contains no runtime HTTP client, telemetry, cloud sync, hosted API, remote login, email, or SMS integration. Checkout, purchases, register sessions, reports, CSV transfer, backups, and restores all read or write local files and the local SQLite database only. Internet access is needed only by a developer when restoring NuGet packages or by GitHub Actions when building a release.

This is an original POS implementation inspired by the publicly known feature set of POS systems in general (sales, inventory, customers, receipts, hardware integration, reports, etc.). The codebase, UI, and architecture are written from scratch.

## Features

| Module              | Highlights |
|---------------------|------------|
| **POS / Checkout**  | Product grid with category filter, barcode scan, weighted items, cart with qty +/-, customer attach, suspend/recall, F10 advanced payment, F12 exact cash, multi-tender checkout with on-screen number pad, quick-cash suggestions, automatic change calculation |
| **Products & Inventory** | Full CRUD for products and categories, SKU/barcode tracking, stock levels, low-stock alerts, stock adjustments, physical inventory counts, stock movement history, stock valuation at cost, CSV catalog import/export |
| **Purchases & Suppliers** | Supplier directory, posted purchase documents, supplier invoice references, multi-line receiving, tax totals, automatic stock increases, moving-average cost, and purchase history |
| **Cash Register** | Opening float, paid-in / paid-out movements with reasons, live expected cash, payment breakdown, printable X reports, manager-only close, counted cash, variance, and final Z reports |
| **Customers**       | Contact records, purchase history, and search by name/phone/email |
| **Sales / Transactions** | Filter by date and status, view receipt detail, reprint, void (restores stock), refund tracking, suspend/recall, CSV export |
| **Users & Roles**   | PIN-based login, three roles (Cashier, Manager, Admin) with sidebar access gated by role, last-admin protection, PIN reset |
| **Reports & Dashboard** | KPI cards (gross, profit, tax, transactions), top products, sales by category, daily trend, payment breakdown, date-range filters (today/week/month/custom) |
| **Taxes & Discounts** | Per-product tax rate, percentage and fixed discounts, promo codes, cart-level discounts |
| **Receipt Printer** | ESC/POS thermal printer via raw spooler (58/80mm) AND fallback Windows PrintDocument path for any printer |
| **Hardware**        | Barcode scanner (HID keyboard + serial), cash drawer (serial DTR pulse + printer DK port), weighing scale (serial) — all gracefully degrade to "no-op" if absent |
| **Data Safety**     | Consistent SQLite backups on startup/exit, manual backup, retention control, validated staged restore, and automatic pre-restore safety copy |

## Tech Stack

- **.NET 8 (LTS)** + **WPF** (XAML and code-behind)
- **EF Core 8** + **SQLite** (single-file local DB at `%LOCALAPPDATA%\PosApp\posapp.db`)
- **System.IO.Ports** for serial hardware (drawer, scale, serial scanner)
- **HidSharp** for HID scanner discovery
- Original UI styling (flat, modern, Material-inspired) with EN + BN bilingual support

## Project Structure

```
posapp/
├── .github/workflows/build.yml     # CI: build single-file exe on push/tag/manual
├── PosApp.sln
├── src/
│   ├── PosApp.Core/                # Entities, enums, interfaces, DTOs
│   ├── PosApp.Data/                # AppDbContext, EF Core, seeder, repositories
│   ├── PosApp.Services/            # Business logic (Auth, Inventory, Sales, Customers, Reports, Settings)
│   ├── PosApp.Hardware/            # Printer, scanner, drawer, scale drivers + HardwareService
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

On first run, the app creates `%LOCALAPPDATA%\PosApp\posapp.db` and seeds:
- Admin user: `admin` / PIN `1234`
- Cashier user: `cashier` / PIN `1111`
- 6 default categories (Beverages, Snacks, Groceries, Household, Personal Care, Produce)
- 15 sample products (mix of fixed-price and weighted)
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

The output is a single `PosApp.exe` (~150 MB) that runs on any Windows 10/11 x64 machine — no .NET install required. Drop it on a USB stick, run it on the POS terminal.

## CI / GitHub Actions

The workflow at `.github/workflows/build.yml` triggers on:

1. **Push to `main`** — builds and uploads the exe as a CI artifact (retained 30 days).
2. **Tag push `v*`** (e.g. `v1.0.0`) — builds and publishes a GitHub Release with `PosApp-<ver>.exe` and `PosApp-<ver>.zip` attached.
3. **Manual dispatch** from the Actions tab — optional `version` input; if provided, also creates a release.
4. **Pull request to `main`** — verify-only build (no artifact release).

### To release a new version

```bash
git tag v1.0.0
git push origin v1.0.0
```

The workflow will build the exe, zip it, and create a public Release at `https://github.com/<you>/<repo>/releases/tag/v1.0.0`.

## Configuration

All settings persist in the SQLite database and are editable from the in-app **Settings** screen:

- **Store info**: name, address, phone, email, tax ID, currency symbol, receipt footer
- **Hardware**: receipt printer name (dropdown of installed Windows printers), cash drawer COM port, scale COM port, auto-print receipt, auto-open drawer on cash sale
- **Language**: English (default) or বাংলা (Bengali) — switches live
- **Theme**: Light / Dark
- **Data safety**: automatic backups on startup and/or exit, retention count, manual backup, validated restore, and backup-folder access

You can also edit hardware defaults by deleting the `store:config` row in the `Settings` table (the app re-creates it with defaults on next run).

## Hardware Wiring Notes

| Device | Connection | Notes |
|--------|------------|-------|
| Barcode scanner | USB (HID keyboard mode) | Most USB scanners work out of the box — they "type" the code + Enter. The app detects fast key sequences and fires the onScan callback. |
| Receipt printer | USB (Windows printer driver) | Set the printer name in Settings. Raw ESC/POS bytes are sent via the Windows spooler (Winspool `WritePrinter`). Falls back to GDI printing if the printer is not ESC/POS-compatible. |
| Cash drawer | Either (a) RJ11 wired to printer's DK port, or (b) direct COM port | For (a), use the printer-kick ESC/POS command. For (b), set the COM port in Settings — the app pulses DTR. |
| Weighing scale | Serial COM port (RS232) | Default protocol: send `R`, read response like `S+0001.235kg`. Override `SerialWeighingScale.ParseWeight` for your scale's protocol. |

When any device is missing, the app silently degrades to a no-op so checkout never blocks.

## Security Notes

- Passwords are hashed with SHA-256 + 16-byte random salt (PINs are short, so consider migrating to PBKDF2/Argon2 for production use with stronger passwords).
- The SQLite database lives under `%LOCALAPPDATA%\PosApp\` — back it up regularly. There is no cloud sync.
- Role-based access:
- **Cashier**: POS, Register, and Sales
- **Manager**: + Products, Inventory, Purchases, Customers, Reports, and register close/Z report
  - **Admin**: + Users, Settings

## Future Roadmap

- Multi-terminal sync (would need a server or shared DB)
- Cloud backup
- Email/SMS receipts
- Purchase orders and supplier stock returns (posted purchase receiving is included)
- Loyalty tier rules
- Barcode label printing

## License

This project is provided as-is for your personal/commercial use. The architecture, code, and UI are original works. "Aronium" is a trademark of its respective owners; this project is not affiliated with or endorsed by them and does not use any of their proprietary code or assets.

## Troubleshooting

**Build fails on `dotnet restore`** — make sure you have the .NET 8 SDK installed: `dotnet --version` should report `8.x.x`.

**Database upgrade errors** — do not delete the database. Copy `%LOCALAPPDATA%\PosApp\posapp.db` to a safe location, review `%LOCALAPPDATA%\PosApp\posapp.log`, and use **Settings → Data Safety & Backups → Restore** if you have a known-good backup. Version 1.1 upgrades older databases in place.

**Receipt doesn't print** — in Settings, ensure the printer name matches exactly what's shown in `Control Panel → Devices and Printers`. Try the **Test Print** button.

**Barcode scanner doesn't fire** — most USB HID scanners work without configuration. If yours is serial-based, switch to the `SerialBarcodeScanner` driver in `App.xaml.cs`.

**Cash drawer won't open** — confirm the COM port in Settings matches Device Manager. If the drawer is wired to the printer's DK port, replace `NullCashDrawer` with `PrinterCashDrawer` in `App.xaml.cs`.
