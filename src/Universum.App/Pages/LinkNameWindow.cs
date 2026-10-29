using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Universum.App.Pages;

/// <summary>A small window asking for the short name of a link pasted into a note (Názov odkazu).</summary>
public static class LinkNameWindow
{
    /// <summary>The word typed (the suggestion when only confirmed); null when cancelled – then the suggestion is used.</summary>
    public static string? Ask(Uri address, string suggestion)
    {
        var name = new TextBox { Text = suggestion, Margin = new Thickness(0, 6, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "Uložiť odkaz", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Použiť návrh", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Názov odkazu – v poznámke bude vidieť len toto slovo", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = address.AbsoluteUri, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, Margin = new Thickness(0, 2, 0, 0),
            Foreground = SystemColors.GrayTextBrush, ToolTip = address.AbsoluteUri,
        });
        panel.Children.Add(name);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = "UNIVERSUM – odkaz v poznámke", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
            Keyboard.Focus(name);
        };
        return window.ShowDialog() == true ? name.Text : null;
    }
}
