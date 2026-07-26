# PosApp v1.10.40 Fix Note

This build updates product cost behavior in Purchases & Suppliers.

- Purchase unit cost can be 0.
- Posting a purchase sets the product Cost to the latest completed purchase cost.
- Product Cost no longer uses weighted-average cost for Purchases & Suppliers.
- Editing a posted purchase line's Unit cost recalculates that purchase and updates the product Cost when that purchase line is the newest applicable purchase for the product.
- CSV stock receipts also stop using weighted-average cost; when a CSV receipt includes CostPrice, that value becomes the product's current cost.
