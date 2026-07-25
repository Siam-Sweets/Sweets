# PosApp v1.10.14 Phone-Only Deployment Guide

1. Upload the v1.10.14 source to the GitHub repository.
2. Keep the existing Cloudflare, Turso, JWT, registration-key, and `POSAPP_CLOUD_API_URL` values unchanged.
3. Run **Actions → Deploy PosApp Cloud**.
4. Confirm `<worker-url>/v1/health` returns `ok: true` and version `1.10.14`.
5. Run **Actions → Build PosApp** with version `1.10.14`.
6. Install the new Windows build over the existing installation.
7. Open **Settings → Cloud** and refresh the page or press **Sync Now** once.

The stale `Pending: 0, Conflicts: 22` records will be marked resolved when their cloud revisions are already present locally. Any conflict that remains is a genuine pending or divergent change and stays in Sync Center for review.

Do not delete the Turso database or local `posapp.db`. No SQLite or Turso migration is required.
