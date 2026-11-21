using System.Windows;

namespace Universum.App.Pages;

/// <summary>
/// Hands the data of a page to the parts of a table that are not in its visual tree (a column's visibility – owner,
/// 21. 11. 2026: the columns of the variants of a title in Hromadné spracovanie). Put into the resources of the element
/// whose data it carries.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
