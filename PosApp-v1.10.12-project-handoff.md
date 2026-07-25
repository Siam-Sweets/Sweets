# PosApp Project Handoff Summary (v1.10.12)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.12-login-scroll-category-fix.zip`
- **Continue versioning from:** **v1.10.12**

## v1.10.12 Changes

- Cashier login uses a dedicated vertical scroll viewport inside the form card.
- Mouse wheel, touchpad, touch panning, and a visible scrollbar keep all login controls reachable.
- Login is resizable and automatically constrained to the Windows work area.
- New-store defaults are seeded idempotently.
- Destination users, categories, taxes, discounts, and settings are queried with `IgnoreQueryFilters()` and the explicit store ID.
- Existing default categories are reused, preventing `Categories.StoreId, Categories.Name` unique-constraint failures.
- Store creation remains atomic and invalidates the new store's settings cache after commit.

## Preserved Behavior

- Online-only first-run sign-in and organization creation remain unchanged.
- Existing accounts restore complete cloud snapshots.
- Snapshot capture-time compatibility from v1.10.11 remains included.
- Cloud synchronization, multi-store isolation, localization, reporting, printing, and role permissions remain intact.

## Data and Validation

- No SQLite schema migration is required.
- No Turso schema migration is required.
- XAML/XML parsing, login handler/resource checks, default-definition uniqueness, localization parity, Worker smoke tests, version consistency, workflow parsing, and ZIP integrity were checked.
- The .NET SDK is unavailable in this workspace, so WPF compilation must be confirmed by GitHub Actions/Windows.
