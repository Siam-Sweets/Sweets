# PosApp v1.10.14 Validation Notes

## Completed in this environment

- Traced the reported `Pending: 0, Conflicts: 22` state to conflict records that survived after a matching own-device pull removed the last outbox row.
- Added immediate conflict closure after accepted pushes, own-device pulls, and successful application of a previously quarantined cloud revision.
- Rebased and unblocked later queued edits after an own-device pull confirms the earlier cloud revision.
- Added an upgrade-time reconciliation sweep that requires no queued edit and a local cloud revision at least as new as the conflict before closing it.
- Kept lower-revision, unsupported-entity, prohibited ledger-delete, and still-pending conflicts unresolved for explicit review.
- Preserved conflict audit rows by marking them resolved rather than deleting them.
- Added `tests/PosApp.SyncRegression`, an executable SQLite regression wired into the Windows build workflow.
- Mapped the production Worker stack to the per-change `SELECT last_insert_rowid()` database round trip.
- Replaced per-record Turso HTTP calls in `/v1/sync/push` with bounded lookup and write pipelines under one transaction baton.
- Preserved atomic conflict handling, idempotency, cursor ordering, and rollback behavior.
- Added duplicate change-ID validation and stable Turso network/statement diagnostics.
- Added a 250-change sync regression that completes in no more than ten Turso HTTP requests, below Cloudflare's 50-external-subrequest Free-plan limit.
- Executed the new `sync_changes` → `sync_idempotency` cursor statement against SQLite successfully.
- Produced a successful Wrangler 4.81.0 production dry-run bundle.
- Added a dedicated vertical scroll viewport to the cashier login form for mouse wheel, touchpad, touch, and visible-scrollbar navigation.
- Made the login window resizable and constrained it to the available Windows work area.
- Made new-store default seeding idempotent across administrators, categories, taxes, discounts, and settings.
- Used `IgnoreQueryFilters()` with the explicit destination store ID so existing destination categories are detected even when another store is active.
- Preserved the existing store-creation transaction and invalidated the new store's settings cache only after commit.
- Fixed existing-account restore failing with `Cloud snapshot capture metadata does not match its backup set`.
- Normalized new snapshot capture timestamps to the millisecond precision shared by .NET and Cloudflare.
- Made restore compatible with already-uploaded snapshots whose payload retains additional .NET fractional-second digits.
- Added Worker-side rejection for genuinely mismatched envelope/payload capture timestamps.
- Added a Worker regression test covering valid precision normalization and invalid capture-time mismatch.
- Fixed the `CS0117` Release build error by replacing the stale `SetupService.SetupCompleteKey` reference in `StoreService` with `SettingSyncPolicy.SetupCompleteKey`.
- Confirmed no source references to `SetupService.SetupCompleteKey` remain.
- Added theme-aware **Sign in** and **Create organization** tabs to first-run setup.
- Added existing-owner email/password authentication followed by complete cloud snapshot restore.
- Added two-phase new-organization setup: local preparation, cloud owner creation, full snapshot upload, then device-local completion.
- Added safe resume behavior for an interrupted initial snapshot upload.
- Excluded `app:`, `cloud:`, and `device:` settings from outbox capture, snapshot upload, and snapshot restore.
- Added a device-local completion marker after successful existing-account restore.
- Removed the seeded shared cashier credential before a new organization snapshot is uploaded.
- Made `POSAPP_CLOUD_API_URL` mandatory for Release/GitHub Actions builds so an unusable online-only installer cannot be published.
- Confirmed every setup XAML event references a matching code-behind handler.
- Parsed all 93 C# source files with the C# syntax grammar without an error node.
- Parsed all project XAML/XML files successfully.
- Parsed both GitHub Actions workflow YAML files successfully.
- Confirmed English and Bengali localization keys are unique and remain in parity.
- Ran the Cloud Worker JavaScript syntax check and smoke suite successfully.
- Updated application, installer, Worker, workflow, README, changelog, fix note, deployment guide, and handoff markers to 1.10.14.
- Confirmed no SQLite or Turso schema migration is required.

## Not available in this environment

- The .NET SDK is not installed in this Linux workspace, so the WPF solution could not be compiled locally.
- For the same reason, the new SQLite sync regression could not execute locally; it is a required step in the Windows build workflow.
- Live WPF rendering, GitHub Actions compilation, and Windows installer execution still require the Windows CI/test run.
