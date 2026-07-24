# PosApp v1.10.8 Fix Note

## Fixed: Stock Transfers showed white tab surfaces in Dark mode

The Stock Transfers page no longer uses Windows default tab chrome. Both **Transfers** and **Inventory by Store** now use PosApp dynamic theme brushes for tab headers, borders, content panels, hover state, and selected state.

## Fixed: store selector displayed the record object

The inventory store filter now displays the actual store name instead of text such as `StoreFilterOption { Id = 0, ... }`.

## Upgrade

Install v1.10.8 over the current installation. Keep `posapp.db`; no database migration or reset is required. Cloud Worker redeployment is optional because the cloud protocol did not change.
