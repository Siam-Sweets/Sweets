# PosApp Project Handoff Summary (v1.10.2)

## Latest Baseline

- **Current baseline:** `PosApp-source-v1.10.2-cloudflare-pbkdf2-fix.zip`
- **Continue versioning from:** **v1.10.2**

## Latest Fix

- Fixed Cloudflare owner signup failing because the Worker requested 120,000 PBKDF2 iterations while the deployed runtime accepts at most 100,000.
- Cloud owner-password hashing now uses PBKDF2-HMAC-SHA256 with a unique salt and 100,000 iterations.
- Added a controlled compatibility guard and smoke-test coverage.
- Local desktop user PIN hashing remains unchanged at 120,000 iterations.

## Deployment

- Redeploy the Worker after updating the source.
- No Turso migration is required for v1.10.2.
- `/v1/health` must report version `1.10.2` before account creation is retried.

## Compatibility

- No local SQLite schema changes.
- No cloud schema or synchronization protocol changes.
- No desktop UI changes.
- No image upload or image synchronization.

## Validation Status

- Worker syntax and atomic-sync smoke tests passed.
- The test suite verifies that signup persists 100,000 iterations.
- Project/XML/XAML/JSON/YAML static validation passed.
- Windows compilation, installer execution, live Cloudflare deployment, and real-device testing still require GitHub Actions and the owner's environment.

## Development Rules

1. Always modify the latest baseline ZIP.
2. Preserve all existing features and prior fixes.
3. Bump every relevant version for each code or workflow change.
4. Update README and CHANGELOG.
5. Validate XAML, localization, project files, cloud Worker, and migrations where applicable.
6. Do not claim a Windows build or live deployment passed unless it was actually run.
