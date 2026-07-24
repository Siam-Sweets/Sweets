using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PosApp.Wpf.Helpers;

internal static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                    ApplyTheme(window);
            }));
    }

    public static void ApplyTheme(Window window)
    {
        if (window == null) return;

        window.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextDarkBrush");

        if (window.Content is Panel panel && panel.Background == null)
            panel.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        else if (window.Content is Border border && border.Background == null)
            border.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        else if (window.Content is Control control)
            control.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");

        window.SourceInitialized -= OnWindowSourceInitialized;
        window.SourceInitialized += OnWindowSourceInitialized;

        if (window.IsLoaded)
            ApplyCaptionTheme(window);
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            ApplyCaptionTheme(window);
    }

    private static void ApplyCaptionTheme(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var enableDark = string.Equals(App.StoreSettings.Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enableDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enableDark, sizeof(int));
        }
        catch
        {
            // Non-client caption theming is best-effort. The client area still uses app brushes.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
