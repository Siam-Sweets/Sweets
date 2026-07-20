using System.Reflection;

namespace PosApp.Services;

/// <summary>
/// Reads the cloud API endpoint embedded into the desktop executable at build time.
/// The endpoint is configuration rather than a credential; authentication still uses
/// per-user access and refresh tokens issued by the Worker.
/// </summary>
public static class CloudDeploymentSettings
{
    public const string MetadataKey = "PosAppCloudApiBaseUrl";

    private static readonly DeploymentEndpoint Endpoint = LoadEndpoint();

    public static bool IsConfigured => Endpoint.ErrorMessage == null;
    public static string ApiBaseUrl => Endpoint.ApiBaseUrl;
    public static string? ConfigurationError => Endpoint.ErrorMessage;

    public static string RequireApiBaseUrl()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Endpoint.ErrorMessage ??
                "Online synchronization is not configured for this PosApp build.");
        return Endpoint.ApiBaseUrl;
    }

    private static DeploymentEndpoint LoadEndpoint()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var configured = entryAssembly?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => string.Equals(value.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value
            ?.Trim();

        if (string.IsNullOrWhiteSpace(configured) ||
            string.Equals(configured, "__NOT_CONFIGURED__", StringComparison.Ordinal))
            return new DeploymentEndpoint(string.Empty,
                "Online synchronization is not configured for this PosApp build.");

        var normalized = configured.TrimEnd('/');
        try
        {
            _ = CloudApiClient.BuildUri(normalized, "/api/v1/meta");
            return new DeploymentEndpoint(normalized, null);
        }
        catch (ArgumentException)
        {
            return new DeploymentEndpoint(string.Empty,
                "The cloud API endpoint embedded in this PosApp build is invalid.");
        }
    }

    private sealed record DeploymentEndpoint(string ApiBaseUrl, string? ErrorMessage);
}
