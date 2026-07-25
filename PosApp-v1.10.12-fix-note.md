# PosApp v1.10.12 Fix Note

## Fixed: cashier-login scrolling

The login form could be clipped at Windows display scaling or on a smaller work area, while mouse-wheel scrolling did nothing.

The form card now has a dedicated vertical scroll viewport supporting:

- A visible scrollbar when content does not fit.
- Mouse-wheel and touchpad scrolling.
- Touch panning.
- A resizable window constrained to the usable Windows work area.

## Fixed: duplicate default categories during store creation

Creating a store could fail with:

`SQLite Error 19: UNIQUE constraint failed: Categories.StoreId, Categories.Name`

Default-store initialization now checks the exact destination store with query filters disabled and inserts only missing data. Administrator access, categories, taxes, discounts, store configuration, and the local completion setting are all idempotent. Retrying store creation no longer inserts duplicate category names.

Store creation remains transactional, so a failed initialization does not intentionally commit a partial new store.

## Upgrade

Build and install v1.10.12 over the existing installation, then retry the store operation. Keep `posapp.db`; no SQLite or Turso migration is required.

The Worker protocol is unchanged.
