using System.Text.Json;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Core.Mp4;

/// <summary>
/// Ponechať MP4 (owner, 7. 11. 2026): a target state of UNIVERSUM's rules as the changes of an MP4's header, and the
/// check of the result – by UNIVERSUM's own reader (every expected value, and the media untouched sample for sample) and,
/// independently, by ffprobe (what players and media servers will see).
/// </summary>
public static class Mp4Targets
{
    /// <summary>The changes of the header the target asks for (mkvmerge's track number of an MP4 is its track ID).</summary>
    public static Mp4Change ChangeFor(TargetState target)
    {
        var tracks = new List<Mp4TrackChange>();
        foreach (var track in target.Tracks)
        {
            var change = new Mp4TrackChange(Id(track))
            {
                Language = track.LanguageChanged ? track.Language : null,
                Name = track.NameChanged ? track.Name ?? "" : null,
                Enabled = track.Flags.Default != track.Source.Flags.Enabled ? track.Flags.Default : null,
            };
            if (change.Language is not null || change.Name is not null || change.Enabled is not null)
            {
                tracks.Add(change);
            }
        }

        return new Mp4Change(tracks, target.TitleChanged ? target.NewTitle : null, target.OrderChanged ? target.Tracks.Select(Id).ToList() : null);
    }

    private static int Id(TrackTarget track) =>
        track.Source.Number ?? throw new InvalidOperationException($"track {track.TrackId} of the MP4 has no number (its ID in the file)");

    /// <summary>What the result does not have that the target wanted (empty: all as planned, the media untouched).</summary>
    public static IReadOnlyList<string> Verify(TargetState target, Mp4File before, Mp4File after) => Verify(ChangeFor(target), before, after);

    /// <summary>What the result does not have that the change asked for (empty: all as planned, the media untouched).</summary>
    public static IReadOnlyList<string> Verify(Mp4Change change, Mp4File before, Mp4File after)
    {
        var problems = new List<string>();
        var wanted = change.Tracks.ToDictionary(t => t.TrackId);
        var order = change.Order ?? before.Tracks.Select(t => t.Id).ToList();
        if (!after.Tracks.Select(t => t.Id).SequenceEqual(order))
        {
            problems.Add($"MP4 track order {string.Join(",", after.Tracks.Select(t => t.Id))}, expected {string.Join(",", order)}");
            return problems;
        }

        foreach (var now in after.Tracks)
        {
            var was = before.Tracks.Single(t => t.Id == now.Id);
            wanted.TryGetValue(now.Id, out var to);
            var where = $"MP4 track {now.Id}";
            var language = to?.Language is { } newLanguage ? Mp4Languages.Unpack(Mp4Languages.Pack(newLanguage)) : was.Language;
            Check(now.Language == language, $"{where}: language {now.Language}, expected {language}");
            if (to?.Name is { } name)
            {
                Check((now.Name ?? "") == name && (name.Length == 0 || now.HandlerName == name), $"{where}: name '{now.Name}' / '{now.HandlerName}', expected '{name}'");
            }
            else
            {
                Check(now.Name == was.Name && now.HandlerName == was.HandlerName, $"{where}: the name changed");
            }

            Check(now.Enabled == (to?.Enabled ?? was.Enabled), $"{where}: default (enabled) {now.Enabled}");
            Check(now.Handler == was.Handler && now.Codec == was.Codec && now.Timescale == was.Timescale && now.Duration == was.Duration,
                $"{where}: its kind, codec or length changed");
            var a = was.Samples;
            var b = now.Samples;
            Check(a.Offsets.AsSpan().SequenceEqual(b.Offsets) && a.Sizes.AsSpan().SequenceEqual(b.Sizes) && a.DecodeTimes.AsSpan().SequenceEqual(b.DecodeTimes) &&
                  a.CompositionOffsets.AsSpan().SequenceEqual(b.CompositionOffsets), $"{where}: the samples are not the same as before");
        }

        var title = change.Title is { } newTitle ? newTitle : before.Title ?? "";
        Check((after.Title ?? "") == title, $"MP4 title '{after.Title}', expected '{title}'");
        return problems;

        void Check(bool fine, string problem)
        {
            if (!fine)
            {
                problems.Add(problem);
            }
        }
    }

    /// <summary>
    /// What ffprobe sees in the result, compared with what UNIVERSUM's reader found in it (<paramref name="after"/>) and
    /// with the change: the streams in order and their kinds, the languages and names changed, the default disposition
    /// (MP4's enabled) of every sound and subtitle track, the title. Null when ffprobe could not tell (not counted then).
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ProbeAsync(IProcessRunner runner, string ffprobe, string path, Mp4Change change, Mp4File after, CancellationToken cancellationToken = default)
    {
        var output = new System.Text.StringBuilder();
        var result = await runner.RunAsync(new ProcessRequest(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path])
        {
            Timeout = TimeSpan.FromMinutes(2),
            OnOutputLine = line => output.AppendLine(line),
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output.ToString());
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToList();
            var problems = new List<string>();
            if (streams.Count < after.Tracks.Count)
            {
                return [$"ffprobe: {streams.Count} streams, expected {after.Tracks.Count}"];
            }

            var wanted = change.Tracks.ToDictionary(t => t.TrackId);
            for (var i = 0; i < after.Tracks.Count; i++)
            {
                var track = after.Tracks[i];
                var stream = streams[i];
                var tags = stream.TryGetProperty("tags", out var t) ? t : default;
                string? Tag(string name) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var value) ? value.GetString() : null;
                var expectedType = track.Kind switch
                {
                    Mp4TrackKind.Video => "video",
                    Mp4TrackKind.Audio => "audio",
                    Mp4TrackKind.Subtitles => "subtitle",
                    _ => null,
                };
                if (expectedType is not null && stream.GetProperty("codec_type").GetString() != expectedType)
                {
                    problems.Add($"ffprobe: stream {i} is {stream.GetProperty("codec_type").GetString()}, expected {expectedType}");
                    continue;
                }

                if (wanted.TryGetValue(track.Id, out var to))
                {
                    if (to.Language is { IsUndetermined: false } && Tag("language") != track.Language)
                    {
                        problems.Add($"ffprobe: stream {i} language {Tag("language")}, expected {track.Language}");
                    }

                    if (to.Name is { Length: > 0 } name && Tag("handler_name") != name)
                    {
                        problems.Add($"ffprobe: stream {i} name '{Tag("handler_name")}', expected '{name}'");
                    }
                }

                var isDefault = stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("default", out var d) && d.GetInt32() == 1;
                if (expectedType is "audio" or "subtitle" && track.Enabled != isDefault)
                {
                    problems.Add($"ffprobe: stream {i} default {isDefault}, expected {track.Enabled}");
                }
            }

            if (change.Title is { } wantedTitle)
            {
                var format = json.RootElement.GetProperty("format");
                var title = format.TryGetProperty("tags", out var formatTags) && formatTags.TryGetProperty("title", out var value) ? value.GetString() ?? "" : "";
                if (title != wantedTitle)
                {
                    problems.Add($"ffprobe: title '{title}', expected '{wantedTitle}'");
                }
            }

            return problems;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
