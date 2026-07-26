# PosApp v1.10.43 Fix Note

## Purchase Posting Cost Concurrency Fix

- Fixed purchase posting showing `Inventory changed while this purchase was posting` while saving a valid purchase.
- Purchase posting now applies the newest applicable purchase cost during the same product update that increases stock.
- Zero-cost purchase lines remain valid and update the product Cost to `0` when that purchase is the newest applicable purchase.
- Added regression coverage for posting a zero-cost purchase and confirming stock and product cost are updated.
