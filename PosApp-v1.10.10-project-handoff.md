# PosApp Project Handoff Summary (v1.10.10)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.10-setup-sign-in-build-fix.zip`
- **Continue versioning from:** **v1.10.10**

## v1.10.10 Change

- Fixed the Release build error reported at `StoreService.cs(208,66)`.
- Replaced the removed `SetupService.SetupCompleteKey` reference with `SettingSyncPolicy.SetupCompleteKey`.
- Kept the v1.10.9 online-only setup flow unchanged:
  - Existing accounts sign in with email/password and restore the latest complete cloud snapshot.
  - New organizations create the owner/store, upload a complete initial snapshot, and only then complete onboarding.
  - Setup and credential markers remain device-local.
- Application, installer, Worker, workflow, and current documentation versions are 1.10.10.

## Data and Deployment

- No SQLite schema migration is required.
- No Turso schema migration is required.
- The cloud API contract is unchanged.
- Release builds still require the `POSAPP_CLOUD_API_URL` GitHub Actions variable.

## Validation Boundary

- Stale setup-key references, XAML/XML parsing, localization parity, Worker syntax/smoke tests, version consistency, workflow parsing, and ZIP integrity were checked.
- The .NET SDK is unavailable in this workspace, so the corrected WPF compilation must be confirmed by GitHub Actions/Windows.
