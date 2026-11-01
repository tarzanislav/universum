using System.Text.RegularExpressions;
using Universum.Core.Library;
using Universum.Core.Media;

namespace Universum.Core.Muxing;

/// <summary>An input file of the multiplexer and what is taken from it besides its tracks.</summary>
public sealed record MuxSource(MediaFile File, bool KeepChapters = true, bool KeepAttachments = true, bool KeepTags = true)
{
    /// <summary>
    /// Further parts of the same file read as one ("VTS_01_1.VOB VTS_01_2.VOB"; mkvmerge "( a b )"): their content
    /// continues the file. Only their paths are needed.
    /// </summary>
    public IReadOnlyList<string> AdditionalParts { get; init; } = [];

    /// <summary>
    /// Files appended to this one (mkvmerge "+", e.g. the second half of an episode): their tracks continue this
    /// file's tracks with the same IDs; names, languages and flags are this file's. Their chapters follow
    /// <see cref="KeepChapters"/>; attachments and tags are not taken from them.
    /// </summary>
    public IReadOnlyList<MediaFile> Appended { get; init; } = [];

    /// <summary>Every file read for this source: the file, its additional parts and the appended files.</summary>
    public IEnumerable<string> AllPaths => [File.Path, .. AdditionalParts, .. Appended.Select(a => a.Path)];
}

/// <summary>How mkvmerge compresses a track's data.</summary>
public enum MuxCompression
{
    /// <summary>mkvmerge decides (the default; nothing is passed).</summary>
    Automatic,

    None,

    Zlib,
}

/// <summary>
/// Less common options of a track (MKVToolNix GUI: Properties – timestamps, video and subtitle properties). Null or
/// default values pass nothing to mkvmerge.
/// </summary>
public sealed partial record MuxTrackOptions
{
    /// <summary>Delay in milliseconds (negative: earlier); mkvmerge "--sync".</summary>
    public int? DelayMs { get; init; }

    /// <summary>Default duration or frame rate: "25p", "24000/1001p", "50i", "40ms".</summary>
    public string? DefaultDuration { get; init; }

    /// <summary>Fix bitstream timing information (video).</summary>
    public bool FixBitstreamTiming { get; init; }

    public MuxCompression Compression { get; init; }

    /// <summary>Display aspect ratio of a video track: "16/9", "1.85".</summary>
    public string? AspectRatio { get; init; }

    /// <summary>Cropping of a video track in pixels: "left,top,right,bottom".</summary>
    public string? Cropping { get; init; }

    /// <summary>Character set of text subtitles that are not UTF-8: "cp1250" (Czech and Slovak in Windows), "iso-8859-2".</summary>
    public string? SubtitleCharset { get; init; }

    public bool IsEmpty => this == new MuxTrackOptions();

    /// <summary>What is wrong with the values (Slovak), for track <paramref name="where"/>.</summary>
    public IEnumerable<string> Problems(string where)
    {
        if (DelayMs is { } delay && Math.Abs(delay) > 86_400_000)
        {
            yield return where + ": oneskorenie najviac ± 24 hodín.";
        }

        if (!string.IsNullOrEmpty(DefaultDuration) && !DurationPattern().IsMatch(DefaultDuration))
        {
            yield return where + ": predvolené trvanie / FPS zadajte napr. 25p, 24000/1001p, 50i alebo 40ms.";
        }

        if (!string.IsNullOrEmpty(AspectRatio) && !AspectPattern().IsMatch(AspectRatio))
        {
            yield return where + ": pomer strán zadajte napr. 16/9 alebo 1.85.";
        }

        if (!string.IsNullOrEmpty(Cropping) && !CroppingPattern().IsMatch(Cropping))
        {
            yield return where + ": orez zadajte ako vľavo,hore,vpravo,dole v pixeloch, napr. 0,140,0,140.";
        }

        if (!string.IsNullOrEmpty(SubtitleCharset) && !CharsetPattern().IsMatch(SubtitleCharset))
        {
            yield return where + ": kódovanie titulkov zadajte napr. cp1250 alebo iso-8859-2.";
        }
    }

    [GeneratedRegex(@"^\d{1,6}(\.\d{1,6})?(/\d{1,6})?(p|i|fps|ms|us|ns)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^\d{1,5}(\.\d{1,4})?(/\d{1,5}(\.\d{1,4})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex AspectPattern();

    [GeneratedRegex(@"^\d{1,5},\d{1,5},\d{1,5},\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex CroppingPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CharsetPattern();
}

/// <summary>A track that goes into the output, with the values it gets there.</summary>
/// <param name="Source">Index of the input file in <see cref="MuxSpec.Sources"/>.</param>
/// <param name="TrackId">mkvmerge track ID within that file.</param>
/// <param name="Name">Track name; null or empty = no name.</param>
public sealed record MuxTrack(int Source, int TrackId, string? Name, LanguageTag Language, TrackFlags Flags)
{
    public MuxTrackOptions Options { get; init; } = new();
}

/// <summary>A file attached to the output (fonts, cover).</summary>
public sealed record MuxAttachment(string Path, string? Description = null);

/// <summary>
/// What the multiplexer writes (MKVToolNix GUI: Multiplexer): input files, the tracks taken from them in output
/// order with their names, languages and flags, the title, a chapter file and attachments, and the output file.
/// </summary>
public sealed record MuxSpec(IReadOnlyList<MuxSource> Sources, IReadOnlyList<MuxTrack> Tracks, string OutputPath)
{
    /// <summary>The problem reported when there is neither an output file nor a placement.</summary>
    public const string MissingOutput = "Zadajte výstupný súbor.";

    public static IReadOnlyList<string> OutputExtensions { get; } = [".mkv", ".mka", ".mks", ".webm"];

    /// <summary>Segment title; null or empty = none (mkvmerge may take the first input's title).</summary>
    public string? Title { get; init; }

    /// <summary>Chapters from a file (XML, OGM or cue sheet) instead of / besides the inputs' chapters.</summary>
    public string? ChaptersFile { get; init; }

    public IReadOnlyList<MuxAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Place of the output named by the rules (in the library or beside the sources); the size class is known only
    /// after writing, so the final name is chosen then. When set, <see cref="OutputPath"/> is not used.
    /// </summary>
    public Placement? Placement { get; init; }

    /// <summary>
    /// Only parts of the film are written – a preview (owner, 1. 11. 2026): the value of mkvmerge's "--split parts:…",
    /// the parts joined into one file; null: the whole film.
    /// </summary>
    public string? PreviewParts { get; init; }

    /// <summary>Every file the multiplexer reads: the sources with their parts and appended files, the chapters, the attachments.</summary>
    public IEnumerable<string> InputPaths() =>
        [.. Sources.SelectMany(s => s.AllPaths), .. string.IsNullOrEmpty(ChaptersFile) ? [] : new[] { ChaptersFile }, .. Attachments.Select(a => a.Path)];

    public MediaTrack TrackOf(MuxTrack track) => Sources[track.Source].File.Tracks.First(t => t.Id == track.TrackId);

    /// <summary>
    /// mkvmerge file IDs of the sources: every source and every appended file counts (additional parts do not), so
    /// "a + b c" gives a = 0, b = 1, c = 2.
    /// </summary>
    public IReadOnlyList<int> FileIds()
    {
        var ids = new List<int>();
        var next = 0;
        foreach (var source in Sources)
        {
            ids.Add(next);
            next += 1 + source.Appended.Count;
        }

        return ids;
    }

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
                continue;
            }

            problems.AddRange(track.Options.Problems($"Stopa {Tracks.ToList().IndexOf(track) + 1}"));
        }

        if (Tracks.Select(t => (t.Source, t.TrackId)).Distinct().Count() != Tracks.Count)
        {
            problems.Add("Niektorá stopa je vybraná dvakrát.");
        }

        var allPaths = Sources.SelectMany(s => s.AllPaths).ToList();
        if (allPaths.Distinct(IO.Paths.Comparer).Count() != allPaths.Count)
        {
            problems.Add("Ten istý súbor je pridaný dvakrát (ako vstup, časť alebo pripojený súbor).");
        }

        for (var index = 0; index < Sources.Count; index++)
        {
            var source = Sources[index];
            foreach (var part in source.AdditionalParts.Where(p => !File.Exists(p)))
            {
                problems.Add("Časť súboru neexistuje: " + part);
            }

            // appended files continue the tracks taken from this file: same IDs, same types and codecs
            foreach (var appended in source.Appended)
            {
                foreach (var track in Tracks.Where(t => t.Source == index))
                {
                    var own = source.File.Tracks.FirstOrDefault(t => t.Id == track.TrackId);
                    var other = appended.Tracks.FirstOrDefault(t => t.Id == track.TrackId);
                    if (own is not null && (other is null || other.Type != own.Type || !string.Equals(other.CodecId ?? other.Codec, own.CodecId ?? own.Codec, StringComparison.Ordinal)))
                    {
                        problems.Add($"Súbor {Path.GetFileName(appended.Path)} sa nedá pripojiť k {Path.GetFileName(source.File.Path)}: stopa {track.TrackId} v ňom chýba alebo je iná.");
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(ChaptersFile) && !File.Exists(ChaptersFile))
        {
            problems.Add("Súbor s kapitolami neexistuje.");
        }

        foreach (var attachment in Attachments.Where(a => !File.Exists(a.Path)))
        {
            problems.Add("Príloha neexistuje: " + attachment.Path);
        }

        if (Placement is not null)
        {
            return problems;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            problems.Add(MissingOutput);
            return problems;
        }

        var output = Path.GetFullPath(OutputPath);
        if (!OutputExtensions.Contains(Path.GetExtension(output), StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("Výstupný súbor musí mať príponu " + string.Join(", ", OutputExtensions) + ".");
        }

        if (allPaths.Any(p => IO.Paths.Same(p, output)))
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

        return problems;
    }
}
