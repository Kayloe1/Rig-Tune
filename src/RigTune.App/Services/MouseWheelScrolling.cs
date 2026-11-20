using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RigTune.App.Services;

/// <summary>
/// App-wide mouse-wheel fix. Some theme templates swallow the wheel (or a nested list that cannot scroll
/// keeps it), so pages with a scrollbar didn't react. On every wheel turn this scrolls the innermost
/// ScrollViewer under the mouse that can still move in that direction, falling through to outer ones.
/// </summary>
public static class MouseWheelScrolling
{
    private const double PixelsPerNotch = 48;

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: false);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (DependencyObject? node = source; node is not null; node = Parent(node))
        {
            // Let controls that use the wheel themselves keep it (open drop-downs, sliders).
            if (node is ComboBox { IsDropDownOpen: true } or Slider)
            {
                return;
            }

            if (node is ScrollViewer viewer && TryScroll(viewer, e.Delta))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private static bool TryScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0)
        {
            return false;
        }

        bool up = delta > 0;
        if (up ? viewer.VerticalOffset <= 0 : viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5)
        {
            return false; // already at the edge — let an outer scroller take it
        }

        int notches = Math.Max(1, Math.Abs(delta) / 120);
        if (viewer.CanContentScroll)
        {
            // Item-based scrolling (virtualized lists): one line per item, three per notch like Windows.
            for (int i = 0; i < notches * SystemParameters.WheelScrollLines; i++)
            {
                if (up)
                {
                    viewer.LineUp();
                }
                else
                {
                    viewer.LineDown();
                }
            }
        }
        else
        {
            double offset = viewer.VerticalOffset + (up ? -1 : 1) * notches * PixelsPerNotch;
            viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
        }

        return true;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
