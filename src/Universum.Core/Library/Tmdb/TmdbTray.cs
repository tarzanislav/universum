using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>A film or series in the Zásobník TMDB: its kind and ID, and its name with the year when known.</summary>
public sealed record TmdbTrayEntry(MediaKind Kind, int Id, string? Name = null)
{
    /// <summary>The page of the title on TMDB.</summary>
    public Uri Address => TmdbTray.Address(Kind, Id);

    /// <summary>"Vymyslený film (2026)", or the address when the name is not known yet.</summary>
    public string Display => Name ?? WebLinks.Display(Address);

    /// <summary>What a title field takes: the address (UNIVERSUM loads the title by it, and knows it is a film or a series).</summary>
    public string FieldText => Address.AbsoluteUri;
}

/// <summary>
/// Zásobník TMDB (owner, 8. 11. 2026: "je otravné stále kopírovať TMDB ID z prehliadača"): the films and series picked in
/// the browser – by the button "Do UNIVERSUM" in the bookmarks bar or read from the open windows – gathered in one note of
/// Poznámky, each a link named by its title; Hromadné, Single, MEGA and Zlučovanie take a title from it with one click.
/// The note is the store: its text, a link per line.
/// </summary>
public static partial class TmdbTray
{
    /// <summary>The mark of the note that is the tray (its Uid).</summary>
    public const string NoteUid = "universum-zasobnik-tmdb";

    public static Uri Address(MediaKind kind, int id) =>
        new($"https://www.themoviedb.org/{(kind == MediaKind.Series ? "tv" : "movie")}/{id.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The titles of the note, in its order (each once; links that are not films or series of TMDB are left out).</summary>
    public static IReadOnlyList<TmdbTrayEntry> Read(string? note)
    {
        var entries = new List<TmdbTrayEntry>();
        foreach (var row in NoteRows.Of(note))
        {
            if (row.Tmdb is { Kind: { } kind } tmdb && !entries.Any(e => e.Kind == kind && e.Id == tmdb.Id))
            {
                entries.Add(new TmdbTrayEntry(kind, tmdb.Id, row.Name));
            }
        }

        return entries;
    }

    /// <summary>
    /// The note with a title added at the top (one already there is moved up and gets the name when it had none); the
    /// other lines as they were.
    /// </summary>
    public static string Add(string? note, TmdbTrayEntry entry)
    {
        var lines = Lines(note);
        var existing = lines.FindIndex(line => Matches(line, entry));
        var name = entry.Name ?? (existing >= 0 ? Read(lines[existing]).FirstOrDefault()?.Name : null);
        if (existing >= 0)
        {
            lines.RemoveAt(existing);
        }

        lines.Insert(0, WebLinks.Named(name, entry.Address));
        return string.Join('\n', lines);
    }

    /// <summary>The note with the name of a title set (it stays where it is); the note as it was when the title is not in it.</summary>
    public static string Name(string? note, MediaKind kind, int id, string name)
    {
        var lines = Lines(note);
        var at = lines.FindIndex(line => Matches(line, new TmdbTrayEntry(kind, id)));
        if (at >= 0)
        {
            lines[at] = WebLinks.Named(name, Address(kind, id));
        }

        return string.Join('\n', lines);
    }

    /// <summary>The note without a title.</summary>
    public static string Remove(string? note, MediaKind kind, int id) =>
        string.Join('\n', Lines(note).Where(line => !Matches(line, new TmdbTrayEntry(kind, id))));

    private static List<string> Lines(string? note) =>
        [.. (note ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0)];

    private static bool Matches(string line, TmdbTrayEntry entry) => Read(line).Any(e => e.Kind == entry.Kind && e.Id == entry.Id);

    /// <summary>
    /// A link the browser hands over (the button "Do UNIVERSUM": "universum://tmdb?u=…&amp;t=…" – the address of the page
    /// and its title): the film or series when it is a page of one on TMDB, else null. Nothing else is ever taken from such
    /// a link (owner, 8. 11. 2026: only TMDB addresses).
    /// </summary>
    public static TmdbTrayEntry? FromLink(string? link)
    {
        if (link is null || link.Length > 4096 || !link.StartsWith("universum:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(link, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "tmdb", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? page = null;
        string? title = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? "" : Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
            if (key == "u")
            {
                page = value;
            }
            else if (key == "t")
            {
                title = value;
            }
        }

        return FromPage(page, title);
    }

    /// <summary>
    /// A page of the browser – its address and title: the film or series when it is a page of one on TMDB (named by the
    /// title, when it is one of TMDB's), else null.
    /// </summary>
    public static TmdbTrayEntry? FromPage(string? page, string? title)
    {
        if (page is null || page.Length > 2048 || !Uri.TryCreate(page, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http") ||
            !(address.Host.Equals("themoviedb.org", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".themoviedb.org", StringComparison.OrdinalIgnoreCase)) ||
            TitleGuess.ParseTmdb(address.AbsoluteUri) is not { Kind: { } kind, Id: var id })
        {
            return null;
        }

        title = title is { Length: > 300 } ? title[..300] : title;
        return new TmdbTrayEntry(kind, id, title is null ? null : FromTabTitle(title) is { } named ? named.Display : null);
    }

    /// <summary>
    /// A title of a TMDB page as a browser tab shows it – "Vymyslený film (2026) — The Movie Database (TMDB)", "Vymyslený
    /// seriál (TV Series 2014–2016) — …" (the words for a series in TMDB's language): the name, the year and, when the
    /// title says so, whether it is a series. Null when it is no title of a film or series page.
    /// </summary>
    public static TabTitle? FromTabTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !title.Contains("The Movie Database", StringComparison.OrdinalIgnoreCase) && !title.Contains("TMDB", StringComparison.Ordinal))
        {
            return null;
        }

        var first = Separator().Split(title)[0].Trim();
        var match = NameAndYear().Match(first);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value.Trim();
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        var words = match.Groups["words"].Value.Trim();
        return name.Length == 0 || name.Length > 200 ? null : new TabTitle(name, year, words.Length > 0 ? MediaKind.Series : MediaKind.Movie);
    }

    // " — " or " - " between the parts of the title
    [GeneratedRegex(@"\s+[—–-]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    // "Name (2026)" or "Name (TV Series 2014–2016)": the words before the year say a series
    [GeneratedRegex(@"^(?<name>.+?)\s*\((?<words>[^()\d]*?)\s*(?<year>(19|20)\d{2})(?:\s*[–-]\s*(?:(19|20)\d{2})?)?\s*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex NameAndYear();
}

/// <summary>The name, year and kind read from the title of a TMDB page.</summary>
public sealed record TabTitle(string Name, int Year, MediaKind Kind)
{
    public string Display => $"{Name} ({Year.ToString(CultureInfo.InvariantCulture)})";
}
