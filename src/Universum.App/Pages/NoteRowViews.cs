using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>
/// A link of a note on a row of its own (owner, 7. 11. 2026): a long one shortened by the row, the whole address in the
/// tooltip, a click opens the page; with Meno namiesto linku a film or series of TMDB shown by its name.
/// </summary>
public sealed class NoteLinkView : ObservableObject
{
    private string _shown;

    public NoteLinkView(NoteRow row, string? titleName)
    {
        Row = row;
        Address = row.Link ?? throw new ArgumentException("a row with a link", nameof(row));
        _shown = row.Name ?? titleName ?? row.Address;
        OpenCommand = new RelayCommand(() => NoteLinks.Open(Address));
    }

    public NoteRow Row { get; }

    public Uri Address { get; }

    /// <summary>The address without "https://www.", the name the note gives it, or the title of TMDB.</summary>
    public string Shown { get => _shown; private set => SetProperty(ref _shown, value); }

    /// <summary>The whole address, as it is read.</summary>
    public string ToolTip => Loc.F("{0}\nKliknutím otvoríte stránku.", AddressText());

    public RelayCommand OpenCommand { get; }

    /// <summary>The title of TMDB found (a link the note names itself keeps its name).</summary>
    public void Named(string name)
    {
        if (Row.Name is null)
        {
            Shown = name;
        }
    }

    private string AddressText()
    {
        try
        {
            return Uri.UnescapeDataString(Address.AbsoluteUri);
        }
        catch (UriFormatException)
        {
            return Address.AbsoluteUri;
        }
    }
}

/// <summary>A row of a note's card: a piece of its text, or one link.</summary>
public sealed record NoteRowView(string? Text, NoteLinkView? Link)
{
    public bool IsLink => Link is not null;
}
