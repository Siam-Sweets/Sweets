# PosApp v1.9.8 Fresh-Install Startup Fix

## Symptom

A new installation created `posapp.db`, then stopped with:

```text
No active store is available.
```

## Root cause

The EF-created `Stores` table requires `SyncVersion` and `SyncUpdatedAt`. The initial `MAIN` store SQL omitted those columns and used `INSERT OR IGNORE`, so SQLite silently skipped the row.

## Fix

- The initial `MAIN` insert now supplies every required sync column.
- The insert is idempotent and no longer hides constraint failures.
- Startup creates `MAIN` if the store table is empty.
- Startup reactivates the oldest store if every store is inactive.
- Repair writes are excluded from cloud outbox capture until store selection is valid.

## Upgrade

Install v1.9.8 over the existing installation and start PosApp again. Do not delete `posapp.db`; the empty database is repaired automatically.

A Windows compile/build is still verified by GitHub Actions, not by the packaging environment.
