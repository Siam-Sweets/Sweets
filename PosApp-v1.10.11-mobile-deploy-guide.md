# PosApp v1.10.11 Phone-Only Deployment Guide

1. Upload the v1.10.11 source to the GitHub repository.
2. Keep the existing Cloudflare/Turso secrets required by **Deploy PosApp Cloud**.
3. Run **Deploy PosApp Cloud** and wait for `/v1/health` to report version `1.10.11`.
4. Keep the deployed HTTPS Worker address in the `POSAPP_CLOUD_API_URL` repository variable.
5. Run **Build PosApp** with version `1.10.11`.
6. Download and install the generated Windows setup artifact.
7. Retry **Sign in and sync everything** with the existing owner email and password.

Do not delete the existing Turso database or snapshots. v1.10.11 restores snapshots already stored with millisecond-normalized Worker capture metadata.

No SQLite or Turso migration is required.
