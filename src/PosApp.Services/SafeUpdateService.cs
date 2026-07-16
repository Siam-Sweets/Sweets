using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Offline updater coordinator. It accepts only a newer local PosApp setup
/// executable, snapshots and validates SQLite before launch, records a SHA-256
/// digest, and leaves the recovery backup outside the installation directory.
/// </summary>
public sealed class SafeUpdateService : IUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IBackupService _backup;
    private readonly Version _currentVersion;

    public SafeUpdateService(IBackupService backup)
    {
        _backup = backup;
        _currentVersion = NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version);
    }

    public string CurrentVersion => DisplayVersion(_currentVersion);
    public string DataFolder => DbPathResolver.AppFolder();
    public string UpdateBackupFolder => DbPathResolver.UpdateBackupFolder();

    public async Task<SafeUpdateRecord?> EnsurePreMigrationBackupAsync()
    {
        var databasePath = DbPathResolver.DefaultPath();
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0) return null;

        var pending = await GetPendingUpdateAsync();
        if (pending != null && File.Exists(pending.BackupPath))
            return pending;

        var lastVersion = await ReadTextAsync(DbPathResolver.LastSuccessfulVersionPath());
        if (string.Equals(lastVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase))
            return null;

        Directory.CreateDirectory(UpdateBackupFolder);
        var fromVersion = string.IsNullOrWhiteSpace(lastVersion) ? "unknown" : lastVersion;
        var backupPath = Path.Combine(UpdateBackupFolder,
            $"posapp-before-startup-{SafeVersionForFileName(fromVersion)}-to-" +
            $"{SafeVersionForFileName(CurrentVersion)}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
        await _backup.CreateBackupAsync(backupPath);
        await _backup.ValidateBackupAsync(backupPath);

        var record = new SafeUpdateRecord
        {
            State = "PreMigrationBackup",
            FromVersion = fromVersion,
            TargetVersion = CurrentVersion,
            RunningVersion = CurrentVersion,
            BackupPath = backupPath,
            DatabasePath = databasePath,
            PreparedAtUtc = DateTime.UtcNow
        };
        await WriteRecordAsync(DbPathResolver.PendingUpdateRecordPath(), record);
        PruneOldUpdateBackups(10);
        return record;
    }

    public async Task<SafeUpdatePackageInfo> InspectInstallerAsync(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
            return Invalid(installerPath, "Choose a PosApp setup installer.");

        string fullPath;
        try { fullPath = Path.GetFullPath(installerPath); }
        catch { return Invalid(installerPath, "The selected installer path is invalid."); }

        if (fullPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return Invalid(fullPath, "Copy the installer to this computer before updating.");
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network)
                return Invalid(fullPath, "Copy the installer from the network drive to this computer before updating.");
        }
        catch
        {
            return Invalid(fullPath, "Windows could not verify that the installer is on a local drive.");
        }
        if (!File.Exists(fullPath))
            return Invalid(fullPath, "The selected installer no longer exists.");

        var fileName = Path.GetFileName(fullPath) ?? string.Empty;
        if (!fileName.StartsWith("PosApp-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
            return Invalid(fullPath, "Select a PosApp-<version>-Setup.exe release installer.");

        if (!await HasPortableExecutableHeaderAsync(fullPath))
            return Invalid(fullPath, "The selected file is not a Windows executable.");

        FileVersionInfo versionInfo;
        try { versionInfo = FileVersionInfo.GetVersionInfo(fullPath); }
        catch (Exception ex) { return Invalid(fullPath, $"Windows could not read the installer metadata: {ex.Message}"); }

        var productName = versionInfo.ProductName?.Trim() ?? string.Empty;
        if (!string.Equals(productName, "PosApp", StringComparison.OrdinalIgnoreCase))
            return Invalid(fullPath, "The selected setup file is not identified as a PosApp installer.");

        var targetVersion = ParseVersion(versionInfo.ProductVersion ?? versionInfo.FileVersion);
        if (targetVersion == null)
            return Invalid(fullPath, "The installer does not contain a valid PosApp version.");
        if (targetVersion.CompareTo(_currentVersion) <= 0)
            return Invalid(fullPath,
                $"This computer has PosApp {CurrentVersion}. Choose a newer installer than {DisplayVersion(targetVersion)}.");

        var hash = await ComputeSha256Async(fullPath);
        return new SafeUpdatePackageInfo
        {
            InstallerPath = fullPath,
            FileName = fileName,
            ProductName = productName,
            CurrentVersion = CurrentVersion,
            TargetVersion = DisplayVersion(targetVersion),
            Sha256 = hash,
            SizeBytes = new FileInfo(fullPath).Length,
            IsValid = true,
            ValidationMessage = "Newer PosApp installer verified. Confirm that it came from your trusted PosApp release source."
        };
    }

    public async Task<SafeUpdateLaunchResult> PrepareAndLaunchAsync(string installerPath)
    {
        var package = await InspectInstallerAsync(installerPath);
        if (!package.IsValid)
            throw new InvalidOperationException(package.ValidationMessage);

        Directory.CreateDirectory(UpdateBackupFolder);
        var safeFrom = SafeVersionForFileName(package.CurrentVersion);
        var safeTo = SafeVersionForFileName(package.TargetVersion);
        var backupPath = Path.Combine(UpdateBackupFolder,
            $"posapp-before-update-{safeFrom}-to-{safeTo}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");

        await _backup.CreateBackupAsync(backupPath);
        await _backup.ValidateBackupAsync(backupPath);

        // Detect a replaced or modified installer between inspection and launch.
        await using var installerLock = new FileStream(package.InstallerPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var launchHash = await ComputeSha256Async(installerLock);
        if (!string.Equals(launchHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The installer changed while the safety backup was being created. The update was stopped; the backup was kept.");

        var logPath = Path.Combine(DbPathResolver.UpdateFolder(),
            $"installer-{safeTo}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var record = new SafeUpdateRecord
        {
            State = "Prepared",
            FromVersion = package.CurrentVersion,
            TargetVersion = package.TargetVersion,
            InstallerPath = package.InstallerPath,
            InstallerSha256 = package.Sha256,
            BackupPath = backupPath,
            DatabasePath = DbPathResolver.DefaultPath(),
            InstallerLogPath = logPath,
            PreparedAtUtc = DateTime.UtcNow
        };

        await WriteRecordAsync(DbPathResolver.PendingUpdateRecordPath(), record);

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = package.InstallerPath,
                Arguments = $"/CLOSEAPPLICATIONS /LOG=\"{logPath}\"",
                WorkingDirectory = Path.GetDirectoryName(package.InstallerPath) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            });
            if (process == null)
                throw new InvalidOperationException("Windows did not start the setup installer.");

            record.State = "InstallerLaunched";
            record.InstallerProcessId = process.Id;
            await WriteRecordAsync(DbPathResolver.PendingUpdateRecordPath(), record);
            PruneOldUpdateBackups(10);

            return new SafeUpdateLaunchResult { Package = package, Record = record };
        }
        catch (Exception ex)
        {
            record.State = "LaunchFailed";
            record.FailureMessage = ex.Message;
            record.CompletedAtUtc = DateTime.UtcNow;
            await WriteRecordAsync(DbPathResolver.LastUpdateRecordPath(), record);
            TryDelete(DbPathResolver.PendingUpdateRecordPath());
            throw new InvalidOperationException(
                $"The installer could not be started. Your verified pre-update backup was kept at:\n{backupPath}", ex);
        }
    }

    public Task<SafeUpdateRecord?> GetPendingUpdateAsync()
        => ReadRecordAsync(DbPathResolver.PendingUpdateRecordPath());

    public Task<SafeUpdateRecord?> GetLastUpdateAsync()
        => ReadRecordAsync(DbPathResolver.LastUpdateRecordPath());

    public async Task<SafeUpdateRecord?> MarkStartupSuccessfulAsync()
    {
        var record = await GetPendingUpdateAsync();
        if (record != null)
        {
            record.RunningVersion = CurrentVersion;
            record.CompletedAtUtc = DateTime.UtcNow;
            var target = ParseVersion(record.TargetVersion);
            record.State = target != null && _currentVersion.CompareTo(target) >= 0
                ? "Completed"
                : "InstallerNotApplied";
            await WriteRecordAsync(DbPathResolver.LastUpdateRecordPath(), record);
            TryDelete(DbPathResolver.PendingUpdateRecordPath());
        }

        await WriteTextAtomicallyAsync(DbPathResolver.LastSuccessfulVersionPath(), CurrentVersion);
        return record;
    }

    private SafeUpdatePackageInfo Invalid(string? path, string message) => new()
    {
        InstallerPath = path ?? string.Empty,
        FileName = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path) ?? string.Empty,
        CurrentVersion = CurrentVersion,
        IsValid = false,
        ValidationMessage = message
    };

    private static async Task<bool> HasPortableExecutableHeaderAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[2];
            var count = await stream.ReadAsync(header);
            return count == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch { return false; }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeSha256Async(stream);
    }

    private static async Task<string> ComputeSha256Async(Stream stream)
    {
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version NormalizeVersion(Version? version) => new(
        Math.Max(version?.Major ?? 0, 0),
        Math.Max(version?.Minor ?? 0, 0),
        Math.Max(version?.Build ?? 0, 0),
        Math.Max(version?.Revision ?? 0, 0));

    private static Version? ParseVersion(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var version)
            ? NormalizeVersion(version)
            : null;
    }

    private static string DisplayVersion(Version version)
        => version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    private static string SafeVersionForFileName(string version)
        => Regex.Replace(version, @"[^0-9A-Za-z.-]", "-");

    private static async Task WriteRecordAsync(string path, SafeUpdateRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<SafeUpdateRecord?> ReadRecordAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SafeUpdateRecord>(await File.ReadAllTextAsync(path), JsonOptions);
        }
        catch { return null; }
    }

    private static async Task<string?> ReadTextAsync(string path)
    {
        try { return File.Exists(path) ? (await File.ReadAllTextAsync(path)).Trim() : null; }
        catch { return null; }
    }

    private static async Task WriteTextAtomicallyAsync(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Recovery metadata must not crash startup. */ }
    }

    private static void PruneOldUpdateBackups(int keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(DbPathResolver.UpdateBackupFolder())
                         .GetFiles("posapp-before-*.db")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(Math.Clamp(keep, 2, 50)))
                old.Delete();
        }
        catch { /* A locked backup is safer to keep than to fail an update. */ }
    }
}
