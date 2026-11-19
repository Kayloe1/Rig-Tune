using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RigTune.App.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        long l => Format.Bytes(l),
        ulong u => Format.Bytes(u),
        int i => Format.Bytes(i),
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:0} {Units[unit]}" : $"{bytes:0.#} {Units[unit]}";
    }

    public static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m" : $"{span.Hours}h {span.Minutes}m";
}

/// <summary>Visible when the bound count is zero (empty-state messages).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Display text for the Tweaks filter pills.</summary>
public sealed class FilterLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RigTune.App.ViewModels.TweakFilter.NeedsRestart => "Needs Restart",
        RigTune.App.ViewModels.TweakFilter.DangerZone => "Danger Zone",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Number of Recommended tweaks in a CollectionViewGroup's items.</summary>
public sealed class RecommendedCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is System.Collections.IEnumerable items
            ? items.OfType<RigTune.App.ViewModels.TweakItemViewModel>().Count(i => i.IsRecommended)
            : 0;
        return targetType == typeof(Visibility) ? (count > 0 ? Visibility.Visible : Visibility.Collapsed) : count;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ActionStatusLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RigTune.Core.Models.ActionStatus.Applied => "✓",
        RigTune.Core.Models.ActionStatus.NotApplied => "○",
        RigTune.Core.Models.ActionStatus.NotApplicable => "—",
        RigTune.Core.Models.ActionStatus.Informational => "i",
        RigTune.Core.Models.ActionStatus.Unsupported => "·",
        _ => "?",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound count is greater than zero.</summary>
public sealed class NonZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
