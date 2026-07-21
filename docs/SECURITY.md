# Security model

## Authentication and sessions

- Online passwords are validated in the Worker and stored as deployment-secret-peppered PBKDF2-HMAC-SHA-256 verifiers with a unique 16-byte random salt and 12,000 iterations. The pepper is domain-separated from both authentication secrets, making Turso-only database exports insufficient for offline password verification. Login attempts are throttled in memory and in Turso. Plaintext passwords and PINs are never stored or logged. The bounded work factor is intentionally compatible with the Workers Free 10 ms CPU budget; legacy 310,000-round verifiers remain readable for backward compatibility.
- Access tokens are HS256 JWTs with issuer/audience, tenant, user, session, device, role, effective permissions, password version, issued time, and expiry. The default lifetime is 10 minutes and is bounded to 5–15 minutes.
- Refresh tokens are opaque random values with a token ID. Turso stores only a secret-keyed SHA-256 hash. Every refresh consumes and revokes the old token and creates a new token in the same family.
- Reuse of an old refresh token revokes the token family and login session. Password changes revoke the user's other sessions and increment a password version that invalidates old access tokens.
- Every protected request reloads the session, user, device, organization, password version, role, and permissions from Turso. Token claims cannot keep a revoked device, disabled user, expired session, or removed permission active online.
- Explicit logout revokes the current or all user sessions server-side and clears the encrypted desktop token file.
- The browser account portal uses the same tenant-scoped authentication endpoints and permissions as the desktop. It keeps rotating tokens only in browser session storage, renews expired access tokens, revokes its server session on sign-out, and serves a nonce-restricted content security policy. It never receives Turso or Cloudflare credentials.

## Desktop secrets

Only access/refresh tokens are present on a desktop. `DpapiTokenStore` encrypts them with Windows Data Protection API `CurrentUser` scope and atomically replaces `%LOCALAPPDATA%\PosApp\Security\cloud-session.dat`. Copying that file to another Windows account does not make it usable. Organization/store/device metadata is non-secret.

The desktop never receives `TURSO_AUTH_TOKEN`, `JWT_SIGNING_SECRET`, `REFRESH_TOKEN_SECRET`, `PASSWORD_PEPPER_SECRET`, or a Cloudflare API token. The public Worker origin is embedded at build time from `POSAPP_CLOUD_API_BASE_URL`; it is routing configuration, not a credential, and is expected to be discoverable in the distributed executable. `.dev.vars`, SQLite/WAL files, build outputs, and Worker dependencies are ignored by Git.

There is no deployment-wide portal password. GitHub Actions contains infrastructure secrets only; using one shared password to enumerate every tenant would violate organization isolation. Portal users authenticate with their own organization account, and the backend requires `users.manage` before returning tenant counts or user rows. Deletion is a transactional soft deletion with current-user/final-administrator protection, session and refresh-token revocation, a synchronization tombstone, and an audit event.

## Authorization

The three existing roles remain Cashier, Manager, and Administrator. The Worker owns the canonical effective-permission map. Each endpoint and each synchronization entity invokes a server check. Sensitive transitions receive additional checks:

- Custom permission overrides are restricted to a server allow-list. A client cannot submit `*` or an unknown permission to turn a non-administrator role into an administrator.

- Cashiers may create normal sales/payments and sale-linked stock deductions but cannot forge arbitrary inventory adjustments.
- Refunds and returns require `sales.refund`; voids require `sales.void`.
- Purchases and purchase stock receipts require purchase/inventory permissions.
- Online user creation/deactivation/role changes require `users.manage`.
- The final active administrator cannot be disabled or demoted; the current user cannot deactivate themself.
- Non-administrators can select only active `user_store_assignments`; no role can access another tenant.

Local PIN checks preserve offline usability and use the existing salted PBKDF2 implementation. Cached local roles are an offline availability decision: a revocation made elsewhere becomes effective on this device at its next successful protected request. An explicit terminal user/device/session/organization/store response clears cloud tokens, stops synchronization, displays a localized notice, and returns the active WPF window to sign-in; device revocation and user deactivation also block cached login. A merely unavailable network or transient refresh request does not interrupt the local register. High-risk businesses should require periodic connectivity and physically secure terminals.

New installations contain no local default usernames, PINs, store configuration, or catalog. First run requires online sign-in or organization creation, and only the authenticated cloud user receives a protected device-local PIN verifier for cached offline login. The setup-complete marker uses the reserved device-local `app:` namespace, which the outbox excludes and the Worker rejects.

## API and data handling

- Non-loopback clients require HTTPS. JSON and gzip bodies have pre/post-decompression size limits.
- Turso statements are parameterized. Entity table names come only from a fixed server map, never request text.
- Tenant/store/user identifiers, roles, relationships, amounts, statuses, receipt/document numbers, payment totals, stock sources, and permissions are revalidated server-side. Immutable business identifiers and normalized branch-scoped master identifiers are protected by database uniqueness constraints; SKU and barcode share one server-enforced namespace.
- Every operation in a multi-record push runs inside its own Turso savepoint. If its validation, uniqueness, cursor, or audit write fails, all writes for that operation roll back before the Worker reports the safe structured error.
- Normal sale lines carry the last synchronized catalog version. The Worker resolves that current or historical product snapshot and verifies price, cost, tax, unit, and discount eligibility, so an offline device can honor a known price without making an arbitrary client price authoritative. Existing-database migration is the only exception: historical lines have no old catalog-version history, so legacy price snapshots require an administrator and the audited cloud-empty migration workflow; financial counts and arithmetic are still reconciled.
- Initial import uses a server-owned, tenant/store/user/device-scoped migration lease. Lease creation and the cloud-empty check occur in one Turso write transaction; concurrent devices are rejected, and legacy price snapshots are accepted only from the active lease owner.
- Errors use stable codes and request IDs; raw SQL/database failures and proxy pages are not returned to the UI.
- In-memory per-isolate limits stop short bursts. Turso-backed, expiry-bounded attempt counters enforce per-network-and-identifier and account-wide login throttles across Worker isolates; lookup keys and unauthenticated security events contain only one-way identifier hashes. At higher hostile-traffic volumes, add a Cloudflare zone/Worker Rate Limiting rule without changing the API.
- Worker code contains no `console` logging of payloads. Audit metadata is bounded and excludes passwords, tokens, PINs, and customer payloads.

## Audit trail

`audit_logs` records tenant, optional store, user, device, UTC timestamp, action, affected type/ID, request ID, and minimal metadata. Coverage includes organization/user/device/session/password changes; store creation; accepted sync operations; sale completion/refund/void; purchase completion; inventory adjustments; and conflict/migration-related synchronized operations. A failed login for a known account also creates a tenant audit row without storing the submitted password; all attempts use hashed identifiers in `security_events` because an authenticated tenant may not be known.

Backups and restore initiation remain local; the reconciliation state prevents restored data from bypassing cloud versions. The rotating desktop cloud diagnostic log records correlation/stage data, queue counts, structured codes, request IDs, and sanitized exception metadata, while redacting or excluding credentials, tokens, payloads, SQL, customer fields, and direct contact details. Operational policy should retain local PosApp logs and database backups securely, review diagnostic lines before sharing them, and restrict Windows account access.
