# PosApp v1.10.9 Phone-Only Deployment Guide

1. In GitHub repository settings, keep the Cloudflare/Turso secrets required by **Deploy PosApp Cloud**.
2. Run **Deploy PosApp Cloud** and wait for the Worker health check to report version `1.10.9`.
3. Save the deployed HTTPS Worker address in the `POSAPP_CLOUD_API_URL` repository variable.
4. Run **Build PosApp** with version `1.10.9`.
5. Download and install the generated Windows setup artifact.
6. On first launch, choose:
   - **Sign in** to authenticate with an existing email/password and restore all organization data, or
   - **Create organization** to create the owner/store and upload the complete initial snapshot.

The registration key is used only while creating an organization. Existing-account sign-in requires only email and password.

No new SQLite or Turso migration is required for v1.10.9. Deploying the new Worker is recommended for aligned health/version metadata; the authentication and snapshot endpoints remain protocol-compatible with v1.10.8.
