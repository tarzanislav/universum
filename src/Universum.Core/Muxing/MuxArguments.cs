using System.Globalization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;

namespace Universum.Core.Muxing;

/// <summary>mkvmerge arguments for a <see cref="MuxSpec"/> (passed through an options file, so any name is safe).</summary>
public static class MuxArguments
{
    public static IReadOnlyList<string> Build(MuxSpec spec, string outputPath)
    {
        // owner, 29. 10. 2026: no segment title unless the user typed one – mkvmerge would take it from the first file
        var arguments = new List<string> { "--gui-mode", "--output", outputPath, "--title", spec.Title ?? "" };

        var fileIds = spec.FileIds();
        for (var index = 0; index < spec.Sources.Count; index++)
        {
            var source = spec.Sources[index];
            var chosen = spec.Tracks.Where(t => t.Source == index).ToList();
            SelectTracks(arguments, source.File, chosen);
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
                TrackOptions(arguments, track, source.File.Tracks.First(t => t.Id == track.TrackId));
            }

            AddFile(arguments, source.File.Path, source.AdditionalParts);

            // appended files: the same tracks (same IDs), names and flags come from the file they continue; how a file
            // is read (character set, frame rate, timing fix, compression) is set for each file, or mkvmerge reads the
            // appended one differently (cp1250 subtitles without their character set lose all their entries)
            foreach (var appended in source.Appended)
            {
                SelectTracks(arguments, appended, chosen);
                if (!source.KeepChapters)
                {
                    arguments.Add("--no-chapters");
                }

                foreach (var track in chosen)
                {
                    ReadingOptions(arguments, track, source.File.Tracks.First(t => t.Id == track.TrackId).Type);
                }

                arguments.AddRange(["--no-attachments", "--no-global-tags", "--no-track-tags", "+"]);
                AddFile(arguments, appended.Path, []);
            }
        }

        arguments.AddRange(["--track-order", string.Join(",", spec.Tracks.Select(t =>
            fileIds[t.Source].ToString(CultureInfo.InvariantCulture) + ":" + t.TrackId.ToString(CultureInfo.InvariantCulture)))]);
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

    // a file, or a file with its additional parts read as one: "( a b c )"
    private static void AddFile(List<string> arguments, string path, IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
        {
            arguments.Add(path);
            return;
        }

        arguments.Add("(");
        arguments.Add(path);
        arguments.AddRange(parts);
        arguments.Add(")");
    }

    private static void SelectTracks(List<string> arguments, MediaFile file, List<MuxTrack> chosen)
    {
        Select(arguments, file, chosen, TrackType.Video, "--video-tracks", "--no-video");
        Select(arguments, file, chosen, TrackType.Audio, "--audio-tracks", "--no-audio");
        Select(arguments, file, chosen, TrackType.Subtitles, "--subtitle-tracks", "--no-subtitles");
    }

    private static void TrackOptions(List<string> arguments, MuxTrack track, MediaTrack source)
    {
        var id = track.TrackId.ToString(CultureInfo.InvariantCulture);
        arguments.AddRange(["--track-name", $"{id}:{track.Name}"]);
        arguments.AddRange(["--language", $"{id}:{MkvToolNixArguments.Code(track.Language)}"]);
        arguments.AddRange(["--default-track-flag", $"{id}:{Bit(track.Flags.Default)}"]);
        arguments.AddRange(["--forced-display-flag", $"{id}:{Bit(track.Flags.Forced)}"]);
        arguments.AddRange(["--track-enabled-flag", $"{id}:{Bit(track.Flags.Enabled)}"]);
        arguments.AddRange(["--hearing-impaired-flag", $"{id}:{Bit(track.Flags.HearingImpaired)}"]);
        arguments.AddRange(["--visual-impaired-flag", $"{id}:{Bit(track.Flags.VisualImpaired)}"]);
        arguments.AddRange(["--text-descriptions-flag", $"{id}:{Bit(track.Flags.TextDescriptions)}"]);
        arguments.AddRange(["--original-flag", $"{id}:{Bit(track.Flags.Original)}"]);
        arguments.AddRange(["--commentary-flag", $"{id}:{Bit(track.Flags.Commentary)}"]);

        var options = track.Options;
        if (options.DelayMs is { } delay && delay != 0)
        {
            arguments.AddRange(["--sync", $"{id}:{delay.ToString(CultureInfo.InvariantCulture)}"]);
        }

        ReadingOptions(arguments, track, source.Type);
        if (!string.IsNullOrEmpty(options.AspectRatio) && source.Type == TrackType.Video)
        {
            arguments.AddRange(["--aspect-ratio", $"{id}:{options.AspectRatio}"]);
        }

        if (!string.IsNullOrEmpty(options.Cropping) && source.Type == TrackType.Video)
        {
            arguments.AddRange(["--cropping", $"{id}:{options.Cropping}"]);
        }

    }

    /// <summary>How mkvmerge reads the track of a file: given for the file and again for each file appended to it.</summary>
    private static void ReadingOptions(List<string> arguments, MuxTrack track, TrackType type)
    {
        var id = track.TrackId.ToString(CultureInfo.InvariantCulture);
        var options = track.Options;
        if (!string.IsNullOrEmpty(options.DefaultDuration))
        {
            arguments.AddRange(["--default-duration", $"{id}:{options.DefaultDuration}"]);
        }

        if (options.FixBitstreamTiming && type == TrackType.Video)
        {
            arguments.AddRange(["--fix-bitstream-timing-information", $"{id}:1"]);
        }

        if (options.Compression != MuxCompression.Automatic)
        {
            arguments.AddRange(["--compression", $"{id}:{(options.Compression == MuxCompression.Zlib ? "zlib" : "none")}"]);
        }

        if (!string.IsNullOrEmpty(options.SubtitleCharset) && type == TrackType.Subtitles)
        {
            arguments.AddRange(["--sub-charset", $"{id}:{options.SubtitleCharset}"]);
        }
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
