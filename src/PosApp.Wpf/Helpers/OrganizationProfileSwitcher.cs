using System.Diagnostics;
using System.IO;
using System.Windows;
using PosApp.Core.Interfaces;
using PosApp.Data;
using PosApp.Services;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// Safely changes the process-wide organization cache. A restart is mandatory
/// because EF Core services are bound to one SQLite file at application start.
/// </summary>
internal static class OrganizationProfileSwitcher
{
    public static async Task SwitchAndRestartAsync(
        LocalOrganizationProfileStore profiles,
        ICloudSyncService sync,
        string profileId,
        bool createProfile,
        CancellationToken cancellationToken = default)
    {
        var previousProfileId = DbPathResolver.CurrentProfileId();

        // Best effort only: an offline or unavailable cloud must never prevent a
        // profile switch. Its isolated outbox remains in the original database.
        try { await sync.SyncNowAsync(false, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { App.LogError("Sync before organization profile switch", exception); }

        await sync.StopAsync(cancellationToken);
        await sync.WaitForIdleAsync(cancellationToken);

        LocalOrganizationProfile target;
        if (createProfile)
            target = profiles.CreateAndActivateProfile();
        else
        {
            profiles.ActivateProfile(profileId);
            target = profiles.GetProfiles().Single(profile => profile.Id == profileId);
        }

        try
        {
            await CloudDiagnosticLogger.WriteAsync("profile.restart_requested", "success",
                new Dictionary<string, object?>
                {
                    ["fromProfileId"] = previousProfileId,
                    ["toProfileId"] = target.Id,
                    ["newProfile"] = createProfile
                });

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidOperationException("PosApp could not locate its executable for a safe restart.");

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
            Application.Current.Shutdown();
        }
        catch
        {
            // Restore the selector if launching the replacement process fails.
            // The running DbContexts never moved away from the old database.
            profiles.ActivateProfile(previousProfileId);
            throw;
        }
    }
}
