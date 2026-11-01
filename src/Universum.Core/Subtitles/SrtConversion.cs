using System.Globalization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.Core.Subtitles;

/// <summary>
/// Text subtitles of another format written as SRT inside the result (owner, 1. 11. 2026: "WebVTT pridané k epizóde chcem
/// mať vo vnútri ako SRT – bez pridávania nového súboru, a pôvodné nech tam nie sú"): the track is read (from its own
/// file, from a Matroska file by mkvextract, from another container through a Matroska copy of it alone), written as SRT
/// in UTF-8 with UNIVERSUM's own writer, and that SRT takes the track's place. Only when the user ticks it.
/// </summary>
public static class SrtConversion
{
    /// <summary>
    /// The track can be written as SRT: text subtitles that are not SRT already (WebVTT, ASS, SSA, MP4 text …). External
    /// files lying with a video (Single, Hromadné spracovanie) are not offered – they are always written as SRT.
    /// </summary>
    public static bool Offers(MediaTrack track) =>
        track is { Type: TrackType.Subtitles, IsTextSubtitles: true, ExternalFile: null } && !IsSrt(track);

    /// <summary>
    /// SRT already: in a Matroska file by its codec ID, in a file of subtitles alone (no codec ID there) by mkvmerge's name.
    /// </summary>
    public static bool IsSrt(MediaTrack track) =>
        track.CodecId?.ToUpperInvariant() is "S_TEXT/UTF8" or "S_TEXT/ASCII" ||
        (track.CodecId is null && track.Codec.Equals("SubRip/SRT", StringComparison.OrdinalIgnoreCase));

    /// <summary>Styled subtitles (ASS, SSA): as SRT they lose their colours, fonts and places on the picture.</summary>
    public static bool IsStyled(MediaTrack track) =>
        track.CodecId?.ToUpperInvariant() is "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" ||
        track.Codec.Equals("SubStationAlpha", StringComparison.OrdinalIgnoreCase);

    /// <summary>The format named for the user ("WebVTT", "ASS").</summary>
    public static string FormatName(MediaTrack track) => track.CodecId?.ToUpperInvariant() switch
    {
        "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" => "WebVTT",
        "S_TEXT/ASS" or "S_ASS" => "ASS",
        "S_TEXT/SSA" or "S_SSA" => "SSA",
        "S_TEXT/USF" => "USF",
        _ when IsStyled(track) => "ASS/SSA",
        _ => track.Codec.Length > 0 ? track.Codec : track.CodecId ?? "?",
    };

    /// <summary>
    /// The subtitles of the track of <paramref name="file"/> as a document; null when they cannot be read. A file that is
    /// only subtitles (a .vtt, .ass added to Zlučovanie) is read directly, in its own encoding.
    /// </summary>
    public static async Task<SubtitleDocument?> ReadAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, MediaFile file, MediaTrack track, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!file.IsMatroska && file.Tracks.Count == 1 && SubtitleReader.TextExtensions.Contains(Path.GetExtension(file.Path)))
            {
                return await SubtitleReader.ReadFileAsync(file.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (file.IsMatroska)
            {
                return await SubtitleText.ExtractDocumentAsync(installation, runner, file, track, cancellationToken).ConfigureAwait(false);
            }

            // another container (MP4 …): the track alone copied into Matroska first, then read from there
            var copy = Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"track-{Guid.NewGuid():N}.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            try
            {
                using var options = MkvToolNixOptionsFile.Create(
                [
                    "--output", copy, "--no-video", "--no-audio", "--no-buttons", "--no-chapters", "--no-attachments", "--no-global-tags", "--no-track-tags",
                    "--subtitle-tracks", track.Id.ToString(CultureInfo.InvariantCulture), file.Path,
                ]);
                var made = await runner.RunAsync(
                    new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                    cancellationToken).ConfigureAwait(false);
                if (made.ExitCode is not (0 or 1) || !File.Exists(copy))
                {
                    return null;
                }

                var inspected = await new MkvMergeInspector(installation, runner).InspectAsync(copy, cancellationToken).ConfigureAwait(false);
                return inspected.TracksOf(TrackType.Subtitles).FirstOrDefault() is { } alone
                    ? await SubtitleText.ExtractDocumentAsync(installation, runner, inspected, alone, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            finally
            {
                File.Delete(copy);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Xml.XmlException or MkvToolNixException)
        {
            return null;
        }
    }

    /// <summary>The subtitles written as SRT in UTF-8 into <paramref name="srt"/>; false when they cannot be read.</summary>
    public static async Task<bool> WriteAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, MediaFile file, MediaTrack track, string srt, SubtitleTiming? timing = null,
        CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(installation, runner, file, track, cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return false;
        }

        if (timing is { IsNone: false })
        {
            document = timing.Apply(document);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(srt))!);
        await SrtWriter.SaveAsync(document, srt, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
