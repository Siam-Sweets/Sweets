# PosApp Project Handoff Summary (v1.10.13)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.13-cloud-sync-push-fix.zip`
- **Continue versioning from:** **v1.10.13**

## v1.10.13 Change

- Fixed Cloudflare Worker HTTP 500 failures from `/v1/sync/push`.
- Mapped the supplied bundled stack exactly to the per-change `SELECT last_insert_rowid()` request.
- Replaced per-record Turso HTTP round trips with bounded lookup and write pipelines.
- Retained one Turso transaction baton across every chunk, preserving atomic commit/rollback behavior.
- Derived idempotency cursors from the inserted `sync_changes` rows inside the same pipeline.
- Added duplicate change-ID validation and improved Turso error diagnostics.
- Added a 250-change regression test capped at ten or fewer database subrequests.

## Preserved Behavior

- v1.10.12 cashier-login scrolling and idempotent store-default creation remain included.
- Existing-account setup, organization creation, snapshot restore, multi-store isolation, localization, reporting, printing, and role permissions remain intact.
- Local SQLite remains authoritative for checkout.

## Data and Validation

- No SQLite schema migration is required.
- No Turso schema migration is required.
- Worker syntax, smoke tests, version consistency, XAML/XML parsing, localization parity, workflow parsing, and ZIP integrity were checked.
- The .NET SDK is unavailable in this workspace, so WPF compilation must be confirmed by GitHub Actions/Windows.
