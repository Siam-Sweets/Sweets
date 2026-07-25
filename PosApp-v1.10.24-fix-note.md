# PosApp v1.10.24 Fix Note

## Fixed

- Fixed Reporting date filters still clipping the last date digit at display scaling.
- Increased DatePicker width to `220px` across Reporting, Dashboard, and Purchases filters.
- Fixed store filter dropdown fallback text so it shows the store name instead of `StoreFilterOption { ... }`.

## Validation

- Checked edited XAML/XML files for well-formed markup.
- Local compilation was not run in this workspace because the .NET SDK is not installed.
