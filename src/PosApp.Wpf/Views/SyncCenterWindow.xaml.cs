using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using PosApp.Core.Interfaces;
using PosApp.Core.Models;
using PosApp.Wpf.Helpers;

namespace PosApp.Wpf.Views;

public partial class SyncCenterWindow : Window
{
    private static readonly HashSet<string> HiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "StoreId", "SyncId", "CloudVersion", "SyncVersion", "SyncUpdatedAt", "ImagePath"
    };

    private readonly ICloudSyncService _sync;
    private readonly ICloudSyncCoordinator _coordinator;
    private SyncConflictRecord? _selectedConflict;
    private List<ConflictFieldRow> _fieldRows = new();

    public SyncCenterWindow(ICloudSyncService sync, ICloudSyncCoordinator coordinator)
    {
        InitializeComponent();
        _sync = sync;
        _coordinator = coordinator;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsEnabled = false;
        try
        {
            var snapshot = await _sync.GetSyncCenterAsync();
            StatusText.Text = FormatResource("SyncCenter_StatusSummary",
                "Pending: {0}. Conflicts: {1}. {2}",
                snapshot.Status.PendingChanges.ToString("N0"),
                snapshot.Status.ConflictCount.ToString("N0"),
                RuntimeUiText.Translate(snapshot.Status.Message));
            ConflictGrid.ItemsSource = snapshot.Conflicts;
            StoreGrid.ItemsSource = snapshot.Stores;
            QueueGrid.ItemsSource = snapshot.QueueIssues;
            DeviceGrid.ItemsSource = snapshot.Devices;
            HistoryGrid.ItemsSource = snapshot.Runs;
            ConflictGrid.SelectedItem = snapshot.Conflicts.FirstOrDefault();
            if (snapshot.Conflicts.Count == 0) ClearConflictDetails();
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.GetBaseException().Message,
                ResourceText("SyncCenter_LoadErrorTitle", "Unable to load sync center"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ConflictGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedConflict = ConflictGrid.SelectedItem as SyncConflictRecord;
        if (_selectedConflict == null)
        {
            ClearConflictDetails();
            return;
        }

        ConflictDetailText.Text = FormatResource("SyncCenter_ConflictDetail",
            "{0} · {1} · local {2}, cloud {3}. {4}",
            _selectedConflict.StoreName, _selectedConflict.EntityType,
            _selectedConflict.LocalOperation, _selectedConflict.RemoteOperation,
            RuntimeUiText.Translate(_selectedConflict.Message));
        _fieldRows = BuildFieldRows(_selectedConflict);
        FieldGrid.ItemsSource = _fieldRows;
        KeepLocalButton.IsEnabled = true;
        UseCloudButton.IsEnabled = true;
        MergeButton.IsEnabled = _selectedConflict.AllowsFieldMerge;
    }

    private void ClearConflictDetails()
    {
        _selectedConflict = null;
        _fieldRows = new List<ConflictFieldRow>();
        FieldGrid.ItemsSource = _fieldRows;
        ConflictDetailText.Text = ResourceText(
            "SyncCenter_NoConflicts", "No unresolved synchronization conflicts.");
        KeepLocalButton.IsEnabled = false;
        UseCloudButton.IsEnabled = false;
        MergeButton.IsEnabled = false;
    }

    private List<ConflictFieldRow> BuildFieldRows(SyncConflictRecord conflict)
    {
        var local = ParseObject(conflict.LocalPayloadJson);
        var cloud = ParseObject(conflict.RemotePayloadJson);
        var localLabel = ResourceText("SyncCenter_Local", "Local");
        var cloudLabel = ResourceText("SyncCenter_Cloud", "Cloud");
        return local.Select(x => x.Key).Union(cloud.Select(x => x.Key), StringComparer.OrdinalIgnoreCase)
            .Where(field => !HiddenFields.Contains(field) &&
                            (!field.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
                             field.EndsWith("SyncId", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(field => field, StringComparer.CurrentCultureIgnoreCase)
            .Select(field =>
            {
                var hasLocal = local.TryGetPropertyValue(field, out var localNode);
                var hasCloud = cloud.TryGetPropertyValue(field, out var cloudNode);
                return new ConflictFieldRow(
                    field, localNode?.DeepClone(), cloudNode?.DeepClone(), hasLocal, hasCloud,
                    localLabel, cloudLabel);
            }).ToList();
    }

    private async void KeepLocal_Click(object sender, RoutedEventArgs e)
        => await ResolveAsync(SyncConflictResolutionMode.KeepLocal, null);

    private async void UseCloud_Click(object sender, RoutedEventArgs e)
        => await ResolveAsync(SyncConflictResolutionMode.UseCloud, null);

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedConflict == null || !_selectedConflict.AllowsFieldMerge) return;
        var merged = ParseObject(_selectedConflict.LocalPayloadJson);
        foreach (var row in _fieldRows.Where(x => x.UseCloud))
        {
            if (row.CloudExists) merged[row.Field] = row.CloudNode?.DeepClone();
            else merged.Remove(row.Field);
        }
        await ResolveAsync(SyncConflictResolutionMode.Merge,
            merged.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    private async Task ResolveAsync(SyncConflictResolutionMode mode, string? mergedPayload)
    {
        if (_selectedConflict == null) return;
        var answer = LocalizedMessageBox.Show(
            ResourceText("SyncCenter_ConfirmResolution",
                "Apply this conflict resolution? The selected record will be updated and synchronization will retry."),
            ResourceText("SyncCenter_ConfirmTitle", "Resolve synchronization conflict"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        IsEnabled = false;
        try
        {
            await _sync.ResolveConflictAsync(new SyncConflictResolutionRequest
            {
                ConflictId = _selectedConflict.Id,
                Mode = mode,
                MergedPayloadJson = mergedPayload
            });
            _coordinator.Trigger();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.GetBaseException().Message,
                ResourceText("SyncCenter_ResolveErrorTitle", "Unable to resolve conflict"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            await _sync.SyncNowAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.GetBaseException().Message, "Cloud synchronization failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            var count = await _sync.RetryFailedChangesAsync();
            _coordinator.Trigger();
            await RefreshAsync();
            StatusText.Text = FormatResource("SyncCenter_RetrySummary",
                "Reset {0} failed change(s) for retry.", count.ToString("N0"));
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.GetBaseException().Message,
                ResourceText("SyncCenter_RetryErrorTitle", "Unable to retry changes"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void ClearResolved_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = await _sync.ClearResolvedConflictsAsync();
            await RefreshAsync();
            StatusText.Text = FormatResource("SyncCenter_ClearSummary",
                "Cleared {0} resolved conflict record(s).", count.ToString("N0"));
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(ex.GetBaseException().Message,
                ResourceText("SyncCenter_ClearErrorTitle", "Unable to clear resolved conflicts"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private static JsonObject ParseObject(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string ResourceText(string key, string fallback)
        => Application.Current.TryFindResource(key)?.ToString() ?? fallback;

    private static string FormatResource(string key, string fallback, params object[] values)
        => string.Format(ResourceText(key, fallback), values);

    private sealed class ConflictFieldRow : INotifyPropertyChanged
    {
        private string _choice;

        public ConflictFieldRow(
            string field, JsonNode? localNode, JsonNode? cloudNode,
            bool localExists, bool cloudExists, string localChoice, string cloudChoice)
        {
            Field = field;
            LocalNode = localNode;
            CloudNode = cloudNode;
            LocalExists = localExists;
            CloudExists = cloudExists;
            Choices = new[] { localChoice, cloudChoice };
            _choice = localChoice;
        }

        public string Field { get; }
        public JsonNode? LocalNode { get; }
        public JsonNode? CloudNode { get; }
        public bool LocalExists { get; }
        public bool CloudExists { get; }
        public IReadOnlyList<string> Choices { get; }
        public string LocalValue => Format(LocalNode, LocalExists);
        public string CloudValue => Format(CloudNode, CloudExists);
        public bool UseCloud => Choice == Choices[1];

        public string Choice
        {
            get => _choice;
            set
            {
                if (_choice == value) return;
                _choice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Choice)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseCloud)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string Format(JsonNode? node, bool exists)
        {
            if (!exists) return "—";
            if (node == null) return "null";
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
            return node.ToJsonString();
        }
    }
}
