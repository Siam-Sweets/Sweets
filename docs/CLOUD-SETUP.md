# Cloudflare Worker and Turso setup

This deployment is required for first-run account onboarding. PosApp remains offline-first after the initial full synchronization, but a new installation cannot create an independent local store.

## Prerequisites

- A Turso account and database.
- A Cloudflare account with Workers enabled.
- Node.js 24 and npm for Worker development.
- Wrangler authenticated locally, or Cloudflare repository secrets for CI.
- Turso CLI only when you need to inspect or apply migrations manually outside GitHub Actions.

## 1. Create Turso

Create the Turso database. The GitHub deployment workflow now applies every pending reviewed migration in filename order and verifies the final schema before deploying the Worker. For a manual deployment or recovery, apply the same files in order:

```bash
turso db create posapp-production
turso db shell posapp-production < cloud/migrations/0001_initial.sql
turso db shell posapp-production < cloud/migrations/0002_indexes.sql
turso db shell posapp-production < cloud/migrations/0003_migration_lock.sql
turso db shell posapp-production < cloud/migrations/0004_financial_composition_staging.sql
turso db shell posapp-production "SELECT version, name, applied_at_utc FROM schema_migrations ORDER BY version;"
```

The final query must show versions 1, 2, 3, and 4. The automated runner reads `schema_migrations`, skips completed versions, handles an interrupted version-3 column addition, and verifies required tables and columns. Review every new SQL file and back up/export important production data before approving a deployment. Never run a development database token against production by accident.

Create a Worker-only database token. Do not distribute it to desktop devices:

```bash
turso db tokens create posapp-production
```

## 2. Configure Worker secrets

From `cloud/worker`, install the locked dependencies and set secrets separately for each environment:

```bash
npm ci
npx wrangler secret put TURSO_DATABASE_URL --env development
npx wrangler secret put TURSO_AUTH_TOKEN --env development
npx wrangler secret put JWT_SIGNING_SECRET --env development
npx wrangler secret put REFRESH_TOKEN_SECRET --env development
npx wrangler secret put PASSWORD_PEPPER_SECRET --env development
```

Repeat with `--env production`. Generate the signing, refresh-token, and password-pepper secrets independently with a cryptographically secure generator and use at least 32 random bytes for each. Values must never be committed. `.dev.vars.example` contains names and placeholders only; copy it to `.dev.vars` for local emulation and keep that file ignored.

Required secret bindings:

| Binding | Purpose |
|---|---|
| `TURSO_DATABASE_URL` | libSQL database URL used only by the Worker |
| `TURSO_AUTH_TOKEN` | least-scope Turso token used only by the Worker |
| `JWT_SIGNING_SECRET` | HMAC key for short-lived access tokens |
| `REFRESH_TOKEN_SECRET` | independent key mixed into stored refresh-token hashes |
| `PASSWORD_PEPPER_SECRET` | independent key used only for online-password verifiers |

Non-secret version, TTL, request-size, and batch limits are declared in `wrangler.toml` for development and production.

## 3. Validate locally

```bash
cd cloud/worker
npm ci
npm run check
npm test
npm run dev
```

Open `http://127.0.0.1:8787/status` to run the public end-to-end status page. It verifies the Free-plan password verifier, JWT/refresh-token cryptography, required tables and columns, and a complete organization/account/device/session/sync/audit batch that is intentionally aborted and checked for atomic rollback. The Worker root (`/`) is the browser account portal. `GET /api/v1/diagnostics` returns the readiness result as JSON, while `GET /api/v1/meta` remains the lightweight metadata endpoint. Do not test account creation until the status page says **Ready** and **Account creation: verified**. The desktop permits plain HTTP only for a loopback Worker; every non-loopback endpoint must use HTTPS.

## 4. Deploy

```bash
cd cloud/worker
npm run deploy:development
npm run deploy:production
```

Do not proceed to production until all five production runtime secrets exist, the migration step succeeds, and the Worker `/status` page reports **Ready**. The deployment workflow checks this automatically through `/api/v1/diagnostics` and prints every failed stage from its JSON report. After that preflight passes, create a test organization, synchronize two disposable clients, revoke one session, and confirm the revoked client can no longer call a protected endpoint.

## GitHub Actions

`.github/workflows/deploy-worker.yml` validates TypeScript, runs Worker unit and real SQLite/libSQL integration tests, executes the migrations against a temporary database, performs a Wrangler dry run, applies pending migrations to the selected Turso database, verifies the remote schema, deploys the Worker, and then calls the deployed public diagnostic endpoint. A manual run can select production.

The deployment uses the Node 24-compatible `cloudflare/wrangler-action@v4`, pins the same Wrangler version as `package-lock.json`, and checks that both Cloudflare credentials are present before invoking Wrangler. Missing credentials therefore produce a named GitHub Actions error instead of an opaque `npx` exit code.

Configure these GitHub environment or repository secrets for Worker deployment:

- `CLOUDFLARE_API_TOKEN`: a narrowly scoped token that can edit Workers Scripts for the intended account.
- `CLOUDFLARE_ACCOUNT_ID`: the target account ID.
- `TURSO_DATABASE_URL`: the Turso/libSQL database URL for the selected environment.
- `TURSO_AUTH_TOKEN`: the Worker-only Turso authentication token.
- `JWT_SIGNING_SECRET`: an independent cryptographically random value of at least 32 characters.
- `REFRESH_TOKEN_SECRET`: a second independent cryptographically random value of at least 32 characters.
- `PASSWORD_PEPPER_SECRET`: a third independent cryptographically random value of at least 32 characters. Keep it stable; changing it invalidates stored online-password verifiers.
- `POSAPP_CLOUD_API_BASE_URL`: the Worker origin for that exact GitHub environment. For the supplied development deployment, use `https://posapp-cloud-api-development.sweets-4c4.workers.dev/`. The deployment job uses it to verify the newly deployed portal/status routes and the Windows build embeds the same origin.

The deployment workflow validates these values, applies and verifies pending SQL migrations with the Turso URL/token, uploads the five runtime bindings as encrypted Cloudflare Worker secrets, and then deploys the selected environment. `wrangler.toml` declares them as required, so Wrangler rejects a deployment that would leave the Worker unable to create or authenticate accounts. Configure the secrets separately in protected `development` and `production` GitHub environments when the environments use different databases or keys.

Use an environment-specific `POSAPP_CLOUD_API_BASE_URL`, such as `https://posapp-cloud-api-development.sweets-4c4.workers.dev/` or your production Worker origin. Do not include `/api/v1`, `/api/v1/meta`, query parameters, or fragments. Development and production environments should point to their matching Worker names.

The desktop workflow validates the value and embeds it into `PosApp.exe` as assembly metadata. Users do not type or choose the Worker address. The URL is not an authentication secret and remains discoverable from the distributed executable; account access is still protected by the Worker-issued user tokens. Main, tag, and manual builds require this secret. Pull-request verification builds can compile without it but disable new online sign-in and organization creation.

Keep Worker runtime secrets in protected GitHub environments; the deployment workflow uses them without writing their values to the repository or logs. Require approval for production. Migrations remain separate, reviewable SQL files, but deployment is blocked unless every pending file applies and the remote schema verification succeeds.

## Desktop connection

On a new computer, PosApp opens the online account window before cashier login. Select **Sign in** for an existing organization or **Create organization** for a new one. There is no offline/local setup option. The app uses the Worker endpoint embedded by GitHub Actions, so no address field is shown. The first organization user is its administrator. Each Windows installation generates a persistent UUID device identity and records its selected branch; the user also chooses a device-local PIN for cached offline login after onboarding. No built-in default account or PIN is created.

A fresh computer joining an existing organization starts with a schema-only SQLite cache and performs a cursor-zero pull of the complete authorized store. Creating a new organization first provisions the organization, store, administrator, device, and session atomically in Turso, then follows the same authoritative cursor-zero download. Store preferences entered in the online form are saved through the normal synchronized settings channel only after that download succeeds. PosApp writes a device-local preparation marker first and writes `app:setup-complete` only after the download and any initial settings upload finish with no pending operations or conflicts. Restart therefore safely resumes the original download path. Both `app:` markers are intentionally local-only and are never sent to Turso.

An already-configured local installation follows a stricter path. If it contains records without cloud identities, PosApp creates and validates a SQLite backup, sets the reconciliation gate, and blocks both push and pull before any background synchronization. The administrator must then choose **Upload local data** (which rechecks that the organization is empty and obtains the migration lease) or **Use server data** (which keeps the safety backup and replaces the local synchronized working copy). A populated local database is never silently combined with a populated cloud organization.

Administrators create additional cloud users from **Online account & sync → Online users**. Assign each user a role and default store. Those users choose their device-local offline PIN on first online sign-in.

## Browser account portal

Open the Worker origin in a modern browser. For the development environment in this project, that is:

```text
https://posapp-cloud-api-development.sweets-4c4.workers.dev/
```

Use an organization administrator's existing PosApp online username/email and password, or create a new organization from the second tab. The portal reads only the authenticated tenant, shows exact **Total users** and **Active users** counts, and lists up to 500 users. Deleting a user is a protected soft deletion: the Worker disables the user, revokes every login session and refresh token, writes the synchronized deletion tombstone and audit record, and leaves sales, payments, inventory, and other financial history intact. The signed-in account and final active administrator cannot be deleted.

Do **not** add an organization user's password to GitHub Actions. GitHub secrets are for deployment infrastructure (`CLOUDFLARE_API_TOKEN`, Turso credentials, and the three independent cryptographic secrets) and never act as a cross-tenant portal password. Online user password verifiers live in Turso as salted, deployment-peppered hashes; plaintext passwords are never stored. If the administrator password is unknown, change it through an authenticated PosApp account-management flow rather than replacing `PASSWORD_PEPPER_SECRET`—changing that pepper invalidates all existing online password verifiers.

## API routes

| Area | Routes |
|---|---|
| Browser portal and public status | `GET /`; `GET /status`; `GET /api/v1/diagnostics` |
| Metadata | `GET /api/v1/meta` |
| Authentication | `POST /auth/signup`, `/auth/login`, `/auth/refresh`, `/auth/logout`, `/auth/register-device`; `GET /auth/sessions`; `DELETE /auth/sessions/{id}`; `DELETE/PATCH /auth/devices/{id}` |
| Account | `GET /account/profile`; `POST /account/password` |
| Users | `GET/POST /users`; `PATCH /users/{id}`; `DELETE /users/{id}` |
| Stores | `GET/POST /stores`; `GET /stores/{id}/verify` |
| Sync | `POST /sync/push`; `GET /sync/pull`; `GET /sync/status` |
| Initial migration | `POST /migrations/initial/start`; `POST /migrations/initial/finish` |
| Audit | `POST /audit/events` for the small allow-list of desktop-originated migration/restore/conflict events |

All paths in the table are beneath `/api/v1`.
