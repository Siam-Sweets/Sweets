# PosApp v1.10.13 Phone-Only Deployment Guide

1. Upload the v1.10.13 source to the GitHub repository.
2. Keep the existing Cloudflare, Turso, JWT, registration-key, and `POSAPP_CLOUD_API_URL` values unchanged.
3. Run **Actions → Deploy PosApp Cloud**.
4. Confirm `<worker-url>/v1/health` returns `ok: true` and version `1.10.13`.
5. On the Windows POS, retry **Sync Now**. The failed operation can be retried because its Turso transaction was rolled back.
6. Run **Build PosApp** with version `1.10.13` when you also want the matching desktop/installer version.

The Worker deployment is the required part of this fix. Do not delete the Turso database or local `posapp.db`; no SQLite or Turso migration is required.
