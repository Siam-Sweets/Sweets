# PosApp v1.10.12 Phone-Only Deployment Guide

1. Upload the v1.10.12 source to the GitHub repository.
2. Keep the existing `POSAPP_CLOUD_API_URL` repository variable and cloud secrets.
3. Run **Build PosApp** with version `1.10.12`.
4. Download and install the generated Windows setup artifact.
5. Confirm that the cashier login card scrolls when its content does not fit.
6. Retry creating or saving the affected store.

The Worker API and database schemas are unchanged, so Worker redeployment is optional for this desktop/store-service fix.

Do not delete `posapp.db`. No SQLite or Turso migration is required.
