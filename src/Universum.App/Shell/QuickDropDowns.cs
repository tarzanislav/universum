using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Universum.App.Shell;

/// <summary>
/// The drop-down lists of the Fluent look scroll their open list by pixels, and that turns off the making of only the
/// visible items: the list of all languages MKVToolNix knows (thousands) was made whole each time it opened and
/// UNIVERSUM froze for seconds (owner, 1. 11. 2026: in Zlučovanie and Hromadné spracovanie, Czech hard to reach). The
/// open list of every drop-down scrolls by items instead – only what is seen is made.
/// </summary>
internal static class QuickDropDowns
{
    /// <summary>For every drop-down list of the application (once, at the start).</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        ByItems(combo);

        // a template made again (another look) has a new list: set again when it opens
        combo.DropDownOpened -= OnOpened;
        combo.DropDownOpened += OnOpened;
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo)
        {
            ByItems(combo);
        }
    }

    /// <summary>The open list scrolls by items (a list in groups keeps scrolling by pixels – it needs it).</summary>
    internal static bool ByItems(ComboBox combo)
    {
        if (combo.IsGrouping)
        {
            return false;
        }

        // the drop-down itself (a template that passes it on to its list) and the list's own scrolling (one that does not)
        combo.SetValue(ScrollViewer.CanContentScrollProperty, true);
        combo.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        combo.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Item);
        if (Scroller(combo) is not { } scroller)
        {
            return false;
        }

        if (!scroller.CanContentScroll)
        {
            scroller.CanContentScroll = true;
        }

        return true;
    }

    /// <summary>The scrolled part of the open list of a drop-down (null before its template is made).</summary>
    internal static ScrollViewer? Scroller(ComboBox combo) =>
        combo.Template?.FindName("PART_Popup", combo) is Popup { Child: { } list } ? Find(list) : null;

    // the scrolled part of the open list (the template's own elements: logical children of the popup's content)
    private static ScrollViewer? Find(DependencyObject element)
    {
        if (element is ScrollViewer scroller)
        {
            return scroller;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            if (Find(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
