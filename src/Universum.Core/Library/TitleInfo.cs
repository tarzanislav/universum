using Universum.Core.Text;

namespace Universum.Core.Library;

/// <summary>What is being stored: a film (MOVIES) or a series (TVSHOWS).</summary>
public enum MediaKind
{
    Movie,
    Series,
}

/// <summary>A film or series as it is named in the library (specification, section 13.2).</summary>
/// <param name="Name">
/// Title used for folder and file names: the original title when it is written in Latin script, otherwise the
/// English title.
/// </param>
/// <param name="Year">Year of the premiere (film release, first episode of a series).</param>
/// <param name="TmdbId">ID at themoviedb.org, when known.</param>
/// <param name="IncludeId">The folder name carries "[tmdbid-…]" (titles whose original is not in Latin script).</param>
public sealed record TitleInfo(MediaKind Kind, string Name, int? Year, int? TmdbId = null, bool IncludeId = false)
{
    /// <summary>
    /// Title from TMDB data: the original title when it is written in Latin script; otherwise the English title with
    /// the TMDB ID. Null when an English title is needed but missing (the user has to type it).
    /// </summary>
    public static TitleInfo? FromTmdb(MediaKind kind, int id, string originalTitle, string? englishTitle, int? year)
    {
        if (LibraryNames.IsLatinScript(originalTitle) && LibraryNames.Clean(originalTitle).Length > 0)
        {
            return new TitleInfo(kind, originalTitle, year, id);
        }

        return string.IsNullOrWhiteSpace(englishTitle) || !LibraryNames.IsLatinScript(englishTitle)
            ? null
            : new TitleInfo(kind, englishTitle, year, id, IncludeId: true);
    }

    /// <summary>Folder of the title: "Name (Year)" or "Name (Year) [tmdbid-ID]".</summary>
    public string FolderName => LibraryNames.FolderName(this);

    /// <summary>The name without year and ID (start of episode file names).</summary>
    public string CleanName => LibraryNames.Clean(Name);

    /// <summary>
    /// Problem preventing use of this title (Slovak), or null. The folder is "Name (Year)" in Latin script (section 13.2):
    /// a title in another script needs its English name and TMDB ID, and the year is required.
    /// </summary>
    public string? Problem =>
        CleanName.Length == 0 ? CoreText.T("chýba názov")
        : !LibraryNames.IsLatinScript(CleanName) ? CoreText.T("názov nie je latinkou – použite anglický názov s TMDB ID (napr. Nine Puzzles (2025) [tmdbid-227191])")
        : LibraryNames.IsReserved(FolderName) ? CoreText.T("názov nie je vo Windows povolený")
        : IncludeId && TmdbId is null ? CoreText.T("chýba TMDB ID")
        : Year is null ? CoreText.T("chýba rok")
        : null;

    public override string ToString() => FolderName;
}
