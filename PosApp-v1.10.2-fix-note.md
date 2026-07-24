# PosApp v1.10.2 Fix Note

## Fixed

Cloud owner-account creation returned `Internal server error` and Cloudflare logs showed:

```text
NotSupportedError: Pbkdf2 failed: iteration counts above 100000 are not supported (requested 120000).
```

The Worker requested 120,000 PBKDF2 iterations, which exceeds the deployed Workers runtime limit.

## Correction

- Cloud owner passwords now use PBKDF2-HMAC-SHA256 with a unique random salt and exactly 100,000 iterations.
- Added a strict Worker guard preventing unsupported iteration values from reaching `crypto.subtle.deriveBits`.
- Added a smoke-test assertion for the persisted iteration count.
- Local desktop user PIN hashing remains at 120,000 iterations and is unaffected.

## Upgrade

Redeploy the v1.10.2 Worker. No Turso migration, Windows database migration, or data reset is required. Failed signup attempts did not insert an owner because hashing failed before the account transaction began.

After deployment, verify `/v1/health` reports `1.10.2`, then retry **Create Account**. If too many attempts were made within fifteen minutes, wait for the authentication-rate window to expire before retrying.

## Validation

- Worker JavaScript syntax passed.
- The atomic-sync smoke suite passed.
- Signup persisted 100,000 password iterations in the test database.
- Project/XML/XAML/JSON/YAML static checks passed.
- A live Cloudflare deployment and Windows build were not run in this environment.
