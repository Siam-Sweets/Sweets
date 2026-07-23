# PosApp v1.9.6 Phone-Only Cloud Setup

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

## v1.9.6 deployment fix

The workflow uses Node.js 24 and Wrangler 4.81.0 directly. Worker code and `POSAPP_CLOUD_CONFIG` are deployed together, so the old **Failed to upload secrets** stage is no longer used. Do not enable the insecure Node 20 compatibility setting.
