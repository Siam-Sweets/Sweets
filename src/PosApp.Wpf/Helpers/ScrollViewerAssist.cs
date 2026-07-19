using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// Routes mouse-wheel input to the nearest scrollable management surface.
/// Logical scrollers such as DataGrid are moved by rows rather than by pixel
/// offsets, preventing one wheel detent from jumping dozens of records.
/// </summary>
public static class ScrollViewerAssist
{
    private const int WheelDeltaPerDetent = 120;
    private static readonly ConditionalWeakTable<ScrollViewer, WheelAccumulator> WheelAccumulators = new();

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

        var viewer = FindScrollableViewer(source, sender as DependencyObject, e.Delta);
        if (viewer == null) return;

        // Stop the DataGrid/outer ScrollViewer from also processing the same
        // wheel event after we have routed it to the correct surface.
        e.Handled = true;

        var wheelLines = SystemParameters.WheelScrollLines;
        if (wheelLines == 0) return;

        var accumulator = WheelAccumulators.GetValue(viewer, static _ => new WheelAccumulator());
        var direction = Math.Sign(e.Delta);
        if (accumulator.PendingDelta != 0 && Math.Sign(accumulator.PendingDelta) != direction)
            accumulator.PendingDelta = 0;

        accumulator.PendingDelta += e.Delta;
        var absoluteDelta = Math.Abs(accumulator.PendingDelta);
        var detents = absoluteDelta / WheelDeltaPerDetent;
        if (detents == 0) return;

        // A very large device delta must not turn one gesture into a jump to
        // the end. Discard excess whole detents and retain only sub-detent input.
        detents = Math.Min(detents, 3);
        accumulator.PendingDelta = direction * (absoluteDelta % WheelDeltaPerDetent);

        ScrollViewerByWheel(viewer, direction, detents, wheelLines);
    }

    private static ScrollViewer? FindScrollableViewer(
        DependencyObject source,
        DependencyObject? boundary,
        int delta)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ScrollViewer viewer && CanMove(viewer, delta))
                return viewer;

            if (ReferenceEquals(current, boundary)) break;
        }

        return null;
    }

    private static void ScrollViewerByWheel(
        ScrollViewer viewer,
        int direction,
        int detents,
        int wheelLines)
    {
        var scrollDown = direction < 0;

        if (wheelLines < 0)
        {
            for (var index = 0; index < detents; index++)
            {
                if (scrollDown) viewer.PageDown();
                else viewer.PageUp();
            }
            return;
        }

        if (viewer.CanContentScroll)
        {
            // DataGrid uses logical offsets, where adding 48 means 48 rows.
            // Move by the configured number of lines instead.
            var lineCount = Math.Clamp(Math.Max(1, wheelLines) * detents, 1, 12);
            for (var index = 0; index < lineCount; index++)
            {
                if (scrollDown) viewer.LineDown();
                else viewer.LineUp();
            }
            return;
        }

        var distance = Math.Max(1, wheelLines) * 16d * detents;
        if (viewer.ViewportHeight > 0)
            distance = Math.Min(distance, Math.Max(48d, viewer.ViewportHeight * 0.45d));

        viewer.ScrollToVerticalOffset(
            viewer.VerticalOffset + (scrollDown ? distance : -distance));
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

    private sealed class WheelAccumulator
    {
        public int PendingDelta { get; set; }
    }
}
