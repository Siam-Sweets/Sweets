# PosApp v1.10.29 Fix Note

## Fixed

- Fixed the Release build error `MC3072` in `Controls.xaml`.
- Removed unsupported `CalendarButtonStyle` and `CalendarDayButtonStyle` attributes from `CalendarItem`.
- The dark calendar popup styling remains applied through the parent `Calendar`.

## Validation

- Checked edited XAML/XML files for well-formed markup.
- Searched the source to confirm the unsupported `CalendarItem` attributes were removed.
- Local compilation was not run in this workspace because the .NET SDK is not installed.
