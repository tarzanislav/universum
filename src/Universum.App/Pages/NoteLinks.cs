using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// Poznámka with links (owner, 29. 10. 2026): a web address pasted into the note asks for a short word; the note shows
/// only the word (the address hides in it, a click opens the page). <c>NoteLinks.AskName</c> on the note's TextBox
/// turns a pasted address into a link of its row (<see cref="MarkedRow.AddLink"/>).
/// </summary>
public static class NoteLinks
{
    public static readonly DependencyProperty AskNameProperty =
        DependencyProperty.RegisterAttached("AskName", typeof(bool), typeof(NoteLinks), new PropertyMetadata(false, OnAskNameChanged));

    public static bool GetAskName(DependencyObject element) => (bool)element.GetValue(AskNameProperty);

    public static void SetAskName(DependencyObject element, bool value) => element.SetValue(AskNameProperty, value);

    /// <summary>Asks for the word of a pasted link (the suggestion given); null keeps the suggestion. The self-test answers itself.</summary>
    public static Func<Uri, string, string?> AskForName { get; set; } = LinkNameWindow.Ask;

    private static void OnAskNameChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBox box)
        {
            DataObject.RemovePastingHandler(box, OnPasting);
            if (e.NewValue is true)
            {
                DataObject.AddPastingHandler(box, OnPasting);
            }
        }
    }

    // the address is not pasted into the text: it becomes a link (a word) of the row, kept at once
    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox { DataContext: MarkedRow row } box || !e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text || WebLinks.AsAddress(text) is not { } address)
        {
            return;
        }

        e.CancelCommand();
        // what was typed so far is kept first (the note is written again with the link)
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.Dispatcher.BeginInvoke(() =>
        {
            var suggestion = WebLinks.SuggestName(address);
            row.AddLink(AskForName(address, suggestion) ?? suggestion, address);
            box.Focus();
            box.CaretIndex = box.Text.Length;
        });
    }

    /// <summary>Opens a link of a note (only http and https addresses are links).</summary>
    public static void Open(Uri address)
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
