# PosApp v1.10.9 Fix Note

## Added: existing-account sign-in during first-run setup

The first-run window now provides two online onboarding paths:

- **Sign in** accepts the email and password for an existing PosApp Online owner account, registers the Windows device, creates a local safety backup, and restores the latest complete organization snapshot.
- **Create organization** creates the owner account, first store, and local administrator login, then uploads the complete initial store snapshot.

Setup is marked complete only after the full restore or initial upload succeeds. Interrupted new-organization uploads can be resumed safely.

Device-local `app:`, `cloud:`, and `device:` settings are excluded from snapshots and incremental synchronization. The old seeded `cashier` / `1111` credential is removed before a new organization is uploaded.

## Upgrade

Build and install v1.10.9 over the existing installation. Keep `posapp.db`. No SQLite or Turso migration is required.

The Worker API contract is unchanged, but deploying the included v1.10.9 Worker keeps the health/version response aligned with the desktop application.
