# PosApp v1.10.10 Validation Notes

## Completed in this environment

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
- Parsed all project XAML/XML files successfully.
- Confirmed English and Bengali localization keys are unique and remain in parity.
- Ran the Cloud Worker JavaScript syntax check and smoke suite successfully.
- Updated application, installer, Worker, workflow, README, changelog, fix note, deployment guide, and handoff markers to 1.10.10.
- Confirmed no SQLite or Turso schema migration is required.

## Not available in this environment

- The .NET SDK is not installed in this Linux workspace, so the WPF solution could not be compiled locally.
- Live WPF rendering, GitHub Actions compilation, and Windows installer execution still require the Windows CI/test run.
