# PosApp v1.10.6 Phone-Only Cloud Setup

## Cloud-enabled build

1. Run **Actions → Deploy PosApp Cloud**.
2. Copy the deployed HTTPS `workers.dev` URL.
3. Open **Settings → Secrets and variables → Actions → Variables**.
4. Add `POSAPP_CLOUD_API_URL` with that URL.
5. Run **Actions → Build PosApp**.
6. Install the generated PosApp build.
7. Open **Settings → Cloud**, press **Test**, then create/sign in to the account.

## Local-only build

Leave `POSAPP_CLOUD_API_URL` unset and run **Build PosApp**. The build will complete normally, and all local POS/multi-store features will remain available without cloud sync.

The Worker URL is embedded only when the variable is supplied. PosApp does not display an endpoint field. The Windows device name is detected and registered automatically; no device-name field is shown.

## Current workflow fixes

Cloud deployment uses Node.js 24 and Wrangler 4.81.0 directly. Worker code and `POSAPP_CLOUD_CONFIG` are deployed together, so the old **Failed to upload secrets** stage is not used. The v1.10.6 build/release workflow also accepts `1.10.6`, `v1.10.6`, or `V1.10.6`. Do not enable the insecure Node 20 compatibility setting.


## v1.10.6 upgrade note

v1.10.6 has no new Turso migration. Deploy it directly. Only deployments upgrading from before v1.10.0 need `cloud/worker/migrations/v1.10.0.sql` when automatic initialization is disabled.

## v1.10.6 signup fix

This release changes cloud owner-password PBKDF2 hashing to the Cloudflare-compatible 100,000-iteration limit. After deployment, confirm `/v1/health` reports `1.10.6` before retrying **Create Account**.


## v1.10.6 desktop checkout note

v1.10.6 changes only desktop checkout behavior. Redeploying the Worker is optional when `/v1/health` already reports 1.10.2 or later. Rebuild and reinstall the Windows application to receive the register-setting and immutable-ledger fixes.
