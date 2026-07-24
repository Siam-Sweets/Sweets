# PosApp Project Handoff Summary (v1.10.6)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.6-store-dialog-scroll-fix.zip`
- **Continue versioning from:** **v1.10.6**

## v1.10.6 Changes

- Fixed Store Details fields being clipped under Windows display scaling.
- Added a vertical `ScrollViewer` around the editable form.
- Made the dialog resizable with minimum dimensions.
- Kept Save/Cancel outside the scroll region.
- Added Address-field scrolling.
- Retained all v1.10.5 dark-mode surface and title-bar fixes.
- No SQLite or Turso migration is required.
- No image upload, storage, or synchronization was added.

## Validation Boundary

- XAML/XML parsing, Worker syntax/smoke tests, version consistency, documentation, and ZIP integrity were checked.
- Windows compilation and live scrolling verification still require GitHub Actions and a Windows machine.
