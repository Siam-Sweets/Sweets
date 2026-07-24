# PosApp v1.10.6 Validation Notes

## Completed in this environment

- Replaced the fixed Store Details content region with a vertically scrollable form.
- Made the Store Details window resizable and added minimum dimensions for small/high-DPI displays.
- Kept Save and Cancel outside the scrolling region so they remain visible.
- Added vertical scrolling to the multiline Address field.
- Retained the v1.10.5 global dark-window and dark-title-bar fixes.
- Updated project, installer, Worker, workflow, README, changelog, fix note, and handoff version markers to 1.10.6.
- Parsed all WPF XAML/XML files successfully.
- Ran the cloud Worker syntax and smoke test suite successfully.
- Confirmed no SQLite or Turso schema migration is required.
- Confirmed cloud image exclusion was not changed.

## Not available in this environment

- The Windows WPF runtime is unavailable, so live mouse-wheel/touchpad scrolling and the compiled installer still require GitHub Actions and a Windows test run.
