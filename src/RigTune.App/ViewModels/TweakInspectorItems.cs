using CommunityToolkit.Mvvm.ComponentModel;

namespace RigTune.App.ViewModels;

public sealed partial class SliderItem : ObservableObject
{
    public SliderItem(string key, string label, double min, double max, double step, string? unit, double value)
    {
        Key = key;
        Label = label;
        Min = min;
        Max = max;
        Step = step;
        Unit = unit ?? string.Empty;
        _value = value;
    }

    public string Key { get; }
    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public string Unit { get; }

    [ObservableProperty]
    private double _value;
}

/// <summary>A selectable device / adapter row in the inspector.</summary>
public sealed partial class SelectableItem : ObservableObject
{
    public SelectableItem(string id, string name, string detail, string? badge = null, bool selected = false)
    {
        Id = id;
        Name = name;
        Detail = detail;
        Badge = badge;
        _isSelected = selected;
    }

    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public string? Badge { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One logical processor in the interrupt-affinity core picker.</summary>
public sealed partial class CoreItem : ObservableObject
{
    public CoreItem(int index, bool isPerformance)
    {
        Index = index;
        IsPerformance = isPerformance;
    }

    public int Index { get; }
    public bool IsPerformance { get; }
    public string Label => $"{(IsPerformance ? "P" : "E")}{Index}";
    public string Tooltip => $"Logical processor {Index} — {(IsPerformance ? "Performance core (P-core)" : "Efficient core (E-core)")}";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record ChoiceItem(string Id, string Name, string? Description = null);
