using Universum.Core.Media;
using Universum.Core.Rules;
using Universum.Core.Text;

namespace Universum.Core.Subtitles;

/// <summary>
/// External subtitles as tracks of the video (owner, 30. 10. 2026, bod 5): the subtitle files that belong to it
/// (<see cref="ExternalSubtitles"/>) become tracks of its own with <see cref="MediaTrack.ExternalFile"/> – the rules
/// order them and set their flags like any other subtitles, and the result has them. Text subtitles get their language
/// from the text (návrh UNIVERSUM, like the subtitles of the video), otherwise from the name; "forced" in the name makes
/// them forced, "sdh" for the hearing impaired. When the video is written they are converted to SRT in UTF-8.
/// </summary>
public static class ExternalSubtitleTracks
{
    /// <summary>The ID of the first added track (the video's own IDs are smaller).</summary>
    public const int FirstId = 1000;

    /// <summary>
    /// The video with the subtitle files lying with it that belong to it (unchanged when there are none); a file the video
    /// already holds (a subtitle track of the same language with as many subtitles – a result processed again) is not added.
    /// </summary>
    /// <param name="siblings">The files of the video's folder.</param>
    /// <param name="videosInFolder">How many videos the folder holds.</param>
    public static async Task<MediaFile> AddAsync(
        MediaFile file, IReadOnlyList<string> siblings, int videosInFolder, IReadOnlyCollection<string>? forcedKeywords = null, CancellationToken cancellationToken = default)
    {
        var matches = ExternalSubtitles.For(file.Path, siblings, videosInFolder, forcedKeywords);
        if (matches.Count == 0)
        {
            return file;
        }

        var fps = file.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
        var tracks = new List<MediaTrack>(file.Tracks);
        var id = Math.Max(FirstId, file.Tracks.Select(t => t.Id + 1).DefaultIfEmpty(0).Max());
        foreach (var match in matches)
        {
            if (await TrackOfAsync(match, id, fps, cancellationToken).ConfigureAwait(false) is { } track &&
                !file.TracksOf(TrackType.Subtitles).Any(t => t.ExternalFile is null && t.StatisticsFrames is > 0 && t.StatisticsFrames == track.StatisticsFrames &&
                                                             t.Language.Primary == track.Language.Primary))
            {
                tracks.Add(track);
                id++;
            }
        }

        return file with { Tracks = tracks };
    }

    /// <summary>The track of one subtitle file; null when it is not subtitles UNIVERSUM can read.</summary>
    public static async Task<MediaTrack?> TrackOfAsync(SubtitleFileMatch match, int id, double? framesPerSecond, CancellationToken cancellationToken = default)
    {
        var language = match.Language ?? LanguageTag.Undetermined;
        var name = match.Forced ? UniversumRules.ForcedName : null;
        var flags = new TrackFlags(Default: false, Forced: false, HearingImpaired: match.HearingImpaired);
        if (match.IsPicture)
        {
            var pgs = Path.GetExtension(match.Path).Equals(".sup", StringComparison.OrdinalIgnoreCase);
            return new MediaTrack
            {
                Id = id, Type = TrackType.Subtitles, CodecId = pgs ? "S_HDMV/PGS" : "S_VOBSUB", Codec = CoreText.F("{0} (externý súbor)", pgs ? "PGS" : "VobSub"),
                Language = language, Name = name, Flags = flags, ExternalFile = match.Path,
            };
        }

        SubtitleDocument document;
        try
        {
            document = await SubtitleReader.ReadFileAsync(match.Path, framesPerSecond, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        if (SubtitleLanguage.Detect(document.PlainText) is { } read && read.Replaces(language))
        {
            language = LanguageTag.FromCode(read.Language);
        }

        return new MediaTrack
        {
            Id = id, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Codec = CoreText.F("{0} (externý súbor)", document.Format), IsTextSubtitles = true,
            Language = language, Name = name, Flags = flags, StatisticsFrames = document.Cues.Count, ExternalFile = match.Path,
        };
    }

    /// <summary>
    /// The external text subtitles of a target written as SRT in UTF-8 into <paramref name="folder"/> (by their original
    /// path), right before mkvmerge reads them; the problem when one cannot be read (then nothing is written).
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, string> Inputs, string? Problem)> ConvertAsync(TargetState target, string folder, CancellationToken cancellationToken = default)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var fps = target.Source.TracksOf(TrackType.Video).FirstOrDefault()?.FramesPerSecond;
        foreach (var track in target.Tracks.Where(t => t.IsAdded && t.Source.IsTextSubtitles))
        {
            var path = track.Source.ExternalFile!;
            if (inputs.ContainsKey(path))
            {
                continue;
            }

            try
            {
                var document = await SubtitleReader.ReadFileAsync(path, fps, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(folder);
                var converted = Path.Combine(folder, $"{inputs.Count + 1:00}.srt");
                await SrtWriter.SaveAsync(document, converted, cancellationToken).ConfigureAwait(false);
                inputs[path] = converted;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return (inputs, $"the subtitle file {Path.GetFileName(path)} cannot be read: {ex.Message}");
            }
        }

        return (inputs, null);
    }
}
