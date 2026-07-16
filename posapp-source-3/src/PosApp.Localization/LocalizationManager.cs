using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PosApp.Localization;

/// <summary>
/// Bilingual (EN / BN) string catalog. Switching <see cref="Culture"/>
/// raises a property-change that the WPF shell listens to and triggers
/// a <c>ResourceDictionary</c> swap so all bound UI re-renders.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationManager> _instance = new(() => new LocalizationManager());
    public static LocalizationManager Instance => _instance.Value;

    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (_culture.Name == value.Name) return;
            _culture = value;
            CultureInfo.DefaultThreadCurrentCulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            OnCultureChanged();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBengali));
        }
    }

    public bool IsBengali => _culture.Name.StartsWith("bn");

    public event EventHandler? CultureChanged;
    private void OnCultureChanged() => CultureChanged?.Invoke(this, EventArgs.Empty);

    public void SetLanguage(string code)
    {
        Culture = code switch
        {
            "bn" => CultureInfo.GetCultureInfo("bn-BD"),
            _ => CultureInfo.GetCultureInfo("en")
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
