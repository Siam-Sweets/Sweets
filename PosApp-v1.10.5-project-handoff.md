# PosApp Project Handoff Summary (v1.10.5)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.5-dark-mode-surface-fix.zip`
- **Continue versioning from:** **v1.10.5**

## v1.10.5 Changes

- Added a global WPF window-theme helper for dark/light surface reapplication.
- Applied theme refresh to already-open windows when appearance settings change.
- Themed programmatically created dialogs so their client areas no longer remain white in Dark mode.
- Requested immersive dark Windows title bars for supported Windows 10/11 builds.
- Dark mode cleanup covers management dialogs such as register, purchases, suppliers, and category windows.
- No SQLite or Turso migration is required.
- No image upload, storage, or synchronization was added.

## Validation Boundary

- Source-level review, version consistency, changelog/docs update, and ZIP integrity passed.
- Windows compilation and live UI verification still require a Windows build/test run.
