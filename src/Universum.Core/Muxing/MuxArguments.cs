using System.Globalization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;

namespace Universum.Core.Muxing;

/// <summary>mkvmerge arguments for a <see cref="MuxSpec"/> (passed through an options file, so any name is safe).</summary>
public static class MuxArguments
{
    public static IReadOnlyList<string> Build(MuxSpec spec, string outputPath)
    {
        var arguments = new List<string> { "--gui-mode", "--output", outputPath };
        if (!string.IsNullOrEmpty(spec.Title))
        {
            arguments.AddRange(["--title", spec.Title]);
        }

        for (var index = 0; index < spec.Sources.Count; index++)
        {
            var source = spec.Sources[index];
            var chosen = spec.Tracks.Where(t => t.Source == index).ToList();
            Select(arguments, source.File, chosen, TrackType.Video, "--video-tracks", "--no-video");
            Select(arguments, source.File, chosen, TrackType.Audio, "--audio-tracks", "--no-audio");
            Select(arguments, source.File, chosen, TrackType.Subtitles, "--subtitle-tracks", "--no-subtitles");
            if (!source.KeepChapters)
            {
                arguments.Add("--no-chapters");
            }

            if (!source.KeepAttachments)
            {
                arguments.Add("--no-attachments");
            }

            if (!source.KeepTags)
            {
                arguments.AddRange(["--no-global-tags", "--no-track-tags"]);
            }

            foreach (var track in chosen)
            {
                var id = track.TrackId.ToString(CultureInfo.InvariantCulture);
                arguments.AddRange(["--track-name", $"{id}:{track.Name}"]);
                arguments.AddRange(["--language", $"{id}:{MkvToolNixArguments.Code(track.Language)}"]);
                arguments.AddRange(["--default-track-flag", $"{id}:{Bit(track.Flags.Default)}"]);
                arguments.AddRange(["--forced-display-flag", $"{id}:{Bit(track.Flags.Forced)}"]);
                arguments.AddRange(["--track-enabled-flag", $"{id}:{Bit(track.Flags.Enabled)}"]);
            }

            arguments.Add(source.File.Path);
        }

        arguments.AddRange(["--track-order", string.Join(",", spec.Tracks.Select(t =>
            t.Source.ToString(CultureInfo.InvariantCulture) + ":" + t.TrackId.ToString(CultureInfo.InvariantCulture)))]);
        if (!string.IsNullOrEmpty(spec.ChaptersFile))
        {
            arguments.AddRange(["--chapters", spec.ChaptersFile]);
        }

        foreach (var attachment in spec.Attachments)
        {
            if (!string.IsNullOrEmpty(attachment.Description))
            {
                arguments.AddRange(["--attachment-description", attachment.Description]);
            }

            arguments.AddRange(["--attach-file", attachment.Path]);
        }

        return arguments;
    }

    /// <summary>"--audio-tracks 1,3" when only some are taken, "--no-audio" when none, nothing when all.</summary>
    private static void Select(List<string> arguments, MediaFile file, List<MuxTrack> chosen, TrackType type, string keepOption, string noneOption)
    {
        var all = file.TracksOf(type).Select(t => t.Id).ToList();
        if (all.Count == 0)
        {
            return;
        }

        var kept = chosen.Select(t => t.TrackId).Where(all.Contains).ToList();
        if (kept.Count == 0)
        {
            arguments.Add(noneOption);
        }
        else if (kept.Count < all.Count)
        {
            arguments.AddRange([keepOption, string.Join(",", kept.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)))]);
        }
    }

    private static string Bit(bool value) => value ? "1" : "0";
}
