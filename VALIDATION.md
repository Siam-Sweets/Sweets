# PosApp v1.9.4 Validation Notes

- Verified application, assembly, file, informational, installer, Worker, README, and changelog versions at 1.9.4.
- Verified `POSAPP_CLOUD_API_URL` is optional in the GitHub Actions build workflow.
- Verified a missing or blank variable produces an empty embedded endpoint instead of failing the build.
- Verified a supplied endpoint must still be an absolute HTTPS URL without query string or fragment.
- Verified the Cloud settings XAML contains no Worker URL input and no device-name input.
- Verified sign-up/sign-in continue using the automatically detected Windows machine name.
- Verified English/Bengali resource-key parity and XML parsing.
- Verified Worker syntax and smoke tests.
- No database schema, Turso schema, synchronization protocol, or image handling changes were introduced.
- Windows restore/build remains unverified until GitHub Actions runs.
