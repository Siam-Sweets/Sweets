using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PosApp.Core.Models;
using PosApp.Data;

namespace PosApp.Services;

/// <summary>
/// Writes privacy-safe, structured diagnostics for online authentication and
/// synchronization. Logging is deliberately best-effort and must never block
/// local POS work or expose credentials, tokens, request payloads, or customer
/// data.
/// </summary>
public static class CloudDiagnosticLogger
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private const int RetainedLogFiles = 4;
    private const int MaximumMessageLength = 1200;
    private const int MaximumStackLength = 12000;

    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly AsyncLocal<string?> ActiveAttempt = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Regex SensitiveAssignmentPattern = new(
        "(?i)(password|passphrase|pin|token|authorization|credential|secret|cookie)\\s*[:=]\\s*[^,;\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerPattern = new(
        "(?i)bearer\\s+[A-Za-z0-9._~+\\-/=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new(
        "(?i)(?<![A-Z0-9._%+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}(?![A-Z0-9.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlQueryPattern = new(
        "(?i)(https?://[^?\\s]+)\\?[^\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SqlStatementPattern = new(
        "(?is)\\b(SELECT\\s+.+?\\s+FROM\\s+|INSERT\\s+INTO\\s+|" +
        "UPDATE\\s+[\"`\\[]?[A-Z_][A-Z0-9_]*[\"`\\]]?\\s+SET\\s+|DELETE\\s+FROM\\s+|" +
        "CREATE\\s+TABLE\\s+|ALTER\\s+TABLE\\s+|DROP\\s+TABLE\\s+|PRAGMA\\s+)[^;]*(;|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsUserPathPattern = new(
        "(?i)([A-Z]:\\\\Users\\\\)[^\\\\\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string LogDirectoryPath
    {
        get
        {
            try
            {
                var directory = Path.Combine(DbPathResolver.AppFolder(), "Logs");
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch
            {
                var fallback = Path.Combine(Path.GetTempPath(), "PosApp", "Logs");
                try { Directory.CreateDirectory(fallback); }
                catch { /* The write path remains best-effort. */ }
                return fallback;
            }
        }
    }

    public static string LogFilePath => Path.Combine(LogDirectoryPath, "cloud-sync.jsonl");

    public static bool HasActiveAttempt => !string.IsNullOrWhiteSpace(ActiveAttempt.Value);

    public static string CreateAttemptId()
        => Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    public static IDisposable BeginScope(string attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId))
            throw new ArgumentException("A diagnostic attempt ID is required.", nameof(attemptId));

        var previous = ActiveAttempt.Value;
        ActiveAttempt.Value = attemptId.Trim();
        return new AttemptScope(previous);
    }

    public static IDisposable SuppressScope()
    {
        var previous = ActiveAttempt.Value;
        ActiveAttempt.Value = null;
        return new AttemptScope(previous);
    }

    public static Task WriteAsync(
        string stage,
        string outcome = "info",
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? exception = null)
        => WriteCoreAsync(
            ActiveAttempt.Value ?? "BACKGROUND",
            stage,
            outcome,
            details,
            exception);

    public static Task WriteStatusAsync(
        string stage,
        CloudSyncStatus status,
        string outcome = "status",
        IReadOnlyDictionary<string, object?>? additionalDetails = null,
        Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["syncState"] = status.State,
            ["signedIn"] = status.IsSignedIn,
            ["networkReportedOnline"] = status.IsOnline,
            ["syncing"] = status.IsSyncing,
            ["deviceRevoked"] = status.IsDeviceRevoked,
            ["requiresReconciliation"] = status.RequiresReconciliation,
            ["pendingUploads"] = status.PendingUploadCount,
            ["conflicts"] = status.ConflictCount,
            ["downloadedChanges"] = status.DownloadedChangeCount,
            ["cursor"] = status.Cursor,
            ["lastSuccessfulSyncAtUtc"] = status.LastSuccessfulSyncAtUtc,
            ["errorCode"] = status.LastErrorCode,
            ["errorMessage"] = status.LastErrorMessage,
            ["requestId"] = status.LastRequestId
        };

        if (additionalDetails != null)
            foreach (var pair in additionalDetails)
                details[pair.Key] = pair.Value;

        return WriteAsync(stage, outcome, details, exception);
    }

    private static async Task WriteCoreAsync(
        string attemptId,
        string stage,
        string outcome,
        IReadOnlyDictionary<string, object?>? details,
        Exception? exception)
    {
        try
        {
            var entry = new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTime.UtcNow,
                ["attemptId"] = SanitizeText(attemptId, 64),
                ["clientVersion"] = CloudProtocol.ClientVersion,
                ["apiVersion"] = CloudProtocol.ApiVersion,
                ["clientSchemaVersion"] = CloudProtocol.ClientSchemaVersion,
                ["stage"] = SanitizeText(stage, 160),
                ["outcome"] = SanitizeText(outcome, 80),
                ["details"] = SanitizeDetails(details),
                ["exception"] = ExceptionDetails(exception)
            };

            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            await WriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var path = LogFilePath;
                RotateIfRequired(path, Encoding.UTF8.GetByteCount(line));
                await File.AppendAllTextAsync(path, line, Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                WriteGate.Release();
            }
        }
        catch
        {
            // Diagnostics must never make authentication, sync, or local work fail.
        }
    }

    private static IReadOnlyDictionary<string, object?>? SanitizeDetails(
        IReadOnlyDictionary<string, object?>? details)
    {
        if (details == null || details.Count == 0) return null;

        var sanitized = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in details)
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            sanitized[key] = IsSensitiveKey(key)
                ? "[redacted]"
                : SanitizeValue(pair.Value);
        }
        return sanitized;
    }

    private static object? SanitizeValue(object? value)
        => value switch
        {
            null => null,
            string text => SanitizeText(text, MaximumMessageLength),
            DateTime dateTime => dateTime.ToUniversalTime(),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal => value,
            _ => SanitizeText(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ??
                              string.Empty, MaximumMessageLength)
        };

    private static IReadOnlyDictionary<string, object?>? ExceptionDetails(Exception? exception)
    {
        if (exception == null) return null;
        return new Dictionary<string, object?>
        {
            ["type"] = exception.GetType().FullName,
            ["hResult"] = $"0x{exception.HResult:X8}",
            ["message"] = SanitizeText(exception.Message, MaximumMessageLength),
            ["stackTrace"] = SanitizeText(exception.StackTrace, MaximumStackLength),
            ["innerType"] = exception.InnerException?.GetType().FullName,
            ["innerHResult"] = exception.InnerException == null
                ? null
                : $"0x{exception.InnerException.HResult:X8}",
            ["innerMessage"] = SanitizeText(exception.InnerException?.Message, MaximumMessageLength)
        };
    }

    private static bool IsSensitiveKey(string key)
        => key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("sql", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("username", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("email", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("customer", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("phone", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("address", StringComparison.OrdinalIgnoreCase);

    private static string? SanitizeText(string? text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sanitized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        sanitized = SensitiveAssignmentPattern.Replace(sanitized, "$1=[redacted]");
        sanitized = BearerPattern.Replace(sanitized, "Bearer [redacted]");
        sanitized = EmailPattern.Replace(sanitized, "[email-redacted]");
        sanitized = UrlQueryPattern.Replace(sanitized, "$1?[redacted]");
        sanitized = SqlStatementPattern.Replace(sanitized, "[sql-redacted]");
        sanitized = WindowsUserPathPattern.Replace(sanitized, "$1[user]");
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength] + "…";
    }

    private static void RotateIfRequired(string path, int incomingBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaximumLogBytes)
            return;

        var oldest = $"{path}.{RetainedLogFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = RetainedLogFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{path}.{index + 1}", overwrite: true);
        }
        File.Move(path, $"{path}.1", overwrite: true);
    }

    private sealed class AttemptScope : IDisposable
    {
        private readonly string? _previousAttempt;
        private bool _disposed;

        public AttemptScope(string? previousAttempt)
        {
            _previousAttempt = previousAttempt;
        }

        public void Dispose()
        {
            if (_disposed) return;
            ActiveAttempt.Value = _previousAttempt;
            _disposed = true;
        }
    }
}
