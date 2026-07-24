namespace PosApp.Data;

/// <summary>Groups all outbox rows created by one business command.</summary>
public static class SyncOperationScope
{
    private static readonly AsyncLocal<string?> Current = new();
    public static string? CurrentOperationId => Current.Value;

    public static IDisposable Begin(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        var previous = Current.Value;
        Current.Value = operationId.Trim();
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            Current.Value = previous;
            _disposed = true;
        }
    }
}
