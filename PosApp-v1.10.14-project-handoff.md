# PosApp Project Handoff Summary (v1.10.14)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.14-stale-conflict-reconciliation.zip`
- **Continue versioning from:** **v1.10.14**

## v1.10.14 Change

- Fixed the `Pending: 0, Conflicts: 22` state caused by own-device revisions completing without closing earlier conflict diagnostics.
- Accepted pushes, own-device pulls, and successfully reapplied cloud changes now close superseded conflicts immediately.
- Existing installations reconcile stale rows on status refresh or synchronization.
- Automatic reconciliation requires both an empty per-entity outbox and proof that local state has reached the reported cloud revision.
- Genuine concurrent edits remain unresolved for explicit review.

## Preserved Behavior

- v1.10.13 bounded Turso push pipelines remain included.
- v1.10.12 cashier-login scrolling and idempotent store-default creation remain included.
- Existing-account setup, snapshots, multi-store isolation, localization, reporting, printing, and role permissions remain intact.
- Local SQLite remains authoritative for checkout.

## Data and Validation

- No SQLite schema migration is required.
- No Turso schema migration is required.
- Worker syntax, smoke tests, version consistency, XAML/XML parsing, localization parity, workflow parsing, and ZIP integrity were checked.
- The .NET SDK is unavailable in this workspace, so WPF compilation must be confirmed by GitHub Actions/Windows.
