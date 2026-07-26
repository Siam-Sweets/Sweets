# PosApp v1.10.35 Fix Note

## Fixed

- Hardened new-store creation against `SQLite Error 19: UNIQUE constraint failed: Categories.StoreId, Categories.Name`.
- Default categories are now merged/upserted at the SQLite level instead of queued as duplicate tracked EF inserts.
- Store save errors now write full technical details to the PosApp app log.

## App Log

If a store save still fails, the popup now shows the log path. The default path is:

`%LOCALAPPDATA%\PosApp\posapp.log`

## Validation

- Parsed the edited project XML successfully.
- Could not run a .NET build locally because the `dotnet` SDK is not installed in this environment.
