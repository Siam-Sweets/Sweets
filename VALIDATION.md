# PosApp v1.9.0 Validation Notes

Validated in the available Linux environment on 2026-07-22:

- Parsed all WPF XAML and localization dictionaries successfully.
- Verified English/Bengali resource parity: 605 keys in each dictionary, with no missing transfer resources.
- Checked referenced XAML event handlers and structural delimiters across 88 C# files.
- Verified application, assembly, file, informational, installer, Worker, README, and changelog versions at 1.9.0.
- Verified the transfer service, dependency injection, navigation, role gating, store filters, report overloads, and status-aware transfer actions are wired.
- Simulated the additive v1.8.0-to-v1.9.0 SQLite upgrade, preserving an existing stock-ledger row while adding transfer tables, indexes, and stock-transaction links.
- Simulated Draft → Dispatch → Receive inventory movement and confirmed balanced linked ledger quantities, source/destination stock totals, foreign-key integrity, and per-store transfer-number uniqueness.
- Ran `npm run check --prefix cloud/worker` successfully.
- Worker smoke coverage passed for health, sign-up, snapshots, sequential edits, idempotent replay, revision conflicts, cursor pull, device listing, `StockTransfer`, and `StockTransferItem` changes.
- Verified snapshot schema version 4, transfer restore ordering, atomic remote-change transactions, and out-of-order destination store/category/product materialization by sync ID.
- Confirmed serializers exclude `ImagePath`; transfer-created destination products explicitly use `ImagePath = null`, and no image-upload/storage endpoint was added.

Not validated here:

- Windows WPF compilation, installer compilation, or runtime UI execution, because a Windows .NET 8 build environment was unavailable.
- Live Cloudflare Worker/Turso deployment, because no user cloud credentials were provided.

The GitHub Actions Windows workflow remains the authoritative build check.
