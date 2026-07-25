# PosApp v1.10.31 Fix Note

## Fixed

- Fixed account creation failing with `SQLite Error 19: UNIQUE constraint failed: Settings.StoreId, Settings.Key`.
- Settings writes now update the existing row for the current store/key instead of adding a duplicate.
- Pending or persisted duplicate setting rows are cleaned before saving, making online setup retry-safe.

## Validation

- Checked edited C# and XML files for basic parse/version consistency.
- Local compilation was not run in this workspace because the .NET SDK is not installed.
