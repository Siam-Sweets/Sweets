# PosApp Project Handoff Summary (v1.10.11)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.11-snapshot-capture-fix.zip`
- **Continue versioning from:** **v1.10.11**

## v1.10.11 Change

- Fixed setup sign-in rejecting valid existing cloud snapshots because .NET and Cloudflare retained different fractional-second precision.
- New uploads use one millisecond-precision capture timestamp in both the envelope and payload.
- Restore compares capture times by their Unix millisecond instant so snapshots uploaded by v1.10.9/v1.10.10 remain usable.
- Worker upload validates that the normalized envelope and payload timestamps match.
- Cryptographic hash, row-count, backup-set, store-ID, schema, and application-version checks remain strict.
- No cloud data reset or snapshot re-upload is required.

## Preserved Behavior

- First-run onboarding remains online-only.
- Existing owners sign in with email/password and restore the complete organization.
- New organizations upload a complete initial snapshot before onboarding completes.
- Setup and credential markers remain device-local.
- Release builds require `POSAPP_CLOUD_API_URL`.

## Data and Validation

- No SQLite schema migration is required.
- No Turso schema migration is required.
- Worker syntax/smoke tests, timestamp regression coverage, XAML/XML parsing, localization parity, version consistency, workflow parsing, and ZIP integrity were checked.
- The .NET SDK is unavailable in this workspace, so WPF compilation must still be confirmed by GitHub Actions/Windows.
