using System.Windows;
using System.Windows.Controls;

namespace RigTune.App.Controls;

/// <summary>Lays children out horizontally with widths proportional to the attached <see cref="WeightProperty"/>.</summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetWeight(DependencyObject o) => (double)o.GetValue(WeightProperty);

    public static void SetWeight(DependencyObject o, double value) => o.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double total = InternalChildren.Cast<UIElement>().Sum(c => Math.Max(0, GetWeight(Container(c))));
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            double width = total <= 0 ? 0 : finalSize.Width * Math.Max(0, GetWeight(Container(child))) / total;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    private static DependencyObject Container(UIElement child) => child;
}
