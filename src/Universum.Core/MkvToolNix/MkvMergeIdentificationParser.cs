using System.Globalization;
using System.Text.Json;
using Universum.Core.Media;

namespace Universum.Core.MkvToolNix;

/// <summary>Converts mkvmerge JSON identification output ("mkvmerge -J") into a <see cref="MediaFile"/>.</summary>
public static class MkvMergeIdentificationParser
{
    /// <exception cref="MkvToolNixException">The output is not valid identification JSON.</exception>
    public static MediaFile Parse(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement, path);
        }
        catch (JsonException ex)
        {
            throw new MkvToolNixException($"mkvmerge returned identification data that cannot be read: {ex.Message}", ex);
        }
    }

    private static MediaFile Parse(JsonElement root, string path)
    {
        var container = Child(root, "container");
        var containerProperties = Child(container, "properties");
        var trackTags = new Dictionary<int, int>();
        foreach (var entry in Array(root, "track_tags"))
        {
            if (Int(entry, "track_id") is { } trackId)
            {
                trackTags[trackId] = trackTags.GetValueOrDefault(trackId) + (Int(entry, "num_entries") ?? 0);
            }
        }

        var tracks = Array(root, "tracks").Select(t => ParseTrack(t, trackTags)).ToList();
        var duration = Long(containerProperties, "duration");

        return new MediaFile
        {
            Path = path,
            ContainerType = String(container, "type") ?? "",
            IsRecognized = Bool(container, "recognized") ?? false,
            IsSupported = Bool(container, "supported") ?? false,
            Title = String(containerProperties, "title"),
            Duration = duration is > 0 ? TimeSpan.FromTicks(duration.Value / 100) : null,
            WritingApplication = String(containerProperties, "writing_application"),
            Tracks = tracks,
            ChapterEntries = Array(root, "chapters").Sum(c => Int(c, "num_entries") ?? 0),
            GlobalTagEntries = Array(root, "global_tags").Sum(c => Int(c, "num_entries") ?? 0),
            AttachmentCount = Array(root, "attachments").Count(),
            Errors = Strings(root, "errors"),
            Warnings = Strings(root, "warnings"),
            IdentificationFormatVersion = Int(root, "identification_format_version"),
        };
    }

    private static MediaTrack ParseTrack(JsonElement track, Dictionary<int, int> trackTags)
    {
        var id = Int(track, "id") ?? -1;
        var properties = Child(track, "properties");
        var legacy = String(properties, "language");
        var ietf = String(properties, "language_ietf");

        return new MediaTrack
        {
            Id = id,
            Type = String(track, "type") switch
            {
                "video" => TrackType.Video,
                "audio" => TrackType.Audio,
                "subtitles" => TrackType.Subtitles,
                _ => TrackType.Other,
            },
            Codec = String(track, "codec") ?? "",
            CodecId = String(properties, "codec_id"),
            Name = String(properties, "track_name"),
            Language = legacy is null && ietf is null
                ? LanguageTag.Undetermined
                : new LanguageTag(legacy ?? "und", ietf),
            Flags = new TrackFlags(
                Default: Bool(properties, "default_track") ?? true,
                Enabled: Bool(properties, "enabled_track") ?? true,
                Forced: Bool(properties, "forced_track") ?? false,
                HearingImpaired: Bool(properties, "flag_hearing_impaired") ?? false,
                VisualImpaired: Bool(properties, "flag_visual_impaired") ?? false,
                Commentary: Bool(properties, "flag_commentary") ?? false,
                Original: Bool(properties, "flag_original") ?? false),
            Number = Int(properties, "number"),
            Uid = ULong(properties, "uid"),
            AudioChannels = Int(properties, "audio_channels"),
            AudioSamplingFrequency = Int(properties, "audio_sampling_frequency"),
            PixelDimensions = String(properties, "pixel_dimensions"),
            IsTextSubtitles = Bool(properties, "text_subtitles") ?? false,
            StatisticsFrames = Long(properties, "tag_number_of_frames"),
            StatisticsBitsPerSecond = Long(properties, "tag_bps"),
            IndexEntries = Int(properties, "num_index_entries"),
            TagEntries = trackTags.GetValueOrDefault(id),
        };
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        var array = Child(element, name);
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
    }

    private static List<string> Strings(JsonElement element, string name) =>
        Array(element, name).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();

    private static string? String(JsonElement element, string name)
    {
        var value = Child(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? Bool(JsonElement element, string name) => Child(element, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static int? Int(JsonElement element, string name)
    {
        var value = Child(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    /// <summary>Reads a number, also when written as a string (statistics tags are strings).</summary>
    private static long? Long(JsonElement element, string name)
    {
        var value = Child(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static ulong? ULong(JsonElement element, string name)
    {
        var value = Child(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;
    }
}
