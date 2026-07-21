using System.Text.Json;
using PosApp.Data;

namespace PosApp.Sync.Tests;

public sealed class OrganizationProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "posapp-profile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyInstallationBecomesTheFirstProfileWithoutMovingItsDatabase()
    {
        Directory.CreateDirectory(_root);
        var existingDatabase = Path.Combine(_root, "posapp.db");
        File.WriteAllBytes(existingDatabase, new byte[] { 1, 2, 3 });

        var store = new LocalOrganizationProfileStore(_root);
        var profile = Assert.Single(store.GetProfiles());

        Assert.True(profile.IsActive);
        Assert.Equal(LocalOrganizationProfileStore.LegacyProfileId, profile.Id);
        Assert.Equal(existingDatabase, store.GetDatabasePath(profile.Id));
        Assert.True(File.Exists(existingDatabase));
    }

    [Fact]
    public void EveryAdditionalOrganizationHasIsolatedDatabaseAndTokenPaths()
    {
        var store = new LocalOrganizationProfileStore(_root);
        var legacy = store.GetActiveProfile();
        var second = store.CreateAndActivateProfile();
        var third = store.CreateAndActivateProfile();

        var databasePaths = new[] { legacy, second, third }
            .Select(profile => store.GetDatabasePath(profile.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokenPaths = new[] { legacy, second, third }
            .Select(profile => store.GetTokenPath(profile.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(3, databasePaths.Count);
        Assert.Equal(3, tokenPaths.Count);
        Assert.False(databasePaths.Any(path => path.Contains("..", StringComparison.Ordinal)));
        Assert.False(tokenPaths.Any(path => path.Contains("..", StringComparison.Ordinal)));
    }

    [Fact]
    public void ActiveProfileAndFriendlyMetadataPersistAcrossStoreInstances()
    {
        var first = new LocalOrganizationProfileStore(_root);
        var added = first.CreateAndActivateProfile();
        first.UpdateActiveProfile("tenant-2", "Second business", "Main branch", "owner");

        var reopened = new LocalOrganizationProfileStore(_root);
        var active = reopened.GetActiveProfile();

        Assert.Equal(added.Id, active.Id);
        Assert.Equal("tenant-2", active.TenantId);
        Assert.Equal("Second business — Main branch", active.DisplayName);
        Assert.Equal("owner", active.Username);
    }

    [Fact]
    public void ProfileRegistryContainsNoTokensOrPasswords()
    {
        var store = new LocalOrganizationProfileStore(_root);
        store.CreateAndActivateProfile();
        store.UpdateActiveProfile("tenant", "Business", "Store", "administrator");

        var registryPath = Path.Combine(_root, "Profiles", "profiles.json");
        using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
        var text = document.RootElement.GetRawText();

        Assert.DoesNotContain("accessToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
