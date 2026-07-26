# PosApp v1.10.36 Fix Note

## Fixed

- Fixed sample products not loading after online account creation.
- Sample products are now seeded for the exact setup store, even if the stored current-store selection is stale.
- Setup now checks that products exist when the sample-products option is enabled.
- Online account setup failures now write full details to `%LOCALAPPDATA%\PosApp\posapp.log`.

## Validation

- Parsed the edited project XML successfully.
- Could not run a .NET build locally because the `dotnet` SDK is not installed in this environment.
