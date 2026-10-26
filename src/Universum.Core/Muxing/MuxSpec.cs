using Universum.Core.Media;

namespace Universum.Core.Muxing;

/// <summary>An input file of the multiplexer and what is taken from it besides its tracks.</summary>
public sealed record MuxSource(MediaFile File, bool KeepChapters = true, bool KeepAttachments = true, bool KeepTags = true);

/// <summary>A track that goes into the output, with the values it gets there.</summary>
/// <param name="Source">Index of the input file in <see cref="MuxSpec.Sources"/>.</param>
/// <param name="TrackId">mkvmerge track ID within that file.</param>
/// <param name="Name">Track name; null or empty = no name.</param>
public sealed record MuxTrack(int Source, int TrackId, string? Name, LanguageTag Language, TrackFlags Flags);

/// <summary>A file attached to the output (fonts, cover).</summary>
public sealed record MuxAttachment(string Path, string? Description = null);

/// <summary>
/// What the multiplexer writes (MKVToolNix GUI: Multiplexer): input files, the tracks taken from them in output
/// order with their names, languages and flags, the title, a chapter file and attachments, and the output file.
/// </summary>
public sealed record MuxSpec(IReadOnlyList<MuxSource> Sources, IReadOnlyList<MuxTrack> Tracks, string OutputPath)
{
    public static IReadOnlyList<string> OutputExtensions { get; } = [".mkv", ".mka", ".mks", ".webm"];

    /// <summary>Segment title; null or empty = none (mkvmerge may take the first input's title).</summary>
    public string? Title { get; init; }

    /// <summary>Chapters from a file (XML, OGM or cue sheet) instead of / besides the inputs' chapters.</summary>
    public string? ChaptersFile { get; init; }

    public IReadOnlyList<MuxAttachment> Attachments { get; init; } = [];

    public MediaTrack TrackOf(MuxTrack track) => Sources[track.Source].File.Tracks.First(t => t.Id == track.TrackId);

    /// <summary>Problems that stop the multiplexer (in Slovak, for the user); empty when the spec can be written.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Sources.Count == 0)
        {
            problems.Add("Pridajte aspoň jeden vstupný súbor.");
        }

        if (Tracks.Count == 0)
        {
            problems.Add("Vyberte aspoň jednu stopu.");
        }

        foreach (var track in Tracks)
        {
            if (track.Source < 0 || track.Source >= Sources.Count || Sources[track.Source].File.Tracks.All(t => t.Id != track.TrackId))
            {
                problems.Add($"Stopa {track.TrackId} nie je v súbore {track.Source + 1}.");
            }
        }

        if (Tracks.Select(t => (t.Source, t.TrackId)).Distinct().Count() != Tracks.Count)
        {
            problems.Add("Niektorá stopa je vybraná dvakrát.");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            problems.Add("Zadajte výstupný súbor.");
            return problems;
        }

        var output = Path.GetFullPath(OutputPath);
        if (!OutputExtensions.Contains(Path.GetExtension(output), StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("Výstupný súbor musí mať príponu " + string.Join(", ", OutputExtensions) + ".");
        }

        if (Sources.Any(s => IO.Paths.Same(s.File.Path, output)))
        {
            problems.Add("Výstupný súbor nesmie byť jedným zo vstupných súborov.");
        }

        if (File.Exists(output))
        {
            problems.Add("Výstupný súbor už existuje – zvoľte iný názov (UNIVERSUM nič neprepisuje).");
        }

        if (Path.GetDirectoryName(output) is not { } folder || !Directory.Exists(folder))
        {
            problems.Add("Priečinok výstupného súboru neexistuje.");
        }

        if (!string.IsNullOrEmpty(ChaptersFile) && !File.Exists(ChaptersFile))
        {
            problems.Add("Súbor s kapitolami neexistuje.");
        }

        foreach (var attachment in Attachments.Where(a => !File.Exists(a.Path)))
        {
            problems.Add("Príloha neexistuje: " + attachment.Path);
        }

        return problems;
    }
}
