using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// ViewModel base class implementing INotifyPropertyChanged with a
/// helper to set backing fields and raise the change event.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void OnPropertyChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
