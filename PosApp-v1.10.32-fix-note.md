# PosApp v1.10.32 Fix Note

## Fixed

- Fixed new-store creation still failing with `SQLite Error 19: UNIQUE constraint failed: Categories.StoreId, Categories.Name`.
- Default category seeding now reuses persisted rows first and detaches/removes duplicate pending rows before saving.
- Category matching now follows the database uniqueness rule by comparing names case-insensitively.

## Validation

- Updated the sync regression fixture to cover a persisted lowercase category plus a pending duplicate.
- Checked edited C# and XML files for basic parse/version consistency.
- Local compilation was not run in this workspace because the .NET SDK is not installed.
