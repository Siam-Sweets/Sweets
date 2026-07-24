# PosApp v1.10.3 Fix Note

## Fixed: register setting ignored for cash sales

`SaleService` previously required an open register whenever the payment included cash, even when **Require an open register before selling** was disabled. The service now enforces the register requirement only when that setting is enabled.

## Fixed: stock checkout rejected by append-only ledger guard

Checkout and item refunds previously inserted stock-ledger rows, saved them, and then edited their sale/item foreign keys. The append-only guard correctly rejected that second update. v1.10.3 first saves the sale/refund and line items, then inserts each ledger row once with its final links. The enclosing database transaction still makes the operation all-or-nothing.

## Upgrade

Install v1.10.3 over the existing installation. Keep `posapp.db`; no database reset or migration is required. Worker redeployment is optional because the cloud protocol did not change.
