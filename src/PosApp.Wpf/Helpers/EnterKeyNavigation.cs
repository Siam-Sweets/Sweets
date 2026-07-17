using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// Applies form-style Enter-key navigation to every application window.
/// Controls that already handle Enter keep their existing behavior because
/// this handler runs only after an unhandled bubbling KeyDown event reaches
/// the containing Window.
/// </summary>
internal static class EnterKeyNavigation
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;

        EventManager.RegisterClassHandler(
            typeof(Window),
            Keyboard.KeyDownEvent,
            new KeyEventHandler(OnWindowKeyDown));

        _registered = true;
    }

    private static void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled ||
            (e.Key != Key.Enter && e.Key != Key.Return) ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var source = Keyboard.FocusedElement as DependencyObject
                     ?? e.OriginalSource as DependencyObject;
        var input = FindInputControl(source);
        if (input == null || !input.IsEnabled || !input.IsKeyboardFocusWithin)
            return;

        // Enter must remain available for line breaks in notes, addresses,
        // comments, descriptions, and any other multiline editor.
        if (input is TextBox { AcceptsReturn: true })
            return;

        // Let an open ComboBox use Enter to accept its highlighted item.
        if (input is ComboBox { IsDropDownOpen: true })
            return;

        if (input.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
            e.Handled = true;
    }

    private static UIElement? FindInputControl(DependencyObject? source)
    {
        TextBox? textBox = null;

        for (var current = source; current != null; current = GetParent(current))
        {
            // Prefer the owning ComboBox over its internal editable TextBox.
            if (current is ComboBox comboBox)
                return comboBox;

            if (current is PasswordBox passwordBox)
                return passwordBox;

            if (current is TextBox candidate)
                textBox ??= candidate;
        }

        return textBox;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual || child is Visual3D)
            return VisualTreeHelper.GetParent(child);

        return LogicalTreeHelper.GetParent(child);
    }
}
