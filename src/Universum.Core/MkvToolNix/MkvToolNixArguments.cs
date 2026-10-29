using System.Globalization;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.Core.MkvToolNix;

/// <summary>Builds mkvpropedit and mkvmerge arguments for a target state.</summary>
public static class MkvToolNixArguments
{
    /// <summary>mkvpropedit arguments that change the header in place (track order must not change).</summary>
    /// <param name="chaptersXml">Chapter file written from <see cref="TargetState.NewChapters"/> (null = none, chapters removed).</param>
    public static IReadOnlyList<string> HeaderEdit(TargetState target, string filePath, string? chaptersXml = null)
    {
        if (target.NeedsRewrite)
        {
            throw new InvalidOperationException("mkvpropedit cannot change the track order or create an index.");
        }

        var arguments = new List<string> { filePath };
        if (target.TagsChanged)
        {
            arguments.AddRange(["--tags", "all:"]);
        }

        if (target.ChaptersChanged)
        {
            arguments.AddRange(["--chapters", chaptersXml ?? ""]);
        }

        if (target.TitleChanged)
        {
            arguments.AddRange(["--edit", "info"]);
            arguments.AddRange(target.ResultTitle is null ? ["--delete", "title"] : ["--set", "title=" + target.ResultTitle]);
        }

        for (var position = 0; position < target.Tracks.Count; position++)
        {
            var track = target.Tracks[position];
            if (!track.HasChanges)
            {
                continue;
            }

            arguments.AddRange(["--edit", Selector(track.Source, position)]);
            if (track.NameChanged)
            {
                arguments.AddRange(track.Name is null ? ["--delete", "name"] : ["--set", "name=" + track.Name]);
            }

            if (track.LanguageChanged)
            {
                arguments.AddRange(["--set", "language=" + Code(track.Language)]);
            }

            AddFlag(arguments, "flag-default", track.Source.Flags.Default, track.Flags.Default);
            AddFlag(arguments, "flag-enabled", track.Source.Flags.Enabled, track.Flags.Enabled);
            AddFlag(arguments, "flag-forced", track.Source.Flags.Forced, track.Flags.Forced);
            AddFlag(arguments, "flag-hearing-impaired", track.Source.Flags.HearingImpaired, track.Flags.HearingImpaired);
            AddFlag(arguments, "flag-visual-impaired", track.Source.Flags.VisualImpaired, track.Flags.VisualImpaired);
            AddFlag(arguments, "flag-text-descriptions", track.Source.Flags.TextDescriptions, track.Flags.TextDescriptions);
            AddFlag(arguments, "flag-original", track.Source.Flags.Original, track.Flags.Original);
            AddFlag(arguments, "flag-commentary", track.Source.Flags.Commentary, track.Flags.Commentary);
        }

        return arguments;
    }

    /// <summary>mkvmerge arguments that write the target state to a new file (track order may change).</summary>
    /// <param name="chaptersXml">Chapter file written from <see cref="TargetState.NewChapters"/> (null = none).</param>
    public static IReadOnlyList<string> Remux(TargetState target, string sourcePath, string outputPath, string? chaptersXml = null)
    {
        var arguments = new List<string> { "--gui-mode", "--output", outputPath };
        if (target.TitleChanged)
        {
            arguments.AddRange(["--title", target.ResultTitle ?? ""]);
        }

        if (target.RemoveTags)
        {
            arguments.AddRange(["--no-global-tags", "--no-track-tags", "--disable-track-statistics-tags"]);
        }

        if (target.Removed.Count > 0)
        {
            AddSelection(arguments, target, TrackType.Audio, "--audio-tracks", "--no-audio");
            AddSelection(arguments, target, TrackType.Subtitles, "--subtitle-tracks", "--no-subtitles");
        }

        foreach (var track in target.Tracks)
        {
            var id = track.TrackId.ToString(CultureInfo.InvariantCulture);
            arguments.AddRange(["--track-name", $"{id}:{track.Name}"]);
            if (track.LanguageChanged)
            {
                arguments.AddRange(["--language", $"{id}:{Code(track.Language)}"]);
            }

            arguments.AddRange(["--default-track-flag", $"{id}:{Bit(track.Flags.Default)}"]);
            arguments.AddRange(["--track-enabled-flag", $"{id}:{Bit(track.Flags.Enabled)}"]);
            arguments.AddRange(["--forced-display-flag", $"{id}:{Bit(track.Flags.Forced)}"]);

            // the other flags are written only when the rules change them (mkvmerge keeps them from the source otherwise)
            if (track.OtherFlagsChanged)
            {
                arguments.AddRange(["--hearing-impaired-flag", $"{id}:{Bit(track.Flags.HearingImpaired)}"]);
                arguments.AddRange(["--visual-impaired-flag", $"{id}:{Bit(track.Flags.VisualImpaired)}"]);
                arguments.AddRange(["--text-descriptions-flag", $"{id}:{Bit(track.Flags.TextDescriptions)}"]);
                arguments.AddRange(["--original-flag", $"{id}:{Bit(track.Flags.Original)}"]);
                arguments.AddRange(["--commentary-flag", $"{id}:{Bit(track.Flags.Commentary)}"]);
            }
        }

        if (target.ChaptersChanged)
        {
            // the file's own chapters are left out; the new ones come from the chapter file
            arguments.Add("--no-chapters");
        }

        arguments.Add(sourcePath);
        if (target.ChaptersChanged && chaptersXml is not null)
        {
            arguments.AddRange(["--chapters", chaptersXml]);
        }

        arguments.AddRange(["--track-order", string.Join(",", target.Tracks.Select(t => "0:" + t.TrackId.ToString(CultureInfo.InvariantCulture)))]);
        return arguments;
    }

    /// <summary>Selects a track by its Matroska track number ("track:@3"), or by position when it is unknown.</summary>
    public static string Selector(MediaTrack track, int position) =>
        track.Number is { } number
            ? "track:@" + number.ToString(CultureInfo.InvariantCulture)
            : "track:" + (position + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Language value for MKVToolNix: the IETF tag when known (sets both elements), else the legacy code.</summary>
    public static string Code(LanguageTag language) =>
        !string.IsNullOrWhiteSpace(language.Ietf) && !language.IsUndetermined ? language.Ietf : language.Legacy;

    /// <summary>Keeps only the target's tracks of one type ("--audio-tracks 1,3" or "--no-audio").</summary>
    private static void AddSelection(List<string> arguments, TargetState target, TrackType type, string keepOption, string noneOption)
    {
        var kept = target.Tracks.Where(t => t.Type == type).Select(t => t.TrackId.ToString(CultureInfo.InvariantCulture)).ToList();
        if (kept.Count == 0)
        {
            arguments.Add(noneOption);
        }
        else if (target.Removed.Any(t => t.Type == type))
        {
            arguments.AddRange([keepOption, string.Join(",", kept)]);
        }
    }

    private static void AddFlag(List<string> arguments, string name, bool before, bool after)
    {
        if (before != after)
        {
            arguments.AddRange(["--set", $"{name}={Bit(after)}"]);
        }
    }

    private static string Bit(bool value) => value ? "1" : "0";
}
