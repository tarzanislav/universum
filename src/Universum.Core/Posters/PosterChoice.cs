using System.Globalization;
using Universum.Core.Library;

namespace Universum.Core.Posters;

/// <summary>A picture of a title on TMDB (a poster, background or logo) as its list of images gives it.</summary>
/// <param name="Language">The language of the text in it (ISO 639-1); null for a picture without text.</param>
public sealed record TmdbPicture(string Path, string? Language, int Width, int Height, double VoteAverage, int VoteCount)
{
    /// <summary>Width ÷ height (a poster: 2:3 = 0,667).</summary>
    public double Aspect => Height > 0 ? Width / (double)Height : 0;
}

/// <summary>
/// How a picture looks, measured on its small copy: <see cref="Colorfulness"/> by Hasler and Süsstrunk (about 0 grey,
/// 30 a little, 60 quite, 90 and more very colourful), <see cref="Brightness"/> and <see cref="Contrast"/> 0–1 (the mean
/// and the spread of the luminance).
/// </summary>
public sealed record PictureLook(double Colorfulness, double Brightness, double Contrast);

/// <summary>What colours suit a title by its genres: a horror is darker than a comedy, a comedy less colourful than a romance.</summary>
public enum PosterMood
{
    Dark,
    Neutral,
    Bright,
}

/// <summary>A poster of the offer: its language group (0 Czech, 1 English, 2 the original language, 3 without text, 4 another) and its score 0–1.</summary>
public sealed record PosterCandidate(TmdbPicture Picture, int Tier, double Score, bool Acceptable);

/// <summary>A group of posters offered to choose from ("CZ", "SK", "EN", "Originál (ja)").</summary>
public sealed record PosterGroup(string Name, IReadOnlyList<PosterCandidate> Posters);

/// <summary>The poster UNIVERSUM recommends, why (its own opinion in words) and the others to choose from.</summary>
public sealed record PosterOffer(PosterCandidate? Recommended, string Opinion, IReadOnlyList<PosterGroup> Groups);

/// <summary>
/// Chooses a poster of a title (owner, 31. 10. 2026): one with the title written in it, in the order Czech → English →
/// the original language; among them one the community of TMDB rates well, large enough, in the proportions of a
/// poster, colourful as the genre suits (a horror darker and harder, a comedy colourful and bright, a romance the most).
/// Pictures without text, amateur ones (rated down) and others too small or of other proportions come only when there is
/// nothing better. The others are offered in groups: the original language (when written in Latin script), English,
/// Czech, Slovak.
/// </summary>
public static class PosterChoice
{
    /// <summary>Posters offered in one group at most.</summary>
    public const int GroupSize = 12;

    private static readonly int[] DarkGenres = [27, 53, 80, 10752, 9648, 10768];
    private static readonly int[] BrightGenres = [35, 10751, 16, 10749, 10402, 10762, 10764];
    private static readonly int[] Romantic = [10749];

    /// <summary>The mood of a title's colours by its TMDB genres (the first dark or bright one decides; a horror comedy is dark).</summary>
    public static PosterMood MoodOf(IReadOnlyList<int> genres) =>
        genres.Any(DarkGenres.Contains) ? PosterMood.Dark : genres.Any(BrightGenres.Contains) ? PosterMood.Bright : PosterMood.Neutral;

    /// <summary>The language group of a poster: 0 Czech, 1 English, 2 the original language, 3 without text, 4 another.</summary>
    public static int Tier(string? language, string? originalLanguage) => language switch
    {
        "cs" => 0,
        "en" => 1,
        null or "" or "xx" => 3,
        _ when string.Equals(language, originalLanguage, StringComparison.OrdinalIgnoreCase) => 2,
        _ => 4,
    };

    /// <summary>
    /// A poster TMDB itself would accept and the community did not reject: at least 500 px wide, 2:3 (±6 %), not rated
    /// down by several votes (amateur work).
    /// </summary>
    public static bool IsAcceptable(TmdbPicture picture) =>
        picture.Width >= 500 && Math.Abs(picture.Aspect - (2 / 3.0)) <= 0.04 && !(picture.VoteCount >= 3 && picture.VoteAverage < 3.5);

    /// <summary>
    /// The score of a poster 0–1: half the rating of the community (a few votes count little), a third how its colours
    /// suit the genre, the rest its size. A poster not measured counts as average in colours.
    /// </summary>
    public static double Score(TmdbPicture picture, PictureLook? look, PosterMood mood)
    {
        var rating = ((picture.VoteAverage * picture.VoteCount) + (5.0 * 2)) / (picture.VoteCount + 2) / 10;
        var size = Math.Min(1, picture.Width / 1400.0);
        return (0.5 * rating) + (0.35 * (look is null ? 0.5 : Fit(look, mood))) + (0.15 * size);
    }

    /// <summary>How the colours of a picture suit the mood 0–1.</summary>
    public static double Fit(PictureLook look, PosterMood mood)
    {
        static double Clamp(double value) => Math.Clamp(value, 0, 1);
        var contrast = Clamp(look.Contrast / 0.28);
        return mood switch
        {
            PosterMood.Bright => (0.65 * Clamp(look.Colorfulness / 85)) + (0.35 * Clamp(look.Brightness / 0.55)),
            PosterMood.Dark => (0.55 * Clamp(1 - (Math.Abs(look.Colorfulness - 45) / 60))) + (0.45 * contrast),
            _ => (0.6 * Clamp(1 - (Math.Abs(look.Colorfulness - 65) / 70))) + (0.4 * contrast),
        };
    }

    /// <summary>
    /// The poster recommended among <paramref name="posters"/>, the opinion and the groups to choose from.
    /// <paramref name="looks"/>: the pictures measured so far (by path); <paramref name="latinOriginal"/>: the original
    /// title is written in Latin script (only then the original language is offered as a group).
    /// </summary>
    public static PosterOffer Offer(IReadOnlyList<TmdbPicture> posters, IReadOnlyDictionary<string, PictureLook> looks, IReadOnlyList<int> genres,
        string? originalLanguage, bool latinOriginal)
    {
        var mood = MoodOf(genres);
        var candidates = posters
            .Where(p => PosterCache.IsValidPath(p.Path))
            .GroupBy(p => p.Path, StringComparer.Ordinal).Select(g => g.First())
            .Select(p => new PosterCandidate(p, Tier(p.Language, originalLanguage), Score(p, looks.GetValueOrDefault(p.Path), mood), IsAcceptable(p)))
            .OrderBy(c => c.Tier).ThenByDescending(c => c.Acceptable).ThenByDescending(c => c.Score)
            .ToList();
        var recommended = candidates.FirstOrDefault(c => c.Acceptable && c.Tier <= 2)
                          ?? candidates.FirstOrDefault(c => c.Tier <= 2)
                          ?? candidates.FirstOrDefault(c => c.Tier == 3 && c.Acceptable)
                          ?? candidates.FirstOrDefault(c => c.Tier == 3);

        var groups = new List<PosterGroup>();
        void Add(string name, Func<PosterCandidate, bool> belongs)
        {
            var group = candidates.Where(belongs).OrderByDescending(c => c.Acceptable).ThenByDescending(c => c.Score).Take(GroupSize).ToList();
            if (group.Count > 0)
            {
                groups.Add(new PosterGroup(name, group));
            }
        }

        var original = originalLanguage?.ToLowerInvariant();
        if (latinOriginal && original is { Length: > 0 } and not ("cs" or "sk" or "en"))
        {
            Add($"Originál ({original})", c => c.Picture.Language == original);
        }

        Add("EN", c => c.Picture.Language == "en");
        Add("CZ", c => c.Picture.Language == "cs");
        Add("SK", c => c.Picture.Language == "sk");
        if (groups.Count == 0)
        {
            // nothing in these languages: what there is (the original in another script, without text)
            Add(original is { Length: > 0 } ? $"Originál ({original})" : "Originál", c => c.Tier == 2);
            Add("Bez textu", c => c.Tier == 3);
        }

        var look = recommended is null ? null : looks.GetValueOrDefault(recommended.Picture.Path);
        return new PosterOffer(recommended, Opinion(recommended, look, candidates, mood, genres, originalLanguage), groups);
    }

    // UNIVERSUM's own opinion of its choice in words
    private static string Opinion(PosterCandidate? chosen, PictureLook? look, IReadOnlyList<PosterCandidate> all, PosterMood mood, IReadOnlyList<int> genres, string? originalLanguage)
    {
        if (chosen is null)
        {
            return "TMDB k titulu nemá žiadny plagát.";
        }

        var sk = CultureInfo.GetCultureInfo("sk-SK");
        var picture = chosen.Picture;
        var parts = new List<string>
        {
            chosen.Tier switch
            {
                0 => "Odporúčam český plagát – názov je na ňom po česky",
                1 => all.Any(c => c.Tier == 0) ? "Odporúčam anglický plagát – české plagáty sú príliš malé alebo nevhodné" : "Odporúčam anglický plagát – český TMDB nemá",
                2 => $"Odporúčam plagát v pôvodnom jazyku ({originalLanguage}) – český ani anglický TMDB nemá",
                _ => "Odporúčam plagát bez textu – plagát s názvom TMDB nemá",
            },
        };

        var sameTier = all.Count(c => c.Tier == chosen.Tier);
        parts.Add(picture.VoteCount > 0
            ? (sameTier > 1 ? "z nich je najlepší" : "jediný v tomto jazyku") +
              $" (hodnotenie {picture.VoteAverage.ToString("0.0", sk)}, {Texts(picture.VoteCount)})"
            : sameTier > 1 ? "z nich sa hodí najviac (zatiaľ bez hodnotení)" : "jediný v tomto jazyku");

        // the colours as measured, and whether they suit the genre
        if (look is not null)
        {
            var colours = look.Colorfulness switch
            {
                < 25 => "tlmené farby",
                < 50 => "striedme farby",
                < 80 => "pestré farby",
                _ => "veľmi pestré farby",
            } + (look.Brightness < 0.3 ? ", tmavý" : look.Brightness > 0.6 ? ", svetlý" : "");
            var genre = (genres.Any(Romantic.Contains) ? Library.Tmdb.TmdbGenres.Name(10749) : null) ??
                        genres.Select(Library.Tmdb.TmdbGenres.Name).FirstOrDefault(n => n is not null) ?? "";
            parts.Add(Fit(look, mood) >= 0.55 && genre.Length > 0 ? $"{colours} – sedí to k žánru ({genre})" : colours);
        }
        parts.Add($"{picture.Width} × {picture.Height} px");
        if (!chosen.Acceptable)
        {
            parts.Add("pozor: je menší alebo iných rozmerov, ako TMDB odporúča");
        }

        return string.Join(", ", parts) + ".";
    }

    private static string Texts(int votes) => votes == 1 ? "1 hlas" : votes is >= 2 and <= 4 ? $"{votes} hlasy" : $"{votes} hlasov";
}
