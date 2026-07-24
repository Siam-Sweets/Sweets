# PosApp Project Handoff Summary (v1.10.1)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.1-readonly-checkbox-binding-fix.zip`
- **Continue versioning from:** **v1.10.1**

## Latest Fix

- Fixed Store Management dispatcher exceptions caused by `DataGridCheckBoxColumn` attempting TwoWay bindings against read-only `StoreRow.IsActive` and `StoreRow.IsCurrent` properties.
- Display-only checkbox columns now explicitly use `Mode=OneWay` and are read-only.
- Applied the same preventive correction to Sync Center device status and cross-store low-stock columns.
- Editable transfer-item selection remains unchanged.

## Compatibility

- No database schema changes.
- No Turso migration required.
- No cloud synchronization protocol changes.
- No image upload or image synchronization.
- Existing v1.10.0 databases can be used directly.

## Validation Status

- XAML/XML parsing passed.
- Read-only binding regression checks passed.
- Worker syntax and smoke tests passed.
- Windows compilation and installer execution still require GitHub Actions or a Windows .NET 8 environment.

## Development Rules

1. Always modify the latest baseline ZIP.
2. Preserve all existing features and prior fixes.
3. Bump every relevant version for each code or workflow change.
4. Update README and CHANGELOG.
5. Validate XAML, localization, project files, cloud Worker, and migrations where applicable.
6. Do not claim a Windows build passed unless GitHub Actions confirms it.
