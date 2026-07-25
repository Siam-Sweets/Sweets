# PosApp v1.10.22 Fix Note

## Fixed

- Fixed date text clipping in the Purchases & Suppliers date filters.
- Increased DatePicker width so dates like `7/25/2026` remain fully visible beside the calendar icon.
- Updated Reports and Dashboard date filters with the same safer width.
- Purchases and Reports filter rows can now wrap instead of cutting off controls on smaller or scaled displays.

## Validation

- Checked the edited XAML/XML files for well-formed markup.
- Local compilation was not run in this workspace because the .NET SDK is not installed.
