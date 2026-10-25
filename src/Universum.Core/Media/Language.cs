namespace Universum.Core.Media;

/// <summary>A language with its ISO 639 codes and display names.</summary>
/// <param name="Ietf">Primary IETF BCP 47 subtag (ISO 639-1 when it exists), e.g. "cs".</param>
/// <param name="Bibliographic">ISO 639-2/B code used by the legacy Matroska element, e.g. "cze".</param>
/// <param name="Terminology">ISO 639-2/T code when it differs from /B, e.g. "ces".</param>
public sealed record Language(string Ietf, string Bibliographic, string? Terminology, string EnglishName, string SlovakName);
