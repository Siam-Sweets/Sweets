# PosApp v1.10.10 Fix Note

## Fixed: Release build failure after setup sign-in update

The Windows Release build failed with:

`CS0117: 'SetupService' does not contain a definition for 'SetupCompleteKey'`

`StoreService` still referenced the former `SetupService.SetupCompleteKey` member after setup state was moved into the centralized device-local `SettingSyncPolicy`.

The new-store initialization path now uses `SettingSyncPolicy.SetupCompleteKey`, matching `SetupService`, cloud restore, outbox filtering, and snapshot filtering.

The email/password **Sign in** and **Create organization** setup flows are unchanged.

## Upgrade

Build and install v1.10.10 over the existing installation. Keep `posapp.db`. No SQLite or Turso migration is required.

The Worker API contract is unchanged. Deploying the included v1.10.10 Worker is optional and keeps health/version metadata aligned with the desktop application.
