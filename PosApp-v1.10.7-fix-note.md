# PosApp v1.10.7 Fix Note

## Fixed: checkout stock-ledger reference failure

Stock-tracked checkout could fail with:

```text
SQLite Error 19: Stock transaction contains an invalid reference
```

The sale and sale lines were saved first, but the second ledger phase still trusted in-memory numeric IDs. v1.10.7 resolves the persisted sale and sale-item rows by permanent `SyncId`, validates the store/product/user relationships, and only then inserts immutable ledger rows.

The SQLite integrity guards remain enabled. Their messages now identify the exact invalid reference type.

## Upgrade

Install v1.10.7 over the existing installation and keep `posapp.db`. Startup recreates the corrected triggers automatically. No SQLite or Turso migration is required. Worker redeployment is optional because the cloud protocol did not change.
