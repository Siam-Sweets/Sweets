# PosApp v1.10.1 Fix Note

## Fixed

Opening or refreshing **Store Management** could raise WPF dispatcher exceptions:

- `A TwoWay or OneWayToSource binding cannot work on the read-only property 'IsActive'`
- `A TwoWay or OneWayToSource binding cannot work on the read-only property 'IsCurrent'`

The `DataGridCheckBoxColumn` bindings defaulted to a source-updating mode even though `StoreRow.IsActive` and `StoreRow.IsCurrent` are display-only properties.

## Correction

- Set the Stores grid checkbox bindings to `Mode=OneWay`.
- Marked the display-only checkbox columns read-only.
- Applied the same preventive correction to:
  - Sync Center device `IsCurrent` and `IsRevoked` columns.
  - Cross-store inventory `IsLowStock` column.
- Preserved the editable transfer-item selection checkbox.

## Upgrade

Install v1.10.1 over v1.10.0. No database migration, cloud schema migration, or data reset is required. Existing `posapp.db` data is preserved.

## Validation

- All 29 XAML files parsed successfully as XML.
- All display-only checkbox bindings were checked for explicit `Mode=OneWay`.
- The editable transfer-selection checkbox remains source-updating.
- Worker JavaScript syntax and smoke tests passed.
- A Windows .NET build was not run in this environment because the .NET SDK is unavailable.
