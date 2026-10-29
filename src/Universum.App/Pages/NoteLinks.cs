using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// Poznámka with links (owner, 29. 10. 2026): the web addresses of a note are shown as links that open the page; a click
/// beside the links edits the note (to change it or add another link). <c>NoteLinks.Text</c> fills a TextBlock with the
/// note's text and links, <c>NoteLinks.Editor</c> names the TextBox a click beside the links focuses.
/// </summary>
public static class NoteLinks
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(NoteLinks), new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty EditorProperty =
        DependencyProperty.RegisterAttached("Editor", typeof(TextBox), typeof(NoteLinks), new PropertyMetadata(null, OnEditorChanged));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static TextBox? GetEditor(DependencyObject element) => (TextBox?)element.GetValue(EditorProperty);

    public static void SetEditor(DependencyObject element, TextBox? value) => element.SetValue(EditorProperty, value);

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        foreach (var piece in WebLinks.Split(e.NewValue as string))
        {
            if (piece.Link is { } address)
            {
                var link = new Hyperlink(new Run(piece.Text)) { ToolTip = address.AbsoluteUri };
                link.Click += (_, args) =>
                {
                    Open(address);
                    args.Handled = true;
                };
                block.Inlines.Add(link);
            }
            else
            {
                block.Inlines.Add(new Run(piece.Text));
            }
        }
    }

    private static void OnEditorChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is UIElement area)
        {
            area.MouseLeftButtonUp -= OnClick;
            area.MouseLeftButtonUp += OnClick;
        }
    }

    // a click on a link opens it; beside the links, the note is edited (the caret at its end, for another link)
    private static void OnClick(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null and not UIElement; source = (source as FrameworkContentElement)?.Parent)
        {
            if (source is Hyperlink)
            {
                return;
            }
        }

        if (sender is DependencyObject area && GetEditor(area) is { } editor)
        {
            editor.Focus();
            editor.CaretIndex = editor.Text.Length;
            e.Handled = true;
        }
    }

    // only http and https addresses become links (WebLinks)
    private static void Open(Uri address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser – nothing to open
        }
    }
}
