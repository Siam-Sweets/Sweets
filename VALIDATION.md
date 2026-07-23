# PosApp v1.9.6 Validation Notes

- Verified application, assembly, file, informational, installer, Worker, README, and changelog versions at 1.9.6.
- Verified workflow-dispatch inputs accept `1.9.6`, `v1.9.6`, and `V1.9.6`.
- Verified lowercase `v*` and uppercase `V*` tag triggers are configured.
- Verified the normalized release version is still required to match the project version.
- Verified the version input is passed through `REQUESTED_VERSION` instead of direct PowerShell interpolation.
- Verified Worker syntax and smoke tests.
- Verified YAML, JSON/JSONC, XML/XAML, and project files parse successfully.
- No database schema, Turso schema, synchronization protocol, cloud deployment, desktop UI, or image-handling changes were introduced.
- Windows restore/build remains unverified until GitHub Actions runs.
