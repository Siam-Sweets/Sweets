# PosApp v1.10.10 Phone-Only Deployment Guide

1. Upload the v1.10.10 source to the GitHub repository.
2. Keep the Cloudflare/Turso secrets required by **Deploy PosApp Cloud**.
3. Run **Deploy PosApp Cloud** and wait for the Worker health check to report version `1.10.10`.
4. Save the deployed HTTPS Worker address in the `POSAPP_CLOUD_API_URL` repository variable.
5. Run **Build PosApp** with version `1.10.10`.
6. Download and install the generated Windows setup artifact.
7. On first launch, choose **Sign in** for an existing account or **Create organization** for a new account.

Existing-account sign-in requires the owner email and password. Organization creation also requires the configured registration key.

No new SQLite or Turso migration is required for v1.10.10. The authentication and snapshot endpoints remain protocol-compatible with v1.10.9.
