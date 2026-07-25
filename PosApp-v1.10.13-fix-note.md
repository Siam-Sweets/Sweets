# PosApp v1.10.13 Fix Note

## Fixed: Cloud sync push HTTP 500

The production stack trace maps to the per-change cursor lookup inside `POST /v1/sync/push`:

`SELECT last_insert_rowid() AS cursor`

The previous transaction made roughly six Turso HTTP requests per changed record. A multi-record operation could therefore exceed Cloudflare Workers Free plan's 50 external subrequests and fail with HTTP 500 even though authentication and routing succeeded.

v1.10.13 groups idempotency checks, current-record reads, writes, and cursor reads into bounded Turso pipelines. All chunks retain the same transaction baton, so a business operation is still committed completely or rolled back completely.

The Worker also now:

- Avoids the per-change `last_insert_rowid()` round trip.
- Bounds write pipelines by record count and encoded payload size.
- Rejects duplicate change IDs with HTTP 400.
- Preserves useful Turso network and statement details in Cloudflare logs.
- Covers a 250-change operation in the smoke suite while remaining well below 50 external subrequests.

## Deploy

Deploy the included v1.10.13 Worker and confirm that `<worker-url>/v1/health` reports version `1.10.13`. Then retry **Sync Now**.

Keep the existing local `posapp.db` and Turso database. Failed transactions are rolled back, and no SQLite or Turso schema migration is required.
