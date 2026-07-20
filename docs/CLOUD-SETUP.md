# Cloudflare Worker and Turso setup

The desktop can be used without this deployment. Complete these steps only for online accounts and multi-device synchronization.

## Prerequisites

- A Turso account and database.
- A Cloudflare account with Workers enabled.
- Node.js 24 and npm for Worker development.
- Wrangler authenticated locally, or Cloudflare repository secrets for CI.
- Turso CLI for applying reviewed production migrations.

## 1. Create and migrate Turso

Create a database with the Turso CLI, then apply migrations in order:

```bash
turso db create posapp-production
turso db shell posapp-production < cloud/migrations/0001_initial.sql
turso db shell posapp-production < cloud/migrations/0002_indexes.sql
turso db shell posapp-production < cloud/migrations/0003_migration_lock.sql
turso db shell posapp-production < cloud/migrations/0004_financial_composition_staging.sql
turso db shell posapp-production "SELECT version, name, applied_at_utc FROM schema_migrations ORDER BY version;"
```

The final query must show versions 1, 2, 3, and 4. Apply each migration once, review it before production, and back up/export important data before future schema changes. Never run a development database token against production by accident.

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
```

Repeat with `--env production`. Generate the signing and refresh secrets independently with a cryptographically secure generator and use at least 32 random bytes. Values must never be committed. `.dev.vars.example` contains names and placeholders only; copy it to `.dev.vars` for local emulation and keep that file ignored.

Required secret bindings:

| Binding | Purpose |
|---|---|
| `TURSO_DATABASE_URL` | libSQL database URL used only by the Worker |
| `TURSO_AUTH_TOKEN` | least-scope Turso token used only by the Worker |
| `JWT_SIGNING_SECRET` | HMAC key for short-lived access tokens |
| `REFRESH_TOKEN_SECRET` | independent key mixed into stored refresh-token hashes |

Non-secret version, TTL, request-size, and batch limits are declared in `wrangler.toml` for development and production.

## 3. Validate locally

```bash
cd cloud/worker
npm ci
npm run check
npm test
npm run dev
```

`GET http://127.0.0.1:8787/api/v1/meta` should return API/schema metadata, a request ID, and `configuration.ready: true`. If either configuration flag is false, add the missing bindings before testing account creation. The desktop permits plain HTTP only for a loopback Worker; every non-loopback endpoint must use HTTPS.

## 4. Deploy

```bash
cd cloud/worker
npm run deploy:development
npm run deploy:production
```

Do not proceed to production until migrations and all four production secrets exist. After deployment, verify `/api/v1/meta`, create a test organization, synchronize two disposable clients, revoke one session, and confirm the revoked client can no longer call a protected endpoint.

## GitHub Actions

`.github/workflows/deploy-worker.yml` validates TypeScript, runs Worker tests, executes the migrations against a temporary SQLite database, performs a Wrangler dry run, and deploys development on cloud-path pushes. A manual run can select production.

The deployment uses the Node 24-compatible `cloudflare/wrangler-action@v4`, pins the same Wrangler version as `package-lock.json`, and checks that both Cloudflare credentials are present before invoking Wrangler. Missing credentials therefore produce a named GitHub Actions error instead of an opaque `npx` exit code.

Configure these GitHub environment or repository secrets for Worker deployment:

- `CLOUDFLARE_API_TOKEN`: a narrowly scoped token that can edit Workers Scripts for the intended account.
- `CLOUDFLARE_ACCOUNT_ID`: the target account ID.
- `TURSO_DATABASE_URL`: the Turso/libSQL database URL for the selected environment.
- `TURSO_AUTH_TOKEN`: the Worker-only Turso authentication token.
- `JWT_SIGNING_SECRET`: an independent cryptographically random value of at least 32 characters.
- `REFRESH_TOKEN_SECRET`: a second independent cryptographically random value of at least 32 characters.

The deployment workflow validates these values, uploads the four runtime bindings as encrypted Cloudflare Worker secrets, and then deploys the selected environment. `wrangler.toml` declares them as required, so Wrangler rejects a deployment that would leave the Worker unable to create or authenticate accounts. Configure the secrets separately in protected `development` and `production` GitHub environments when the environments use different databases or keys.

Configure this repository secret for the Windows desktop build:

- `POSAPP_CLOUD_API_BASE_URL`: the deployed Worker origin, such as `https://posapp-cloud-api-production.example.workers.dev`. Do not include `/api/v1`, `/api/v1/meta`, query parameters, or fragments.

The desktop workflow validates the value and embeds it into `PosApp.exe` as assembly metadata. Users do not type or choose the Worker address. The URL is not an authentication secret and remains discoverable from the distributed executable; account access is still protected by the Worker-issued user tokens. Main, tag, and manual builds require this secret. Pull-request verification builds can compile without it but disable new online sign-in and organization creation.

Keep Worker runtime secrets in protected GitHub environments; the deployment workflow transfers them to encrypted Cloudflare Worker secret bindings without writing their values to the repository or logs. Require approval for production. Turso migrations are deliberately separate from Worker deployment so a reviewed schema step cannot be hidden inside an application deploy.

## Desktop connection

On a new computer, choose **Sign in or create account** directly in first-run setup. On an already configured computer, use **Online account** at login. The app uses the Worker endpoint embedded by GitHub Actions, so no address field is shown. Create an organization or sign in with an existing account. The first organization user is its administrator. Each Windows installation generates a persistent UUID device identity and records its currently selected branch; the user also chooses a device-local PIN for cached offline login. No built-in default account or PIN is created.

A first-run computer joining an existing organization clears only its unused bootstrap catalog templates, retains the authenticated local user, and performs a cursor-zero pull. Creating a new organization from first-run setup keeps the standard templates and imports them through the same server-owned cloud-empty lease used by the reviewed migration flow. Setup writes a device-local preparation marker first and writes `app:setup-complete` only after that upload or download succeeds. Restart therefore resumes the original path; a lost migration-completion response is recovered by verifying the completed server lease and counts. Both `app:` markers are intentionally local-only and are never sent to Turso.

An already-configured local installation follows a stricter path. If it contains records without cloud identities, PosApp creates and validates a SQLite backup, sets the reconciliation gate, and blocks both push and pull before any background synchronization. The administrator must then choose **Upload local data** (which rechecks that the organization is empty and obtains the migration lease) or **Use server data** (which keeps the safety backup and replaces the local synchronized working copy). A populated local database is never silently combined with a populated cloud organization.

Administrators create additional cloud users from **Online account & sync → Online users**. Assign each user a role and default store. Those users choose their device-local offline PIN on first online sign-in.

## API routes

| Area | Routes |
|---|---|
| Metadata | `GET /api/v1/meta` |
| Authentication | `POST /auth/signup`, `/auth/login`, `/auth/refresh`, `/auth/logout`, `/auth/register-device`; `GET /auth/sessions`; `DELETE /auth/sessions/{id}`; `DELETE/PATCH /auth/devices/{id}` |
| Account | `GET /account/profile`; `POST /account/password` |
| Users | `GET/POST /users`; `PATCH /users/{id}` |
| Stores | `GET/POST /stores`; `GET /stores/{id}/verify` |
| Sync | `POST /sync/push`; `GET /sync/pull`; `GET /sync/status` |
| Initial migration | `POST /migrations/initial/start`; `POST /migrations/initial/finish` |
| Audit | `POST /audit/events` for the small allow-list of desktop-originated migration/restore/conflict events |

All paths in the table are beneath `/api/v1`.
