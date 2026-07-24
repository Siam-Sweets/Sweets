namespace PosApp.Core.Utilities;

/// <summary>
/// Identifies settings that belong only to the current Windows installation.
/// Device-local onboarding and credential state must never be uploaded to, or
/// restored from, another register.
/// </summary>
public static class SettingSyncPolicy
{
    public const string SetupCompleteKey = "app:setup-complete";
    public const string SetupPreparedKey = "app:setup-prepared";
    public const string SetupAccountEmailKey = "app:setup-account-email";

    public static bool IsDeviceLocal(string? key)
        => !string.IsNullOrWhiteSpace(key) &&
           (key.StartsWith("app:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("cloud:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("device:", StringComparison.OrdinalIgnoreCase));
}
