# Cloudflare and Turso free-plan operation

Provider quotas can change, so confirm the current limits in the Cloudflare and Turso dashboards before production. PosApp avoids depending on a specific numerical quota and uses these controls to remain practical for small businesses on free tiers:

As checked on 2026-07-19, the official [Cloudflare Workers limits](https://developers.cloudflare.com/workers/platform/limits/) and [pricing documentation](https://developers.cloudflare.com/workers/platform/pricing/) list 100,000 requests per day, 10 ms of CPU per HTTP request, 50 external subrequests per invocation, a 3 MB compressed Worker bundle, and 128 MB memory for the Free plan. The current [Turso pricing page](https://turso.tech/pricing) lists 100 databases, 5 GB total storage, 500 million rows read per month, 10 million rows written per month, and 3 GB of monthly sync on its Free plan. These are provider limits rather than PosApp guarantees and may change.

- Local reads and writes: product lookup, cart changes, checkout, printing, reports, and cached permissions make no per-action cloud call.
- Event-triggered plus two-minute background synchronization avoids rapid polling.
- Push requests contain at most two operations; a foreground cycle sends up to 20 requests. This leaves headroom beneath the Workers Free external-subrequest ceiling because each operation can require several sequential Turso relationship, ledger, audit, and idempotency statements. Pull pages default to 100 and cap at 200 because a pull is one indexed query rather than one database round trip per row.
- A foreground cycle caps its number of batches/pages and resumes later rather than consuming unbounded Worker CPU.
- Only changed rows are placed in the outbox; rapid unsent edits to one record are compacted.
- Server cursors prevent full-database downloads. Soft-deletion tombstones are small incremental changes.
- Requests over 16 KiB are gzip-compressed by the desktop; the Worker enforces a 1 MiB decompressed request limit by default.
- Tenant/store/cursor/version indexes avoid full table scans on normal sync paths.
- Worker authentication uses Web Crypto and batched libSQL transactions. Independent idempotency and record-version reads, and savepoint recovery statements, share HTTP pipelines to conserve external subrequests. The Worker does not perform reports or local POS queries in the cloud.
- A finalized sale/purchase is published once after its staged composition is complete. The one-time child replay makes interrupted multi-batch transactions safe; exceptionally large receipts or purchase documents can consume more Turso rows and Worker/database time and should be load-tested against current free-plan quotas.
- HTTP connection pooling, local caches, and rotating access tokens minimize repeated login/profile calls.

## Growth thresholds to monitor

Watch Worker requests/CPU, Turso rows/read-write usage/storage, response latency, outbox depth, tombstone count, and audit growth. A high-volume chain with continuous sales across many terminals, very large initial imports, long-offline devices, image/blob synchronization, cloud reporting, or permanent audit retention may outgrow free quotas.

Product images and database backup files are intentionally not synchronized through this API. Large catalogs migrate over multiple bounded cycles. Before scaling, consider a paid Worker/Turso tier, per-tenant database placement, scheduled tombstone/audit retention after all device cursors pass a safe point, and dedicated object storage for media/backups.

Never reduce authentication iterations, remove indexes/tenant checks, disable idempotency, or enlarge batches without load testing merely to fit a quota.
