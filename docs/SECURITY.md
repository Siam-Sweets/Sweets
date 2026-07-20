# Security model

## Authentication and sessions

- Online passwords are validated and hashed in the Worker with PBKDF2-HMAC-SHA-256, a unique 16-byte random salt, and 310,000 iterations. Plaintext passwords and PINs are never stored or logged.
- Access tokens are HS256 JWTs with issuer/audience, tenant, user, session, device, role, effective permissions, password version, issued time, and expiry. The default lifetime is 10 minutes and is bounded to 5–15 minutes.
- Refresh tokens are opaque random values with a token ID. Turso stores only a secret-keyed SHA-256 hash. Every refresh consumes and revokes the old token and creates a new token in the same family.
- Reuse of an old refresh token revokes the token family and login session. Password changes revoke the user's other sessions and increment a password version that invalidates old access tokens.
- Every protected request reloads the session, user, device, organization, password version, role, and permissions from Turso. Token claims cannot keep a revoked device, disabled user, expired session, or removed permission active online.
- Explicit logout revokes the current or all user sessions server-side and clears the encrypted desktop token file.

## Desktop secrets

Only access/refresh tokens are present on a desktop. `DpapiTokenStore` encrypts them with Windows Data Protection API `CurrentUser` scope and atomically replaces `%LOCALAPPDATA%\PosApp\Security\cloud-session.dat`. Copying that file to another Windows account does not make it usable. Organization/store/device metadata is non-secret.

The desktop never receives `TURSO_AUTH_TOKEN`, `JWT_SIGNING_SECRET`, `REFRESH_TOKEN_SECRET`, or a Cloudflare API token. The public Worker origin is embedded at build time from `POSAPP_CLOUD_API_BASE_URL`; it is routing configuration, not a credential, and is expected to be discoverable in the distributed executable. `.dev.vars`, SQLite/WAL files, build outputs, and Worker dependencies are ignored by Git.

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

New installations contain no known default usernames or PINs. Local setup creates its administrator only after the owner chooses a PIN; online first-run setup retains only the authenticated cloud user. The setup-complete marker uses the reserved device-local `app:` namespace, which the outbox excludes and the Worker rejects.

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

Backups and restore initiation remain local; the reconciliation state prevents restored data from bypassing cloud versions. Operational policy should retain local PosApp logs and database backups securely and restrict Windows account access.
