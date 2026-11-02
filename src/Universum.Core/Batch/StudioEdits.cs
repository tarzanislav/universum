using System.Text.Json.Serialization;
using Universum.Core.Audio;
using Universum.Core.IO;
using Universum.Core.Media;
using Universum.Core.Rules;
using Universum.Core.Subtitles;
using Universum.Core.Text;
using Universum.Core.Video;

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
/// What Úprava videostopy decided for a file of Single or Hromadné spracovanie (owner, 2. 11. 2026: Obraz v štúdiu →
/// Uložiť do úlohy, also for a file of a MEGA batch): the parts kept as marked (<paramref name="Parts"/>) and as they will
/// be written (<paramref name="Kept"/> – exactly, or moved to the key frames the studio read), the picture written anew by
/// <paramref name="Recipe"/> (also for an exact cut) and the proportions it is shown at (<paramref name="Aspect"/>, as
/// chosen: "16/9").
/// </summary>
public sealed record StudioVideo(IReadOnlyList<CutPart> Parts, IReadOnlyList<CutPart> Kept, bool ExactCut, VideoRecipe Recipe, string? Aspect)
{
    /// <summary>The picture is written anew (a change of it, or the exact cut).</summary>
    [JsonIgnore]
    public bool WritesPicture => Recipe.NeedsEncoding || (ExactCut && Parts.Count > 0);

    /// <summary>The proportions written: those chosen, turned with a picture turned by 90°.</summary>
    [JsonIgnore]
    public string? WrittenAspect => Aspect is null ? null : Recipe.Rotation % 180 == 90 ? VideoAspects.Upright(Aspect) : Aspect;

    /// <summary>Nothing of the picture changes.</summary>
    [JsonIgnore]
    public bool IsNone => Parts.Count == 0 && Aspect is null && !WritesPicture;

    /// <summary>The video's own picture (the track written anew is made of it).</summary>
    public static MediaTrack? OwnPicture(MediaFile file) => file.TracksOf(TrackType.Video).FirstOrDefault(t => t.ExternalFile is null);

    /// <summary>
    /// The picture of <paramref name="file"/> written anew, a track of its own (<paramref name="id"/>) with key frames where
    /// each part begins and ends (the cut is then exact); null when the picture is not written anew.
    /// </summary>
    public MediaTrack? Picture(MediaFile file, int id)
    {
        if (!WritesPicture || OwnPicture(file) is not { } video)
        {
            return null;
        }

        var marks = Cut.Normalized(Parts).SelectMany(p => new[] { p.Start, p.End }).Distinct().Order().ToList();
        return new VideoEncoding(file.Path, video, Recipe, marks, file.IsMatroska).ToTrack(id);
    }

    /// <summary>In words, each change apart: "strih 2", "pomer strán 16:9", "otočiť o 90°", "720 riadkov", "obraz znova".</summary>
    public IEnumerable<string> Describe()
    {
        if (Parts.Count > 0)
        {
            yield return CoreText.F("strih {0}", Parts.Count);
        }

        if (Aspect is not null)
        {
            yield return CoreText.F("pomer strán {0}", VideoAspects.NameOf(Aspect));
        }

        if (Recipe.Rotation % 360 != 0)
        {
            yield return CoreText.F("otočiť o {0}°", Recipe.Rotation);
        }

        if (Recipe.Height is { } height)
        {
            yield return CoreText.F("{0} riadkov", height);
        }

        if (WritesPicture)
        {
            yield return CoreText.T("obraz znova");
        }
    }
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

    /// <summary>Úprava videostopy: the parts kept, the proportions, the picture written anew; null = the picture as it is.</summary>
    public StudioVideo? Video { get; init; }

    // these edits with the sound part of other
    private StudioEdits WithSoundsOf(StudioEdits? other) => other is null ? this : this with
    {
        SoundStates = other.SoundStates, SoundLanguages = other.SoundLanguages, SoundNames = other.SoundNames, RemovedSounds = other.RemovedSounds,
        SoundRecipes = other.SoundRecipes, AddedSounds = other.AddedSounds,
    };

    /// <summary>The edits as they are now (<paramref name="current"/>) with the subtitle part of these (Úprava titulkostopy saves its part only).</summary>
    public StudioEdits SubtitlesOver(StudioEdits? current) => WithSoundsOf(current) with { Video = current?.Video };

    /// <summary>The edits as they are now with the sound part of these (Úprava audiostopy saves its part only).</summary>
    public StudioEdits SoundsOver(StudioEdits? current) => (current ?? new StudioEdits()).WithSoundsOf(this);

    /// <summary>The edits as they are now with the picture part of these (Úprava videostopy saves its part only).</summary>
    public StudioEdits VideoOver(StudioEdits? current) => (current ?? new StudioEdits()) with { Video = Video is { IsNone: false } video ? video : null };

    public bool IsEmpty => Timings.Count == 0 && FileTimings.Count == 0 && Languages.Count == 0 && FileLanguages.Count == 0 && Forced.Count == 0 &&
                           FileForced.Count == 0 && Removed.Count == 0 && RemovedFiles.Count == 0 && Added.Count == 0 && Names.Count == 0 && FileNames.Count == 0 &&
                           SoundLanguages.Count == 0 && SoundNames.Count == 0 && RemovedSounds.Count == 0 && SoundRecipes.Count == 0 && AddedSounds.Count == 0 &&
                           Video is null or { IsNone: true };

    /// <summary>In words, for the batch: "časy titulkov 2, pridané 1, vynechané 1".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Timings.Count + FileTimings.Count is > 0 and var timed)
        {
            parts.Add(CoreText.F("časy titulkov {0}", timed));
        }

        if (Languages.Count + FileLanguages.Count is > 0 and var languages)
        {
            parts.Add(CoreText.F("jazyk {0}", languages));
        }

        if (Names.Count + FileNames.Count is > 0 and var named)
        {
            parts.Add(CoreText.F("názvy {0}", named));
        }

        if (Added.Count > 0)
        {
            parts.Add(CoreText.F("pridané {0}", Added.Count));
        }

        if (Removed.Count + RemovedFiles.Count is > 0 and var removed)
        {
            parts.Add(CoreText.F("vynechané {0}", removed));
        }

        if (SoundRecipes.Count > 0 || SoundLanguages.Count + SoundNames.Count > 0)
        {
            parts.Add(CoreText.F("zvuk {0}", SoundRecipes.Keys.Concat(SoundLanguages.Keys).Concat(SoundNames.Keys).Distinct().Count()));
        }

        if (AddedSounds.Count > 0)
        {
            parts.Add(CoreText.F("pridaný zvuk {0}", AddedSounds.Count));
        }

        if (RemovedSounds.Count > 0)
        {
            parts.Add(CoreText.F("vynechaný zvuk {0}", RemovedSounds.Count));
        }

        if (Video is { } video)
        {
            parts.AddRange(video.Describe());
        }

        return parts.Count == 0 ? CoreText.T("bez zmien") : string.Join(", ", parts);
    }

    /// <summary>
    /// The file with the subtitle files and sounds added in the studios (one already among its tracks is not added twice),
    /// the sounds of the video written anew (a track each, beside the video's own) and its picture written anew.
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

        // the picture written anew (Úprava videostopy): a track of its own; the rules leave the video's own out
        if (!tracks.Any(t => t.VideoEncode is { } written && Paths.Same(written.Path, file.Path)) && Video?.Picture(file, next) is { } picture)
        {
            tracks.Add(picture);
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

        // the picture: the video's own left out when it is written anew, the parts kept and the proportions
        var cut = decisions.Cut;
        var aspect = decisions.VideoAspect;
        if (Video is { } video)
        {
            foreach (var written in file.TracksOf(TrackType.Video).Select(t => t.VideoEncode).OfType<VideoEncoding>().Where(v => Paths.Same(v.Path, file.Path)))
            {
                removed.Add(written.Source.Id);
            }

            cut = video.Kept.Count > 0 ? video.Kept : null;
            aspect = video.WrittenAspect;
        }

        return decisions with { Timings = timings, Languages = languages, Forced = forced, Removed = removed, Names = names, Order = order, Cut = cut, VideoAspect = aspect };
    }

    // by path, as Windows compares names
    private static SubtitleTiming? ByPath(Dictionary<string, SubtitleTiming> values, string path) =>
        values.FirstOrDefault(v => Paths.Same(v.Key, path)).Value;
}
