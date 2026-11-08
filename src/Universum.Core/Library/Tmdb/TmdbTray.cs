using System.Globalization;
using System.Text.RegularExpressions;
using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// A film or series in the Zásobník TMDB: its kind and ID, and its name with the year when known; an episode of a series
/// with its season and number (owner, 8. 11. 2026 – the button on an episode page of TMDB).
/// </summary>
public sealed record TmdbTrayEntry(MediaKind Kind, int Id, string? Name = null, int? Season = null, int? Episode = null)
{
    /// <summary>The page of the title on TMDB (of the episode, when it is one).</summary>
    public Uri Address => IsEpisode ? TmdbTray.EpisodeAddress(Id, Season!.Value, Episode!.Value) : TmdbTray.Address(Kind, Id);

    public bool IsEpisode => Kind == MediaKind.Series && Season is not null && Episode is not null;

    /// <summary>"S01E03" for an episode; empty otherwise.</summary>
    public string EpisodeTag => IsEpisode ? $"S{Season!.Value:00}E{Episode!.Value:00}" : "";

    /// <summary>"Vymyslený film (2026)", "Vymyslený seriál (2024) · S01E03", or the address when the name is not known yet.</summary>
    public string Display => (Name ?? WebLinks.Display(Address)) + (IsEpisode && Name is not null ? " · " + EpisodeTag : "");

    /// <summary>What a title field takes: the address of the film or series (UNIVERSUM loads the title by it).</summary>
    public string FieldText => TmdbTray.Address(Kind, Id).AbsoluteUri;

    /// <summary>The same title in the tray (an episode is a title of its own).</summary>
    public bool SameAs(TmdbTrayEntry other) => Kind == other.Kind && Id == other.Id && Season == other.Season && Episode == other.Episode;

    /// <summary>The film or series without the episode.</summary>
    public TmdbTrayEntry WithoutEpisode() => this with { Season = null, Episode = null };
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

    /// <summary>The page of an episode on TMDB.</summary>
    public static Uri EpisodeAddress(int id, int season, int episode) =>
        new($"https://www.themoviedb.org/tv/{id.ToString(CultureInfo.InvariantCulture)}/season/{season.ToString(CultureInfo.InvariantCulture)}/episode/{episode.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The season and number of an episode page of TMDB ("/tv/222-…/season/1/episode/3"); null for any other page.</summary>
    public static (int Season, int Episode)? EpisodeOf(Uri? address) =>
        address is not null && EpisodePath().Match(address.AbsolutePath) is { Success: true } match &&
        int.TryParse(match.Groups["season"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var season) &&
        int.TryParse(match.Groups["episode"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episode) && episode > 0
            ? (season, episode)
            : null;

    [GeneratedRegex(@"^/tv/\d+(-[^/]*)?/season/(?<season>\d{1,3})/episode/(?<episode>\d{1,4})(/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePath();

    public static Uri Address(MediaKind kind, int id) =>
        new($"https://www.themoviedb.org/{(kind == MediaKind.Series ? "tv" : "movie")}/{id.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The titles of the note, in its order (each once; links that are not films or series of TMDB are left out).</summary>
    public static IReadOnlyList<TmdbTrayEntry> Read(string? note)
    {
        var entries = new List<TmdbTrayEntry>();
        foreach (var row in NoteRows.Of(note))
        {
            if (row.Tmdb is { Kind: { } kind } tmdb)
            {
                var episode = kind == MediaKind.Series ? EpisodeOf(row.Link) : null;
                var entry = new TmdbTrayEntry(kind, tmdb.Id, row.Name, episode?.Season, episode?.Episode);
                if (!entries.Any(e => e.SameAs(entry)))
                {
                    entries.Add(entry);
                }
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

        lines.Add(WebLinks.Named(name, entry.Address));
        return Sorted(lines);
    }

    /// <summary>The note with the name of a title set (it stays where it is); the note as it was when the title is not in it.</summary>
    public static string Name(string? note, MediaKind kind, int id, string name)
    {
        // the film or series and each of its episodes keep their place and their address
        var lines = Lines(note);
        for (var at = 0; at < lines.Count; at++)
        {
            if (Read(lines[at]) is [{ } entry] && entry.Kind == kind && entry.Id == id)
            {
                lines[at] = WebLinks.Named(name, entry.Address);
            }
        }

        return Sorted(lines);
    }

    /// <summary>The note without a film or series (and its episodes).</summary>
    public static string Remove(string? note, MediaKind kind, int id) =>
        string.Join('\n', Lines(note).Where(line => !Read(line).Any(e => e.Kind == kind && e.Id == id)));

    /// <summary>The note without one title (an episode alone).</summary>
    public static string Remove(string? note, TmdbTrayEntry entry) =>
        string.Join('\n', Lines(note).Where(line => !Matches(line, entry)));

    /// <summary>
    /// The tray emptied (owner, 8. 11. 2026: "reštart – nebude tam žiaden údaj z TMDB"): every film and series line out,
    /// the owner's own lines stay.
    /// </summary>
    public static string Clear(string? note) => string.Join('\n', Lines(note).Where(line => Read(line).Count == 0));

    private static readonly CompareInfo Slovak = CultureInfo.GetCultureInfo("sk-SK").CompareInfo;

    // owner, 8. 11. 2026 ("zoradené podľa abecedy, lebo sa v tom zle orientuje"): the own lines first as they were, then the
    // titles by their names in the Slovak alphabet (an episode right after its series); titles without a name yet at the end
    /// <summary>The note in the order of the tray (its own lines first, the titles by the alphabet).</summary>
    public static string Sort(string? note) => Sorted(Lines(note));

    private static string Sorted(List<string> lines)
    {
        var own = lines.Where(l => Read(l).Count == 0);
        var titles = lines.Select(l => (Line: l, Entry: Read(l).FirstOrDefault())).Where(t => t.Entry is not null)
            .OrderBy(t => t.Entry!.Name is null ? 1 : 0)
            .ThenBy(t => t.Entry!.Name ?? t.Entry.Address.AbsoluteUri, Comparer<string>.Create((a, b) => Slovak.Compare(a, b, CompareOptions.IgnoreCase)))
            .ThenBy(t => t.Entry!.Season ?? -1).ThenBy(t => t.Entry!.Episode ?? -1)
            .Select(t => t.Line);
        return string.Join('\n', own.Concat(titles));
    }

    private static List<string> Lines(string? note) =>
        [.. (note ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0)];

    private static bool Matches(string line, TmdbTrayEntry entry) => Read(line).Any(e => e.SameAs(entry));

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

        // an episode page (owner, 8. 11. 2026): the series with the season and number; its tab names the episode, so the
        // name of the series comes from TMDB
        if (kind == MediaKind.Series && EpisodeOf(address) is { } episode)
        {
            return new TmdbTrayEntry(kind, id, null, episode.Season, episode.Episode);
        }

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

    /// <summary>
    /// A film or series page of ČSFD the script sends (owner, 8. 11. 2026): csfd.cz or csfd.sk, "/film/12345-…" (a season,
    /// an episode or the gallery of it too – the first ID is the title); the IMDb ID only as "tt" and digits. Null for any
    /// other page.
    /// </summary>
    public static CsfdPage? FromCsfdPage(string? page, string? title, string? imdb)
    {
        if (page is null || page.Length > 2048 || !Uri.TryCreate(page, UriKind.Absolute, out var address) || address.Scheme != "https" ||
            !IsCsfdHost(address.Host) || CsfdFilm().Match(address.AbsolutePath) is not { Success: true } film ||
            !int.TryParse(film.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return null;
        }

        return new CsfdPage(id, imdb is not null && ImdbId().IsMatch(imdb) ? imdb : null, FromCsfdTitle(title is { Length: > 300 } ? title[..300] : title));
    }

    /// <summary>
    /// The title of a ČSFD page as a tab shows it – "Vymyslený film (2026) | ČSFD.cz", "Vymyslený seriál (TV seriál) (2024)
    /// | ČSFD.sk": the name, the year and whether it is a series. Null when it is no such title.
    /// </summary>
    public static TabTitle? FromCsfdTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !title.Contains("ČSFD", StringComparison.OrdinalIgnoreCase) && !title.Contains("CSFD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var first = title.Split('|')[0].Trim();
        var years = CsfdYear().Matches(first);
        if (years.Count == 0)
        {
            return null;
        }

        var name = first[..first.IndexOf('(', StringComparison.Ordinal)].Trim();
        var series = first.Contains("seriál", StringComparison.OrdinalIgnoreCase) || first.Contains("series", StringComparison.OrdinalIgnoreCase);
        var year = int.Parse(years[0].Groups["year"].Value, CultureInfo.InvariantCulture);
        return name.Length is 0 or > 200 ? null : new TabTitle(name, year, series ? MediaKind.Series : MediaKind.Movie);
    }

    /// <summary>
    /// A poster the script sends: the page it was picked on (a film or series of TMDB or ČSFD) and the picture's address –
    /// only from the picture servers of these two sites (image.tmdb.org, media.themoviedb.org, image.pmgstatic.com) and
    /// only posters. Null for anything else: UNIVERSUM downloads nothing else (owner, 8. 11. 2026).
    /// </summary>
    public static TrayPosterRequest? FromPoster(string? page, string? image, string? title, string? imdb)
    {
        if (image is null || image.Length > 2048)
        {
            return null;
        }

        image = image.StartsWith("//", StringComparison.Ordinal) ? "https:" + image : image;
        if (!Uri.TryCreate(image, UriKind.Absolute, out var picture) || picture.Scheme != "https" || picture.UserInfo.Length > 0 || !picture.IsDefaultPort)
        {
            return null;
        }

        if (FromPage(page, title) is { } entry && page!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (picture.Host is not ("image.tmdb.org" or "media.themoviedb.org") || TmdbPicture().Match(picture.AbsolutePath) is not { Success: true } tmdb)
            {
                return null;
            }

            var path = "/" + tmdb.Groups["file"].Value;
            return new TrayPosterRequest(TrayPosterSource.Tmdb, [new Uri("https://image.tmdb.org/t/p/original" + path)], entry, null, path);
        }

        if (FromCsfdPage(page, title, imdb) is { } csfd)
        {
            if (picture.Host != "image.pmgstatic.com" || CsfdPicture().Match(picture.AbsolutePath) is not { Success: true } poster || picture.Query.Length > 0)
            {
                return null;
            }

            // the original first, then the large copy, then the copy the page shows
            var file = poster.Groups["file"].Value;
            var images = new List<Uri> { new("https://image.pmgstatic.com/files/images/film/posters/" + file), new("https://image.pmgstatic.com/cache/resized/w1080/files/images/film/posters/" + file) };
            if (!images.Contains(picture))
            {
                images.Add(picture);
            }

            return new TrayPosterRequest(TrayPosterSource.Csfd, images, null, csfd, null);
        }

        return null;
    }

    private static bool IsCsfdHost(string host) => host is "www.csfd.cz" or "csfd.cz" or "www.csfd.sk" or "csfd.sk";

    [GeneratedRegex(@"^/film/(?<id>\d{1,9})(-[^/]*)?(/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CsfdFilm();

    [GeneratedRegex(@"^tt\d{5,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImdbId();

    [GeneratedRegex(@"\((?<year>(18|19|20)\d{2})\)", RegexOptions.CultureInvariant)]
    private static partial Regex CsfdYear();

    // "/t/p/w220_and_h330_face/abc.jpg" (any size), the file only letters and digits
    [GeneratedRegex(@"^/t/p/[a-z0-9_]{1,40}/(?<file>[A-Za-z0-9]{6,64}\.(jpg|png|webp))$", RegexOptions.CultureInvariant)]
    private static partial Regex TmdbPicture();

    // "/cache/resized/w420/files/images/film/posters/163/579/163579352_bf8737.jpg" or the original without "cache/…"
    [GeneratedRegex(@"^/(cache/resized/[a-z0-9]{1,24}/)?files/images/film/posters/(?<file>(\d{1,4}/){1,4}[A-Za-z0-9_]{1,64}\.(jpg|jpeg|png|webp))$", RegexOptions.CultureInvariant)]
    private static partial Regex CsfdPicture();

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
