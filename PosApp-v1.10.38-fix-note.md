# PosApp v1.10.38 Fix Note

## Fixed

- Sales History no longer stays stuck on yesterday's default date range when the app remains open across days.
- The default range refreshes to the latest `Today - 7 days` through `Today` whenever Sales History is opened/refreshed.
- If the user manually changes the From or To date, PosApp keeps that manual range.
- Sales History load failures now write full details to `%LOCALAPPDATA%\PosApp\posapp.log`.

## Validation

- Parsed the edited XAML and project XML successfully.
- Could not run a .NET build locally because the `dotnet` SDK is not installed in this environment.
