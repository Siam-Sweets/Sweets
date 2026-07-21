using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosApp.Data;

/// <summary>
/// Non-secret metadata for one locally isolated organization cache. Cloud
/// tokens are never written here; each profile has its own DPAPI token file.
/// </summary>
public sealed class LocalOrganizationProfile
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsActive { get; set; }

    [JsonIgnore]
    public bool IsLinked => !string.IsNullOrWhiteSpace(TenantId);

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(OrganizationName)
        ? string.IsNullOrWhiteSpace(StoreName) ? OrganizationName : $"{OrganizationName} — {StoreName}"
        : "New organization";
}

/// <summary>
/// Maintains the small profile selector stored beside PosApp's local data.
/// Every non-legacy profile resolves to a separate directory, SQLite database,
/// backup set, restore staging file, device identity, and DPAPI token file.
/// </summary>
public sealed class LocalOrganizationProfileStore
{
    public const string LegacyProfileId = "legacy";
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _appFolder;
    private readonly string _registryPath;

    public LocalOrganizationProfileStore()
        : this(DbPathResolver.AppFolder())
    {
    }

    /// <summary>Allows deterministic isolated profile-store tests.</summary>
    public LocalOrganizationProfileStore(string appFolder)
    {
        if (string.IsNullOrWhiteSpace(appFolder))
            throw new ArgumentException("An application data folder is required.", nameof(appFolder));
        _appFolder = Path.GetFullPath(appFolder);
        Directory.CreateDirectory(_appFolder);
        _registryPath = Path.Combine(_appFolder, "Profiles", "profiles.json");
    }

    public IReadOnlyList<LocalOrganizationProfile> GetProfiles()
    {
        lock (_gate)
        {
            var registry = LoadOrCreateLocked();
            return registry.Profiles
                .OrderByDescending(profile => profile.Id == registry.ActiveProfileId)
                .ThenByDescending(profile => profile.LastUsedAtUtc)
                .Select(profile => Clone(profile, profile.Id == registry.ActiveProfileId))
                .ToArray();
        }
    }

    public LocalOrganizationProfile GetActiveProfile()
    {
        lock (_gate)
        {
            var registry = LoadOrCreateLocked();
            var profile = registry.Profiles.Single(profile => profile.Id == registry.ActiveProfileId);
            return Clone(profile, true);
        }
    }

    public LocalOrganizationProfile CreateAndActivateProfile()
    {
        lock (_gate)
        {
            var registry = LoadOrCreateLocked();
            var profile = new LocalOrganizationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                LastUsedAtUtc = DateTime.UtcNow
            };
            // Prepare the isolated target before publishing it as active. If
            // the directory cannot be created, the previous selector remains
            // intact and the running organization is still recoverable.
            _ = GetProfileFolder(profile.Id);
            registry.Profiles.Add(profile);
            registry.ActiveProfileId = profile.Id;
            SaveLocked(registry);
            return Clone(profile, true);
        }
    }

    public void ActivateProfile(string profileId)
    {
        ValidateProfileId(profileId);
        lock (_gate)
        {
            var registry = LoadOrCreateLocked();
            var profile = registry.Profiles.SingleOrDefault(value => value.Id == profileId)
                          ?? throw new InvalidOperationException("The selected organization profile no longer exists.");
            _ = GetProfileFolder(profile.Id);
            profile.LastUsedAtUtc = DateTime.UtcNow;
            registry.ActiveProfileId = profile.Id;
            SaveLocked(registry);
        }
    }

    public void UpdateActiveProfile(string tenantId, string organizationName, string storeName, string username)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        lock (_gate)
        {
            var registry = LoadOrCreateLocked();
            var profile = registry.Profiles.Single(value => value.Id == registry.ActiveProfileId);
            profile.TenantId = tenantId.Trim();
            profile.OrganizationName = organizationName?.Trim() ?? string.Empty;
            profile.StoreName = storeName?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(username))
                profile.Username = username.Trim();
            profile.LastUsedAtUtc = DateTime.UtcNow;
            SaveLocked(registry);
        }
    }

    public string GetDatabasePath(string profileId)
    {
        ValidateProfileId(profileId);
        return profileId == LegacyProfileId
            ? Path.Combine(_appFolder, "posapp.db")
            : Path.Combine(GetProfileFolder(profileId), "posapp.db");
    }

    public string GetTokenPath(string profileId)
    {
        ValidateProfileId(profileId);
        var securityFolder = profileId == LegacyProfileId
            ? Path.Combine(_appFolder, "Security")
            : Path.Combine(GetProfileFolder(profileId), "Security");
        Directory.CreateDirectory(securityFolder);
        return Path.Combine(securityFolder, "cloud-session.dat");
    }

    public string GetProfileFolder(string profileId)
    {
        ValidateProfileId(profileId);
        if (profileId == LegacyProfileId) return _appFolder;
        var folder = Path.Combine(_appFolder, "Profiles", profileId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private ProfileRegistry LoadOrCreateLocked()
    {
        ProfileRegistry? registry = null;
        if (File.Exists(_registryPath))
        {
            try
            {
                registry = JsonSerializer.Deserialize<ProfileRegistry>(File.ReadAllText(_registryPath), JsonOptions);
            }
            catch (JsonException)
            {
                PreserveCorruptRegistryLocked();
            }
            catch (IOException)
            {
                PreserveCorruptRegistryLocked();
            }
        }

        if (registry == null)
        {
            registry = NewLegacyRegistry();
            SaveLocked(registry);
            return registry;
        }

        if (registry.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException(
                "The local organization profile selector was created by an incompatible PosApp version.");

        registry.Profiles ??= new List<LocalOrganizationProfile>();
        registry.Profiles = registry.Profiles
            .Where(profile => IsValidProfileId(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (registry.Profiles.Count == 0)
            registry.Profiles.Add(NewLegacyProfile());
        if (!registry.Profiles.Any(profile => profile.Id == registry.ActiveProfileId))
            registry.ActiveProfileId = registry.Profiles[0].Id;
        return registry;
    }

    private void SaveLocked(ProfileRegistry registry)
    {
        var folder = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(folder);
        var temporary = _registryPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(registry, JsonOptions));
        File.Move(temporary, _registryPath, true);
    }

    private void PreserveCorruptRegistryLocked()
    {
        try
        {
            var folder = Path.GetDirectoryName(_registryPath)!;
            Directory.CreateDirectory(folder);
            var preserved = Path.Combine(folder,
                $"profiles-corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(_registryPath, preserved, overwrite: false);
        }
        catch
        {
            // A damaged selector must not block access to the preserved legacy
            // database. The original remains in place if the safety copy fails.
        }
    }

    private static ProfileRegistry NewLegacyRegistry()
        => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            ActiveProfileId = LegacyProfileId,
            Profiles = new List<LocalOrganizationProfile> { NewLegacyProfile() }
        };

    private static LocalOrganizationProfile NewLegacyProfile()
        => new()
        {
            Id = LegacyProfileId,
            CreatedAtUtc = DateTime.UtcNow,
            LastUsedAtUtc = DateTime.UtcNow
        };

    private static LocalOrganizationProfile Clone(LocalOrganizationProfile source, bool isActive)
        => new()
        {
            Id = source.Id,
            TenantId = source.TenantId,
            OrganizationName = source.OrganizationName,
            StoreName = source.StoreName,
            Username = source.Username,
            CreatedAtUtc = source.CreatedAtUtc,
            LastUsedAtUtc = source.LastUsedAtUtc,
            IsActive = isActive
        };

    private static bool IsValidProfileId(string? profileId)
        => profileId == LegacyProfileId ||
           (profileId?.Length == 32 && Guid.TryParseExact(profileId, "N", out _));

    private static void ValidateProfileId(string profileId)
    {
        if (!IsValidProfileId(profileId))
            throw new ArgumentException("The organization profile ID is invalid.", nameof(profileId));
    }

    private sealed class ProfileRegistry
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string ActiveProfileId { get; set; } = LegacyProfileId;
        public List<LocalOrganizationProfile> Profiles { get; set; } = new();
    }
}
