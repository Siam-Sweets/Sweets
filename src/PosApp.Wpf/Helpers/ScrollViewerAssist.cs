using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// Makes the mouse wheel move the nearest scrollable management surface and,
/// when an inner grid reaches its edge, continue with the containing page.
/// WPF otherwise lets nested DataGrids and ComboBoxes swallow those wheel events.
/// </summary>
public static class ScrollViewerAssist
{
    public static readonly DependencyProperty EnableNestedMouseWheelProperty =
        DependencyProperty.RegisterAttached(
            "EnableNestedMouseWheel",
            typeof(bool),
            typeof(ScrollViewerAssist),
            new PropertyMetadata(false, EnableNestedMouseWheelChanged));

    public static void SetEnableNestedMouseWheel(DependencyObject element, bool value)
        => element.SetValue(EnableNestedMouseWheelProperty, value);

    public static bool GetEnableNestedMouseWheel(DependencyObject element)
        => (bool)element.GetValue(EnableNestedMouseWheelProperty);

    private static void EnableNestedMouseWheelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element) return;
        if ((bool)args.OldValue)
            element.RemoveHandler(UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(PreviewMouseWheel));
        if ((bool)args.NewValue)
            element.AddHandler(UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(PreviewMouseWheel), true);
    }

    private static void PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || e.OriginalSource is not DependencyObject source) return;

        var boundary = sender as DependencyObject;
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ScrollViewer viewer && CanMove(viewer, e.Delta))
            {
                var wheelLines = SystemParameters.WheelScrollLines;
                var distance = wheelLines < 0
                    ? Math.Max(48d, viewer.ViewportHeight * 0.9d)
                    : Math.Max(48d, wheelLines * 16d) * Math.Max(1d, Math.Abs(e.Delta) / 120d);
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (e.Delta < 0 ? distance : -distance));
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(current, boundary)) break;
        }
    }

    private static bool CanMove(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0.5d) return false;
        return delta > 0
            ? viewer.VerticalOffset > 0.5d
            : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5d;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is FrameworkContentElement content) return content.Parent;
        try { return VisualTreeHelper.GetParent(child); }
        catch (InvalidOperationException) { return LogicalTreeHelper.GetParent(child); }
    }
}
