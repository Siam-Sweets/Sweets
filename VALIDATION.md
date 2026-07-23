# PosApp v1.9.8 Validation Notes

- Verified application, assembly, file, informational, installer, Worker, README, and changelog versions at 1.9.8.
- Verified the fresh-database `Stores` insert includes `SyncVersion`, `SyncUpdatedAt`, and `CloudVersion`, which are required by the EF-created schema.
- Simulated a fresh SQLite database and confirmed exactly one active `MAIN` store is created.
- Verified the insert is idempotent and does not create a second store on a repeated schema upgrade.
- Verified `StoreService.InitializeAsync()` repairs both an empty store table and an all-inactive store table while suppressing cloud capture.
- Verified Worker syntax and smoke tests.
- Verified YAML, JSON/JSONC, XML/XAML, and project files parse successfully.
- No Turso schema, synchronization protocol, cloud deployment, desktop layout, or image-handling changes were introduced.
- Windows restore/build remains unverified until GitHub Actions runs.
