# Phone-only cloud deployment

This path uses GitHub Actions, so no local PC, Node.js, Wrangler, or Turso CLI is required after the required account values are available.

## Required GitHub secrets

Add under **Repository → Settings → Secrets and variables → Actions → Secrets**:

- `CLOUDFLARE_ACCOUNT_ID`
- `CLOUDFLARE_API_TOKEN`
- `POSAPP_JWT_SECRET` — at least 32 random characters; keep it unchanged.
- `POSAPP_REGISTRATION_KEY` — a private key used only when creating the first owner account.

Then choose one Turso setup mode.

### Mode A — existing Turso database

Variable:

- `TURSO_DATABASE_URL`

Secret:

- `TURSO_DATABASE_AUTH_TOKEN`

### Mode B — let GitHub provision Turso

Variable:

- `TURSO_ORGANIZATION`
- `TURSO_DATABASE_NAME` — optional; default `posapp-cloud`.
- `TURSO_GROUP` — optional; default `default`.

Secret:

- `TURSO_PLATFORM_API_TOKEN`

## Deploy

1. Open **Actions → Deploy PosApp Cloud → Run workflow**.
2. The workflow resolves or provisions Turso, verifies Cloudflare authentication, then deploys the Worker and encrypted `POSAPP_CLOUD_CONFIG` together with Wrangler 4.81.0 on Node.js 24.
3. Open the workflow result and copy the deployed `workers.dev` URL.
4. Test `<worker-url>/v1/health`; it should return `ok: true` and version `1.10.0`.
5. Optionally add a GitHub Actions **repository variable** named `POSAPP_CLOUD_API_URL` containing that URL. Leave it unset for a local-only build.
6. Run **Actions → Build PosApp**. The URL is embedded in the Windows build and is not shown as an editable app field.

## Connect PosApp

On the Windows computer running PosApp, open **Settings → Cloud**:

1. Press **Test**.
2. Enter email, password, display name, and the same `POSAPP_REGISTRATION_KEY`.
3. Press **Create account**, then upload the initial snapshots or run **Sync Now**.

The device name is detected automatically from Windows and registered without showing a device-name field.

`AUTO_INITIALIZE_SCHEMA=true` creates missing Turso tables on the first Worker request. Existing tables remain unchanged because initialization uses idempotent `CREATE ... IF NOT EXISTS` statements.

## Cloudflare authentication requirements

- Create `CLOUDFLARE_API_TOKEN` from Cloudflare's **Edit Cloudflare Workers** template and scope it to the account identified by `CLOUDFLARE_ACCOUNT_ID`.
- Do not add `ACTIONS_ALLOW_USE_UNSECURE_NODE_VERSION`; v1.10.0 no longer depends on Node.js 20.
- If authentication is wrong, the **Verify Cloudflare authentication** step now shows the failure before deployment.
