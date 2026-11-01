using Universum.Core.Text;

namespace Universum.Core.Media;

/// <summary>A language with its ISO 639 codes and display names.</summary>
/// <param name="Ietf">Primary IETF BCP 47 subtag (ISO 639-1 when it exists), e.g. "cs".</param>
/// <param name="Bibliographic">ISO 639-2/B code used by the legacy Matroska element, e.g. "cze".</param>
/// <param name="Terminology">ISO 639-2/T code when it differs from /B, e.g. "ces".</param>
public sealed record Language(string Ietf, string Bibliographic, string? Terminology, string EnglishName, string SlovakName)
{
    /// <summary>
    /// The name in the language of the window (owner, 1. 11. 2026): the English name in English, the Czech one from the
    /// window's dictionary in Czech, the Slovak one otherwise.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => CoreText.Language == "en" ? EnglishName : CoreText.T(SlovakName);
}
