using System.Globalization;
using System.Text;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.Core.Text;

/// <summary>
/// One row of a note as Poznámky shows it (owner, 7. 11. 2026): a piece of its text, or one link on a row of its own –
/// with its name when the note gives one ("[name](address)") and the film or series when it is an address of TMDB.
/// </summary>
public sealed record NoteRow(string? Text, Uri? Link = null, string? Name = null, TmdbReference? Tmdb = null)
{
    public bool IsLink => Link is not null;

    /// <summary>The address as it is read: without "https://" and "www.", the letters not escaped ("themoviedb.org/tv/1-x").</summary>
    public string Address => Link is null ? "" : WebLinks.Display(Link);
}

/// <summary>
/// A note in rows (owner, 7. 11. 2026: links pasted one after another ran together into an unreadable block): its text
/// as it is written, and every link on its own row, in the order of the note.
/// </summary>
public static class NoteRows
{
    public static IReadOnlyList<NoteRow> Of(string? note)
    {
        var rows = new List<NoteRow>();
        var text = new StringBuilder();
        foreach (var piece in WebLinks.Split(note))
        {
            if (piece.Link is { } link)
            {
                Flush();
                var tmdb = TitleGuess.ParseTmdb(link.AbsoluteUri);
                rows.Add(new NoteRow(null, link, piece.Named ? piece.Text : null, tmdb is { Kind: not null } ? tmdb : null));
            }
            else
            {
                text.Append(piece.Text);
            }
        }

        Flush();
        return rows;

        // the text between links, without the blank lines around it; what is only spaces or punctuation between two
        // links ("a, b") is no row
        void Flush()
        {
            var lines = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
            // a comma that parted the text from the link before it parts nothing on a row of its own
            var written = string.Join('\n', lines).Trim('\n', ' ', '\t').TrimStart(',', ';').Trim('\n', ' ', '\t');
            text.Clear();
            if (written.Any(char.IsLetterOrDigit))
            {
                rows.Add(new NoteRow(written));
            }
        }
    }

    /// <summary>
    /// The name of a title of TMDB as the library names it: the original title in Latin script, otherwise the English one
    /// (Chinese, Korean, Japanese … titles), with the year – "Vymyslený film (2026)". TMDB's own title when neither fits.
    /// </summary>
    public static string TitleName(TmdbTitle title)
    {
        var library = title.ToLibraryTitle(null);
        var name = library?.CleanName is { Length: > 0 } clean ? clean : title.Title;
        var year = library?.Year ?? title.Year;
        return year is { } y ? $"{name} ({y.ToString(CultureInfo.InvariantCulture)})" : name;
    }
}
