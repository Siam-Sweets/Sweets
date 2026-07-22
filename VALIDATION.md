# PosApp v1.9.1 Validation Notes

Validated in the available Linux environment on 2026-07-22:

- Confirmed `PosApp.Services` now references `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2.
- Confirmed `PosApp.Wpf` now references `Microsoft.Extensions.DependencyInjection` 8.0.2.
- Verified no remaining 8.0.1 Dependency Injection package references exist in project files.
- Verified application, assembly, file, informational, installer, Worker, README, and changelog versions at 1.9.1.
- Parsed all WPF XAML and localization dictionaries successfully.
- Verified English/Bengali resource parity and referenced XAML event handlers.
- Ran `npm run check --prefix cloud/worker` successfully.
- Confirmed v1.9.1 contains no local SQLite or Turso schema changes.
- Confirmed serializers still exclude `ImagePath` and no image-upload/storage endpoint exists.

Not validated here:

- `dotnet restore`, Windows WPF compilation, installer compilation, or runtime UI execution, because the available container does not include the .NET SDK or Windows build tools.
- Live Cloudflare Worker/Turso deployment, because no user cloud credentials were provided.

The GitHub Actions Windows workflow remains the authoritative restore and build check.
