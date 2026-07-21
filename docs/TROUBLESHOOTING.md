# Online synchronization troubleshooting

The UI maps Worker codes to localized, user-safe messages and retains the request ID for server investigation. Never paste access/refresh tokens, password/PIN values, `.dev.vars`, or customer payloads into support tickets.

| Code or condition | Meaning | Safe action |
|---|---|---|
| `NETWORK_UNAVAILABLE`, `NETWORK_TIMEOUT` | Internet/Worker cannot be reached | Keep working locally; use Retry when connectivity returns. |
| `REMOTE_SERVICE_ERROR`, `SYNC_FAILED` | Worker/Turso/proxy failure | Check Worker/Turso status and request ID; pending outbox rows remain local. |
| `DATABASE_CONFIGURATION_ERROR` | The selected Worker environment is missing `TURSO_DATABASE_URL` or `TURSO_AUTH_TOKEN` | Add both secrets to the matching GitHub environment and rerun **Validate and deploy PosApp Cloud Worker**. |
| `DATABASE_SCHEMA_NOT_READY` | Turso is reachable but required tables or migration versions are missing | Rerun **Validate and deploy PosApp Cloud Worker** for the correct environment; the workflow applies and verifies pending migrations before redeploying. |
| `AUTHENTICATION_CONFIGURATION_ERROR` | The Worker is missing a valid `JWT_SIGNING_SECRET`, `REFRESH_TOKEN_SECRET`, or `PASSWORD_PEPPER_SECRET` | Add three independent values of at least 32 characters to the matching GitHub environment and redeploy. |
| `ORGANIZATION_PROVISIONING_FAILED` | A specific database or cryptographic stage failed while creating the tenant | Open the Worker `/status` page, rerun diagnostics, and use the displayed failed stage and request ID. The transaction is rolled back. |
| `ORGANIZATION_PREFLIGHT_FAILED` | The public safe write-and-rollback test could not reproduce the complete signup transaction | Do not create accounts yet. Fix the stage shown on the Worker status page and redeploy. |
| `INVALID_CREDENTIALS` | Username/email/password mismatch | Check the account; repeated failures are rate-limited. |
| `ACCESS_TOKEN_EXPIRED` | Short token expired | Client renews automatically; if renewal fails, sign in again. |
| `REFRESH_TOKEN_EXPIRED`, `REFRESH_TOKEN_REVOKED`, `REFRESH_TOKEN_REUSE` | Session cannot be renewed | Sign in again; investigate reuse because the session family was revoked. |
| `SESSION_REVOKED`, `SESSION_EXPIRED` | This device login was revoked or aged out | Sign in again if the device remains authorized; ask an administrator if revocation was unexpected. |
| `DEVICE_REVOKED` | Administrator revoked the terminal | Administrator must authorize a valid device/session; local data is retained. |
| `USER_DISABLED`, `ORGANIZATION_DISABLED`, `STORE_DISABLED` | Account or selected store access is disabled | Contact an administrator; do not create a replacement tenant to bypass policy. |
| `PERMISSION_DENIED`, `STORE_ACCESS_DENIED` | Role/store assignment blocks action | Correct the online role/assignment; do not retry unchanged batches repeatedly. |
| `DUPLICATE_OPERATION_IN_BATCH`, `IDEMPOTENCY_KEY_REUSED` | Invalid/reused operation identity | Normal retries are safe; inspect the local outbox if identifiers were manually altered. |
| `VERSION_CONFLICT` | Master record changed on another device | Open Account & Sync, review both versions, choose Keep local or Use server. |
| `IMMUTABLE_TRANSACTION` | Final financial fact was edited | Use refund, void, reversal, or inventory adjustment instead of overwrite/delete. |
| `DUPLICATE_BUSINESS_RECORD` | A branch-scoped SKU/barcode/category/setting, receipt, purchase document, register, or ledger source already exists | Keep the original record; inspect operation IDs before retrying any newly generated duplicate. |
| `PAYMENT_TOTAL_EXCEEDED` | Payments exceed the immutable sale total | Correct the tender workflow; never edit the completed sale total to make it fit. |
| `INVENTORY_SOURCE_MISMATCH` | Ledger movement does not match its sale/purchase source | Preserve the source document and create a reviewed adjustment instead of altering the movement. |
| `CATALOG_VERSION_UNAVAILABLE` | The offline product version cannot be verified | Keep the local operation; restore the required catalog history or resolve it as a reviewed migration conflict. |
| `REFUND_QUANTITY_EXCEEDED` | Cumulative returned quantity exceeds the original line | Review linked refund receipts; do not regenerate the refund with a new operation ID. |
| `CLOUD_DATA_NOT_EMPTY`, `MIGRATION_IN_PROGRESS` | Initial import is unsafe or another device owns the migration lease | Use the owning administrator device, or reconcile the non-empty organization explicitly; never force-merge. |
| `MIGRATION_INCOMPLETE` | A staged sale or purchase is still missing declared children | Retry on the migration device and resolve its failed outbox item before finishing. |
| `MIGRATION_NOT_ACTIVE` | The recorded initial-import lease is not owned by this user/device or cannot be resumed | Use the original administrator device; do not generate a replacement migration ID or force-upload legacy snapshots. |
| `MIGRATION_ALREADY_COMPLETED` | This device completed the lease but its final client response was interrupted | Let PosApp verify cloud counts and recover the retained backup summary; do not upload the snapshot again. |
| `INVALID_SYNC_CURSOR` | Cursor is malformed | Reset only through documented reconciliation; do not edit SQLite by hand. |
| `CLIENT_VERSION_INCOMPATIBLE` | Desktop is below server minimum | Install a trusted signed PosApp update. |
| `SERVER_VERSION_INCOMPATIBLE` | Worker/schema is older than desktop | Deploy the matching Worker and reviewed Turso migrations. |
| `VALIDATION_ERROR`, `PAYLOAD_TOO_LARGE`, `REQUEST_TOO_LARGE` | Invalid or excessive payload | Correct local data/import; inspect error details without exposing PII. |
| `PARTIAL_BATCH_FAILURE` | Some operations were accepted while others failed | Accepted rows remain idempotent; resolve the listed failed/conflict rows and Retry. |
| `RESTORE_RECONCILIATION_REQUIRED` | A restored or newly linked local database may differ from cloud data | Explicitly use server state, or upload local data only after proving the cloud organization is empty. |

## Desktop onboarding and sync diagnostic log

Every online sign-in and organization-creation attempt now receives a short diagnostic ID. If setup does not finish, the dialog shows that ID, the exact log path, the current sync state, pending-upload and conflict counts, downloaded-change count, and cursor. Select **Open log folder** directly from the account window.

The current log is `%LOCALAPPDATA%\PosApp\Logs\cloud-sync.jsonl`. It contains one JSON object per line and rotates at 2 MiB to `cloud-sync.jsonl.1` through `.4`. Entries for the same attempt share `attemptId`. To isolate one attempt in PowerShell, replace the sample ID with the value displayed by PosApp:

```powershell
Get-Content "$env:LOCALAPPDATA\PosApp\Logs\cloud-sync.jsonl" |
  Select-String '"attemptId":"A1B2C3D4E5F6"'
```

The log records stage names, client/API/schema versions, non-sensitive queue summaries, retry counts, cursor progress, Worker request IDs, and sanitized exception metadata. It does not intentionally record usernames, emails, passwords, PINs, access/refresh tokens, authorization headers, cookies, request payloads, SQL, customer fields, phone numbers, or addresses. Log writing is best-effort and can never block the local register.

For support, provide the diagnostic ID and only the matching JSON lines. If an entry contains `requestId`, use that value to correlate with Cloudflare Worker logs. Do not send `cloud-session.dat`, `.dev.vars`, `posapp.db`, Turso credentials, screenshots containing passwords, or an unreviewed complete log archive.

PosApp 2.0.19 could report `SYNC_FAILED` with an inner `ReadOnlySpan<string>` or `FuncCallInstruction` error while returning a failed upload batch to `Pending`. That was a desktop EF Core expression-tree issue, not a Turso credential or connectivity problem. Upgrade the desktop client to 2.0.20 or later. The corrected client also logs and preserves the original API failure if a separate server-side problem remains.

PosApp 2.0.20 may then expose the underlying `VALIDATION_ERROR` stating that `clientTimestampUtc must be a UTC timestamp`. SQLite preserved the UTC value but did not preserve its .NET `DateTimeKind`, so JSON omitted the `Z` marker required by the Worker. Upgrade the desktop client to 2.0.21 or later. No Cloudflare or Turso secret needs to be changed for this correction.

## Worker deployment checks

1. Run `npm ci`, `npm run check`, and `npm test` in `cloud/worker`.
2. Confirm the **Apply and verify Turso migrations** workflow step succeeded for the selected environment.
3. Confirm the five runtime secrets and environment-specific `POSAPP_CLOUD_API_BASE_URL` exist for the exact Wrangler environment.
4. Open the Worker `/status` URL over HTTPS. Confirm the page says **Ready** and **Account creation: verified**; the Worker base URL itself should show the PosApp Cloud Account portal.
5. For machine-readable output, request `/api/v1/diagnostics`. The endpoint always returns readable JSON; inspect `ready`, require every check to have `status: "pass"`, and require `accountCreationReady` to be `true`.
6. Use Cloudflare logs by the status-page or desktop request ID; the Worker emits only a sanitized category/provider code and never request bodies, tokens, SQL, or database URLs.

## Local database issues

If startup reports migration failure or corruption, do not delete `posapp.db`. Preserve the database, WAL/SHM files, logs, and newest verified backups. Use the existing staged Restore flow. After any restore while cloud is configured, complete the reconciliation card before uploads resume.

If a batch is interrupted, press Retry. Synchronized operation IDs make uncertain retries idempotent. If Pending stays high, migrate in repeated bounded cycles and resolve Failed/Conflict entries first.

## Windows build validation

WPF/XAML compilation, self-contained publication, portable ZIP, signing, and Inno Setup run on `windows-latest` in `.github/workflows/build.yml`. Worker validation/deployment is independent. An unsigned local build remains runnable but the secure in-app updater deliberately refuses an unsigned installer.
