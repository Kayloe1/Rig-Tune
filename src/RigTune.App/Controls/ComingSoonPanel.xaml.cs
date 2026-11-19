using System.Windows;
using System.Windows.Controls;

namespace RigTune.App.Controls;

/// <summary>Placeholder body for modules scheduled in later build phases.</summary>
public partial class ComingSoonPanel : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register(nameof(Phase), typeof(string), typeof(ComingSoonPanel), new PropertyMetadata("Coming in a later phase"));

    public ComingSoonPanel()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string Phase
    {
        get => (string)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public List<string> Features { get; } = new();
}
