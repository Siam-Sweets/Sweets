# PosApp Cloud Worker v1.9.1

Self-hosted account, device, snapshot, and incremental-sync API for PosApp. Turso credentials and JWT signing material remain in Worker secrets; Windows devices receive only PosApp access/refresh tokens.

## Deploy

### New database

1. Create a Turso database and apply `schema.sql`.
2. Copy `wrangler.toml.example` to `wrangler.toml`.
3. Set secrets:

```powershell
npx wrangler secret put TURSO_DATABASE_URL
npx wrangler secret put TURSO_AUTH_TOKEN
npx wrangler secret put JWT_SECRET
npx wrangler secret put REGISTRATION_KEY
```

4. Run `npm run check`, then `npm run deploy`.
5. In PosApp, open **Settings → Cloud**, enter the HTTPS Worker URL, and create the owner account.

### Upgrade

- From v1.6.0: apply `migrations/v1.7.0.sql` once, then deploy the v1.9.1 Worker.
- From v1.7.0 or v1.8.0: deploy the v1.9.1 Worker directly. No Turso schema migration is required.

## Sync behavior

- `POST /v1/sync/push`: accepts up to 100 idempotent changes per request.
- `GET /v1/sync/pull`: returns ordered changes after a per-device cursor.
- `GET /v1/devices`: returns registered-device and last-seen diagnostics for the owner.
- `POST /v1/sync/snapshot/upload`: stores a full restore baseline.
- `GET /v1/sync/snapshot/download`: returns the latest store snapshot.
- Cloud revisions detect concurrent edits; conflicts are returned without overwriting either side.
- Deleted records are retained as tombstones in the sync log.
- Store transfers, transfer items, and their append-only stock movements synchronize as protected operational records.

## Security and limits

- Passwords use PBKDF2-HMAC-SHA256 with a unique salt.
- Access tokens expire after 15 minutes; refresh tokens rotate and are stored only as hashes.
- Owner sign-up requires the private registration key.
- Repeated sign-in attempts are rate-limited per email/IP window.
- Push requests are limited to 100 changes; each record is limited to 500 KB.
- Full snapshots are limited to 15 MB per store and retain the latest three versions.
- Product/user image paths are excluded. No image files are uploaded or stored.
