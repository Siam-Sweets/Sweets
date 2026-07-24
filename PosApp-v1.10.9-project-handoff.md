# PosApp Project Handoff Summary (v1.10.9)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.9-setup-sign-in.zip`
- **Continue versioning from:** **v1.10.9**

## v1.10.9 Changes

- First-run onboarding is online-only.
- The setup window has **Sign in** and **Create organization** tabs.
- Existing accounts authenticate with email/password and restore the latest complete cloud snapshot before onboarding completes.
- New organizations create the owner account, prepare local store/admin data, upload a full snapshot, and only then write the completion marker.
- An interrupted new-organization snapshot upload can resume with the protected local credential.
- Setup and credential markers remain device-local and are excluded from all synchronization paths.
- The shared seeded cashier credential is removed before new-organization upload.
- Release builds require the `POSAPP_CLOUD_API_URL` GitHub Actions variable.
- English and Bengali localization remain in parity.
- Application, installer, Worker, workflow, and documentation versions are 1.10.9.

## Data and Deployment

- No SQLite schema migration is required.
- No Turso schema migration is required.
- Existing account restore makes a local safety backup before replacing the unused first-run cache.
- The existing cloud endpoints remain compatible; Worker redeployment is optional but recommended for aligned health metadata.

## Validation Boundary

- XAML/XML parsing, XAML event-handler checks, localization parity, Worker syntax/smoke tests, version checks, and ZIP integrity were validated.
- The .NET SDK is unavailable in this workspace, so WPF compilation, live Windows rendering, and installer execution must run in GitHub Actions/Windows.
