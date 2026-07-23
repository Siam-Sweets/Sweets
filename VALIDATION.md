# PosApp v1.9.7 Validation Notes

- Verified application, assembly, file, informational, installer, Worker, README, and changelog versions at 1.9.7.
- Verified both the Windows build job and Linux release job accept `1.9.7`, `v1.9.7`, and `V1.9.7`.
- Verified lowercase `v*` and uppercase `V*` tag triggers are configured.
- Verified the normalized release version is still required to match the project version.
- Verified manual version input is passed through `REQUESTED_VERSION` in both PowerShell and Bash jobs instead of direct script interpolation.
- Verified Worker syntax and smoke tests.
- Verified YAML, JSON/JSONC, XML/XAML, and project files parse successfully.
- No database schema, Turso schema, synchronization protocol, cloud deployment, desktop UI, or image-handling changes were introduced.
- Windows restore/build remains unverified until GitHub Actions runs.
