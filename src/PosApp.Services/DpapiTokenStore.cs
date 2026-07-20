using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;

namespace PosApp.Services;

/// <summary>Encrypts cloud tokens for the current Windows user with DPAPI.</summary>
public sealed class DpapiTokenStore : ISecureTokenStore
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("PosApp.CloudTokens.v2");
    private readonly string _tokenPath;

    public DpapiTokenStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PosApp", "Security");
        Directory.CreateDirectory(folder);
        _tokenPath = Path.Combine(folder, "cloud-session.dat");
    }

    public async Task<CloudAuthTokens?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_tokenPath)) return null;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cloud session protection requires Windows DPAPI.");

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_tokenPath, cancellationToken);
            var clear = ProtectedData.Unprotect(encrypted, OptionalEntropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<CloudAuthTokens>(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (CryptographicException)
        {
            // Tokens copied from another Windows account or damaged on disk are
            // intentionally unusable. Remove them and require a fresh sign-in.
            await ClearAsync(cancellationToken);
            return null;
        }
        catch (JsonException)
        {
            await ClearAsync(cancellationToken);
            return null;
        }
    }

    public async Task SaveAsync(CloudAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cloud session protection requires Windows DPAPI.");

        var clear = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            var encrypted = ProtectedData.Protect(clear, OptionalEntropy, DataProtectionScope.CurrentUser);
            var temporary = _tokenPath + ".new";
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
            File.Move(temporary, _tokenPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
        var temporary = _tokenPath + ".new";
        if (File.Exists(temporary)) File.Delete(temporary);
        return Task.CompletedTask;
    }
}
