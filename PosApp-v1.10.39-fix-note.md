# PosApp v1.10.39 Fix Note

This build removes SKU from the product workflow.

- Product, inventory, purchase, and POS screens no longer show SKU.
- Product editing no longer accepts SKU.
- POS search now supports product name and barcode only.
- CSV export no longer includes SKU.
- CSV import ignores legacy SKU columns and matches by barcode, then name.
- Sample products are created without SKU values.
- Existing local databases clear legacy product SKU values during schema upgrade so old hidden SKU values cannot interfere with barcode uniqueness.

Legacy database columns are retained for compatibility with older local databases and sync data, but the app no longer uses SKU as a product identifier.
