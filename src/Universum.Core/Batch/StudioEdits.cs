using Universum.Core.Audio;
using Universum.Core.IO;
using Universum.Core.Media;
using Universum.Core.Rules;
using Universum.Core.Subtitles;

namespace Universum.Core.Batch;

/// <summary>An external subtitle file added to a video in Úprava titulkostopy (not one the analysis found with it).</summary>
public sealed record StudioFile(string Path, LanguageTag Language, bool Forced, bool HearingImpaired, string CodecId, string Codec, bool IsText, long? Frames)
{
    /// <summary>The file as a subtitle track of the video (<paramref name="id"/>: after the video's own tracks).</summary>
    public MediaTrack ToTrack(int id) => new()
    {
        Id = id, Type = TrackType.Subtitles, CodecId = CodecId, Codec = Codec, IsTextSubtitles = IsText, Language = Language,
        Flags = new TrackFlags(Default: false, Forced: Forced, HearingImpaired: HearingImpaired), StatisticsFrames = Frames, ExternalFile = Path,
    };
}

/// <summary>
/// A sound added in Úprava audiostopy: the audio track <paramref name="Ordinal"/> of another file (<paramref name="Source"/>
/// as UNIVERSUM read it there, with its ID in that file), its language, name and what becomes of it.
/// </summary>
public sealed record StudioSound(string Path, int Ordinal, MediaTrack Source, LanguageTag Language, string? Name, AudioRecipe Recipe)
{
    /// <summary>The track of the video (<paramref name="id"/>: after the video's own tracks); written anew when the recipe says so.</summary>
    public MediaTrack ToTrack(int id) => (Recipe.NeedsEncoding
        ? new SoundEncoding(Path, Ordinal, Source, Recipe).ToTrack(id)
        : Source with { Id = id, ExternalFile = Path, Flags = Source.Flags with { Default = false } }) with { Language = Language };

    /// <summary>The track of the video is this sound.</summary>
    public bool Is(MediaTrack track) =>
        track.ExternalFile is { } file && Paths.Same(file, Path) &&
        (track.Encode is { } encode ? encode.Source.ExternalTrack == Source.ExternalTrack : track.ExternalTrack == Source.ExternalTrack);
}

/// <summary>
/// What Úprava titulkostopy decided for one file of Hromadné spracovanie (owner, 30. 10. 2026: Upraviť v štúdiu → Uložiť
/// do úlohy): new times of subtitle tracks, the tracks left out, their languages and the subtitle files added. The
/// video's own tracks are known by their IDs, external files by their paths (their IDs are given by each analysis).
/// The batch, MEGA batches and the job apply it; the repairs of the text are not part of it.
/// </summary>
public sealed record StudioEdits
{
    /// <summary>New times of the video's own subtitle tracks, by track ID.</summary>
    public Dictionary<int, SubtitleTiming> Timings { get; init; } = [];

    /// <summary>New times of external subtitle files (found by the analysis or added here), by path.</summary>
    public Dictionary<string, SubtitleTiming> FileTimings { get; init; } = [];

    /// <summary>Languages chosen for the video's own subtitle tracks, by track ID.</summary>
    public Dictionary<int, LanguageTag> Languages { get; init; } = [];

    /// <summary>Languages chosen for external files found by the analysis, by path.</summary>
    public Dictionary<string, LanguageTag> FileLanguages { get; init; } = [];

    /// <summary>Forced chosen for the video's own subtitle tracks, by track ID (only where the studio changed it).</summary>
    public Dictionary<int, bool> Forced { get; init; } = [];

    /// <summary>Forced chosen for external files found by the analysis, by path.</summary>
    public Dictionary<string, bool> FileForced { get; init; } = [];

    /// <summary>Track names written for the video's own tracks, by track ID (empty = no name).</summary>
    public Dictionary<int, string> Names { get; init; } = [];

    /// <summary>Track names written for external files (found by the analysis or added here), by path.</summary>
    public Dictionary<string, string> FileNames { get; init; } = [];

    /// <summary>
    /// The values of the studio's rows (FPS, speed, shift, as typed) – the studio shows them again exactly when the task
    /// is opened again; the plan uses the times above.
    /// </summary>
    public List<StudioTrackState> States { get; init; } = [];

    /// <summary>Subtitle tracks of the video left out.</summary>
    public List<int> Removed { get; init; } = [];

    /// <summary>External files found by the analysis but left out.</summary>
    public List<string> RemovedFiles { get; init; } = [];

    /// <summary>External files added in the studio.</summary>
    public List<StudioFile> Added { get; init; } = [];

    /// <summary>Úprava audiostopy: the values of its rows, to be shown again exactly when the task is opened again.</summary>
    public List<AudioTrackState> SoundStates { get; init; } = [];

    /// <summary>Languages chosen for the video's own audio tracks, by track ID.</summary>
    public Dictionary<int, LanguageTag> SoundLanguages { get; init; } = [];

    /// <summary>Names written for the video's own audio tracks, by track ID (empty = no name).</summary>
    public Dictionary<int, string> SoundNames { get; init; } = [];

    /// <summary>Audio tracks of the video left out.</summary>
    public List<int> RemovedSounds { get; init; } = [];

    /// <summary>What becomes of the video's own audio tracks (a shift; written anew), by track ID.</summary>
    public Dictionary<int, AudioRecipe> SoundRecipes { get; init; } = [];

    /// <summary>Sounds added from other files.</summary>
    public List<StudioSound> AddedSounds { get; init; } = [];

    /// <summary>These edits with the sound part of <paramref name="other"/> (Úprava titulkostopy saves its part only).</summary>
    public StudioEdits WithSoundsOf(StudioEdits? other) => other is null ? this : this with
    {
        SoundStates = other.SoundStates, SoundLanguages = other.SoundLanguages, SoundNames = other.SoundNames, RemovedSounds = other.RemovedSounds,
        SoundRecipes = other.SoundRecipes, AddedSounds = other.AddedSounds,
    };

    /// <summary>The sound part of these edits with the subtitle part of <paramref name="other"/> (Úprava audiostopy saves its part only).</summary>
    public StudioEdits WithSubtitlesOf(StudioEdits? other) => (other ?? new StudioEdits()).WithSoundsOf(this);

    public bool IsEmpty => Timings.Count == 0 && FileTimings.Count == 0 && Languages.Count == 0 && FileLanguages.Count == 0 && Forced.Count == 0 &&
                           FileForced.Count == 0 && Removed.Count == 0 && RemovedFiles.Count == 0 && Added.Count == 0 && Names.Count == 0 && FileNames.Count == 0 &&
                           SoundLanguages.Count == 0 && SoundNames.Count == 0 && RemovedSounds.Count == 0 && SoundRecipes.Count == 0 && AddedSounds.Count == 0;

    /// <summary>In words, for the batch: "časy titulkov 2, pridané 1, vynechané 1".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Timings.Count + FileTimings.Count is > 0 and var timed)
        {
            parts.Add($"časy titulkov {timed}");
        }

        if (Languages.Count + FileLanguages.Count is > 0 and var languages)
        {
            parts.Add($"jazyk {languages}");
        }

        if (Names.Count + FileNames.Count is > 0 and var named)
        {
            parts.Add($"názvy {named}");
        }

        if (Added.Count > 0)
        {
            parts.Add($"pridané {Added.Count}");
        }

        if (Removed.Count + RemovedFiles.Count is > 0 and var removed)
        {
            parts.Add($"vynechané {removed}");
        }

        if (SoundRecipes.Count > 0 || SoundLanguages.Count + SoundNames.Count > 0)
        {
            parts.Add($"zvuk {SoundRecipes.Keys.Concat(SoundLanguages.Keys).Concat(SoundNames.Keys).Distinct().Count()}");
        }

        if (AddedSounds.Count > 0)
        {
            parts.Add($"pridaný zvuk {AddedSounds.Count}");
        }

        if (RemovedSounds.Count > 0)
        {
            parts.Add($"vynechaný zvuk {RemovedSounds.Count}");
        }

        return parts.Count == 0 ? "bez zmien" : string.Join(", ", parts);
    }

    /// <summary>
    /// The file with the subtitle files and sounds added in the studios (one already among its tracks is not added twice)
    /// and the sounds of the video written anew (a track each, beside the video's own).
    /// </summary>
    public MediaFile Apply(MediaFile file)
    {
        var tracks = file.Tracks.ToList();
        var next = tracks.Count == 0 ? 0 : tracks.Max(t => t.Id) + 1;
        foreach (var added in Added.Where(a => !file.Tracks.Any(t => t.ExternalFile is { } known && Paths.Same(known, a.Path))))
        {
            tracks.Add(added.ToTrack(next++));
        }

        var own = file.TracksOf(TrackType.Audio).Where(t => t.ExternalFile is null).ToList();
        for (var ordinal = 0; ordinal < own.Count; ordinal++)
        {
            if (SoundRecipes.TryGetValue(own[ordinal].Id, out var recipe) && recipe.NeedsEncoding && !RemovedSounds.Contains(own[ordinal].Id) &&
                !tracks.Any(t => t.Encode is { } encode && Paths.Same(encode.Path, file.Path) && encode.Source.Id == own[ordinal].Id))
            {
                tracks.Add(new SoundEncoding(file.Path, ordinal, own[ordinal], recipe).ToTrack(next++));
            }
        }

        foreach (var sound in AddedSounds.Where(s => !tracks.Any(s.Is)))
        {
            tracks.Add(sound.ToTrack(next++));
        }

        return tracks.Count == file.Tracks.Count ? file : file with { Tracks = tracks };
    }

    /// <summary>The decisions of the file (its tracks with the added files – <see cref="Apply(MediaFile)"/>) with these edits.</summary>
    public TrackDecisions Apply(TrackDecisions decisions, MediaFile file)
    {
        var timings = decisions.Timings.ToDictionary();
        var languages = decisions.Languages.ToDictionary();
        var forced = decisions.Forced.ToDictionary();
        var removed = decisions.Removed.ToHashSet();
        var names = decisions.Names.ToDictionary();
        foreach (var track in file.Tracks.Where(t => t.Type is TrackType.Audio or TrackType.Subtitles))
        {
            var path = track.ExternalFile;
            if ((path is null ? Names.GetValueOrDefault(track.Id) : FileNames.FirstOrDefault(n => Paths.Same(n.Key, path)).Value) is { } name)
            {
                names[track.Id] = name;
            }

            if (track.Type != TrackType.Subtitles)
            {
                continue;
            }

            if ((path is null ? Timings.GetValueOrDefault(track.Id) : ByPath(FileTimings, path)) is { IsNone: false } timing)
            {
                timings[track.Id] = timing;
            }

            if (path is null && Languages.TryGetValue(track.Id, out var language))
            {
                languages[track.Id] = language;
            }
            else if (path is not null && FileLanguages.FirstOrDefault(l => Paths.Same(l.Key, path)) is { Key: not null } chosen)
            {
                languages[track.Id] = chosen.Value;
            }

            // forced: a file added here as it was added, the others where the studio changed it
            if (path is not null && Added.FirstOrDefault(a => Paths.Same(a.Path, path)) is { } added)
            {
                forced[track.Id] = added.Forced;
            }
            else if (path is null && Forced.TryGetValue(track.Id, out var isForced))
            {
                forced[track.Id] = isForced;
            }
            else if (path is not null && FileForced.FirstOrDefault(f => Paths.Same(f.Key, path)) is { Key: not null } fileForced)
            {
                forced[track.Id] = fileForced.Value;
            }

            if (path is null ? Removed.Contains(track.Id) : RemovedFiles.Any(r => Paths.Same(r, path)))
            {
                removed.Add(track.Id);
            }
        }

        // sounds: the video's own (left out, language, name, shift – or written anew in a track of its own), the added ones
        var order = decisions.Order.ToList();
        foreach (var track in file.TracksOf(TrackType.Audio))
        {
            if (track.ExternalFile is null)
            {
                if (RemovedSounds.Contains(track.Id))
                {
                    removed.Add(track.Id);
                }

                if (SoundLanguages.TryGetValue(track.Id, out var language))
                {
                    languages[track.Id] = language;
                }

                if (SoundNames.TryGetValue(track.Id, out var name))
                {
                    names[track.Id] = name;
                }

                if (SoundRecipes.TryGetValue(track.Id, out var recipe))
                {
                    if (recipe.NeedsEncoding)
                    {
                        removed.Add(track.Id);
                    }
                    else if (!recipe.Shift.IsNone)
                    {
                        timings[track.Id] = recipe.Shift;
                    }
                }
            }
            else if (track.Encode is { } encode && Paths.Same(encode.Path, file.Path))
            {
                // the video's sound written anew: what was decided for its track (here or for the group), in its place
                var original = encode.Source.Id;
                if ((SoundLanguages.TryGetValue(original, out var language) ? language : decisions.Languages.TryGetValue(original, out var decided) ? decided : (LanguageTag?)null) is { } chosen)
                {
                    languages[track.Id] = chosen;
                }

                if ((SoundNames.GetValueOrDefault(original) ?? decisions.Names.GetValueOrDefault(original)) is { } writtenName)
                {
                    names[track.Id] = writtenName;
                }

                if (!encode.Recipe.Shift.IsNone)
                {
                    timings[track.Id] = encode.Recipe.Shift;
                }

                if (order.IndexOf(original) is var at and >= 0)
                {
                    order[at] = track.Id;
                }
            }
            else if (AddedSounds.FirstOrDefault(s => s.Is(track)) is { } sound)
            {
                // an undetermined one is left to the batch (its language can be chosen there)
                if (!sound.Language.IsUndetermined)
                {
                    languages[track.Id] = sound.Language;
                }

                if (sound.Name is not null)
                {
                    names[track.Id] = sound.Name;
                }

                if (!sound.Recipe.Shift.IsNone)
                {
                    timings[track.Id] = sound.Recipe.Shift;
                }
            }
        }

        return decisions with { Timings = timings, Languages = languages, Forced = forced, Removed = removed, Names = names, Order = order };
    }

    // by path, as Windows compares names
    private static SubtitleTiming? ByPath(Dictionary<string, SubtitleTiming> values, string path) =>
        values.FirstOrDefault(v => Paths.Same(v.Key, path)).Value;
}
