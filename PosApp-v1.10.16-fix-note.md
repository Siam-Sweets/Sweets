# PosApp v1.10.16 Fix Note

## Removed: Stock Transfers

- Removed the Stock Transfers navigation entry and page from the Windows app.
- Removed transfer creation, dispatch, receive, cancellation, and transfer-specific inventory UI.
- Removed the stock-transfer application service, interface, and draft DTOs.
- Renamed shared store-filter localization keys used by Dashboard and Reports so those screens no longer depend on transfer-specific resource names.
- Kept legacy stock-transfer database entities/schema and cloud-sync compatibility internally so existing installations and older synchronized data are not broken by this update.
