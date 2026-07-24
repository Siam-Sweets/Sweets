# PosApp v1.10.1 Validation Notes

## Completed in this environment

- Verified application, assembly, file, informational, installer, Worker, README, workflow examples, and changelog versions at 1.10.1.
- Ran the Cloud Worker JavaScript syntax check and the v1.10.1 atomic-sync smoke suite.
- Parsed every project, XML, XAML, RESX, JSON/JSONC, and GitHub Actions YAML file.
- Verified English/Bengali resource-key parity and XAML event-handler references.
- Ran a C# lexical-structure scan over all source files.
- Parsed the complete Turso schema and simulated the v1.10.1 additive migration in SQLite.
- Simulated SQLite integrity triggers for append-only stock history, protected product deletion, transfer references, and the shared SKU/barcode namespace.
- Checked the 42-item audit against the implementation and recorded the mapping in `PosApp-v1.10.1-bug-fix-report.md`.
- Verified cloud payload serialization still excludes `ImagePath`; no image upload or image synchronization was added.
- Verified the release ZIP opens and every packaged file matches its generated SHA-256 manifest.

## Not available in this environment

- The .NET SDK, MSBuild, and Windows WPF runtime are unavailable, so `dotnet restore`, Windows compilation, installer creation, and live UI execution were not claimed.
- Live Cloudflare/Turso authentication and real concurrent two-device operation require the owner's deployed services and Windows devices.

Run GitHub Actions and real-device regression tests before production use.


## v1.10.1 read-only binding validation

- Parsed all WPF XAML files as XML.
- Confirmed display-only `DataGridCheckBoxColumn` bindings for `IsActive`, `IsCurrent`, `IsRevoked`, and `IsLowStock` use `Mode=OneWay`.
- Confirmed the editable transfer-selection checkbox remains TwoWay-capable.
- A Windows .NET build was not run in this environment because the .NET SDK is unavailable.
