# PosApp v1.10.11 Fix Note

## Fixed: existing-account snapshot restore

Setup sign-in could authenticate successfully and then fail with:

`Cloud snapshot capture metadata does not match its backup set.`

.NET serialized the capture time with up to seven fractional-second digits. The Cloudflare Worker normalized the same instant to JavaScript's millisecond precision before storing it. Restore compared those representations as exact .NET values and rejected otherwise valid snapshots.

v1.10.11:

- Canonicalizes new capture timestamps to milliseconds before upload.
- Compares capture metadata at the millisecond precision preserved by the Worker.
- Restores valid snapshots already uploaded by earlier builds.
- Continues validating payload hashes, row counts, backup-set IDs, store IDs, schema versions, and application compatibility.
- Rejects genuinely mismatched capture metadata at the Worker.

No existing cloud data needs to be deleted or re-uploaded.

## Upgrade

Build and install v1.10.11 over the existing installation, then retry **Sign in and sync everything**. Keep `posapp.db`. No SQLite or Turso migration is required.

Deploy the included v1.10.11 Worker to add upload-time mismatch protection and align health/version metadata. The desktop compatibility fix can restore existing snapshots after the application update.
