# PosApp v1.10.42 Fix Note

## New-Store Discount Duplicate Fix

- Fixed new-store creation failing with `SQLite Error 19: UNIQUE constraint failed: Discounts.StoreId, Discounts.Code`.
- Default discounts are now seeded through guarded SQL upserts by store and discount code.
- Pending duplicate default discount rows are detached before the final store save, so a retry cannot leave duplicate tracked rows that fail the database constraint.
- Added regression coverage for duplicate `SAVE5` discount seeding.
