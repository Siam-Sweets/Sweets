# PosApp v1.10.41 Fix Note

## Net Profit Dashboard Label

- Dashboard and Reports now show `Net Profit` instead of `Gross Profit`.
- Profit display bindings now use the `NetProfit` report metric.
- `NetProfit` is calculated as sales after discounts minus cost of goods, excluding collected tax from profit.

## Compatibility

- Existing `GrossProfit` report properties remain as compatibility aliases for older code paths.
