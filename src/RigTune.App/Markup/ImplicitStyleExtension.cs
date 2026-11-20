using System.Windows;
using System.Windows.Markup;

namespace RigTune.App.Markup;

/// <summary>
/// Returns the theme's implicit style for <see cref="TargetType"/>, or <c>null</c> when the theme does
/// not define one. Use as <c>BasedOn="{markup:ImplicitStyle {x:Type ListViewItem}}"</c>; unlike
/// <c>{StaticResource {x:Type ...}}</c> it never throws when the key is missing (WPF-UI 4 has no
/// implicit ListViewItem style, which crashed System Care on load).
/// </summary>
[MarkupExtensionReturnType(typeof(Style))]
public sealed class ImplicitStyleExtension : MarkupExtension
{
    public ImplicitStyleExtension()
    {
    }

    public ImplicitStyleExtension(Type targetType)
    {
        TargetType = targetType;
    }

    [ConstructorArgument("targetType")]
    public Type? TargetType { get; set; }

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        if (TargetType is null || Application.Current is null)
        {
            return null;
        }

        return Application.Current.TryFindResource(TargetType) is Style style && style.TargetType.IsAssignableFrom(TargetType)
            ? style
            : null;
    }
}
