using Universum.Core.Chapters;
using Universum.Core.Media;

namespace Universum.Core.Rules;

/// <summary>Result of the rules for one file: all tracks in their final order and the file-level changes.</summary>
public sealed record TargetState(MediaFile Source, IReadOnlyList<TrackTarget> Tracks, bool RemoveTags, IReadOnlyList<RuleIssue> Issues)
{
    /// <summary>The file is rewritten to create a missing index (cues) for the video.</summary>
    public bool RepairIndex { get; init; }

    /// <summary>Ponechať MP4 is chosen (the profile): an MP4 stays MP4 when MP4 can hold all its changes.</summary>
    public bool KeepMp4 { get; init; }

    /// <summary>
    /// The result stays MP4 – its header edited in place by UNIVERSUM (owner, 7. 11. 2026): Ponechať MP4, an MP4, and
    /// nothing MP4 cannot hold (see <see cref="Mp4Obstacle"/>). Otherwise the MP4 becomes an MKV as always.
    /// </summary>
    public bool StaysMp4 => KeepMp4 && Source.IsMp4 && Mp4Obstacles().FirstOrDefault() is null;

    /// <summary>
    /// Why an MP4 does not stay MP4 although Ponechať MP4 is chosen – the first thing MP4 cannot hold, in words ("odstraňujú
    /// sa stopy"); null when it stays, or when it is no MP4 or Ponechať MP4 is off.
    /// </summary>
    public string? Mp4Obstacle => KeepMp4 && Source.IsMp4 ? Mp4Obstacles().FirstOrDefault() : null;

    // MP4 holds the order, the title, and per track the language, the name and one flag (enabled = the default); a track
    // left out or added, new times, a cut, chapters, a new index, proportions, SRT, and the flags of Matroska MP4 has no
    // place for (forced, hearing impaired, commentary …, a default track switched off) make an MKV
    private IEnumerable<string> Mp4Obstacles()
    {
        // subtitles left out go only from the header (their few bytes stay, 8. 11. 2026); a picture or sound left out makes
        // an MKV – the file is to be smaller
        if (RemovedFromFile.Any(t => t.Type != TrackType.Subtitles))
        {
            yield return Text.CoreText.T("odstraňuje sa obraz alebo zvuk (súbor sa zmenší)");
        }

        if (AddsFiles)
        {
            yield return Text.CoreText.T("pridávajú sa titulky zo súboru");
        }

        if (TimingChanged)
        {
            yield return Text.CoreText.T("menia sa časy titulkov");
        }

        if (IsCut)
        {
            yield return Text.CoreText.T("strih");
        }

        if (ChaptersChanged)
        {
            yield return Text.CoreText.T("kapitoly");
        }

        if (RepairIndex)
        {
            yield return Text.CoreText.T("oprava indexu");
        }

        if (Tracks.Any(t => t.AspectRatio is not null))
        {
            yield return Text.CoreText.T("nový pomer strán obrazu");
        }

        if (Tracks.Any(t => t.ToSrt))
        {
            yield return Text.CoreText.T("titulky do SRT");
        }

        // forced, hearing impaired, commentary and visual impaired an MP4 holds as roles of its tracks (8. 11. 2026)
        if (Tracks.Any(t => !Mp4.Mp4Roles.CanHold(t.Source.Flags, t.Flags)))
        {
            yield return Text.CoreText.T("príznak pôvodný jazyk alebo textový popis");
        }

        if (Tracks.Any(t => t.Flags.Default && !t.Flags.Enabled))
        {
            yield return Text.CoreText.T("predvolená stopa je vypnutá");
        }
    }

    /// <summary>
    /// What changes in an MP4 that stays MP4: the order, the title, and per track the language, the name and the default
    /// track (MP4's "enabled" flag – MP4 has no other default), and the roles: forced, hearing impaired, commentary.
    /// </summary>
    public bool Mp4Changes => OrderChanged || TitleChanged || RemovedFromFile.Any() || Tracks.Any(t => t.NameChanged || t.LanguageChanged || t.Flags.Default != t.Source.Flags.Enabled ||
                                                                               !Mp4.Mp4Roles.Of(t.Flags).SequenceEqual(Mp4.Mp4Roles.Of(t.Source.Flags)));

    /// <summary>Tracks left out of the result (only audio and subtitles can be removed).</summary>
    public IReadOnlyList<MediaTrack> Removed { get; init; } = [];

    /// <summary>Subtitle tracks without a single subtitle (owner, 30. 10. 2026): left out.</summary>
    public IReadOnlyList<MediaTrack> RemovedEmpty { get; init; } = [];

    /// <summary>Subtitles left out because their language is not among the profile's subtitle languages.</summary>
    public IReadOnlyList<MediaTrack> RemovedByLanguage { get; init; } = [];

    /// <summary>New segment title: null keeps the title of the file, "" removes it.</summary>
    public string? NewTitle { get; init; }

    /// <summary>New chapters (chapter editor): null keeps the file's chapters, an empty list removes them.</summary>
    public IReadOnlyList<Chapter>? NewChapters { get; init; }

    /// <summary>The chapters are replaced.</summary>
    public bool ChaptersChanged => NewChapters is not null;

    /// <summary>The parts of the film kept (Úprava videostopy: strih – owner, 30. 10. 2026); null keeps it whole.</summary>
    public IReadOnlyList<Video.CutPart>? Cut { get; init; }

    /// <summary>The film is cut.</summary>
    public bool IsCut => Cut is { Count: > 0 };

    /// <summary>Number of chapter entries the result has.</summary>
    public int ResultChapterEntries => NewChapters?.Count ?? Source.ChapterEntries;

    /// <summary>The segment title changes (header editor).</summary>
    public bool TitleChanged => NewTitle is not null && !string.Equals(NewTitle, Source.Title ?? "", StringComparison.Ordinal);

    /// <summary>The segment title the result has.</summary>
    public string? ResultTitle => TitleChanged ? (NewTitle!.Length == 0 ? null : NewTitle) : Source.Title;

    /// <summary>True when the track order differs from the file (requires writing a new file).</summary>
    public bool OrderChanged => !Tracks.Where(t => !t.IsAdded).Select(t => t.TrackId).SequenceEqual(Source.Tracks.Where(t => t.ExternalFile is null).Select(t => t.Id));

    public bool TagsChanged => RemoveTags && Source.HasTags && !StaysMp4;

    /// <summary>
    /// The file is not Matroska (MP4, AVI, TS…): the result is always a new MKV written by mkvmerge – never a header edit
    /// and never the file copied as it is (owner, 27. 10. 2026: batch processing converts every video format it can).
    /// </summary>
    public bool ContainerChanged => !Source.IsMatroska && !StaysMp4;

    /// <summary>External files are added to the video (subtitles lying with it).</summary>
    public bool AddsFiles => Tracks.Any(t => t.IsAdded);

    /// <summary>The times of some subtitles change.</summary>
    public bool TimingChanged => Tracks.Any(t => t.TimingChanged);

    /// <summary>Tracks of the file itself left out (an external file not added is no change of the file).</summary>
    public IEnumerable<MediaTrack> RemovedFromFile => Removed.Where(t => t.ExternalFile is null);

    public bool HasChanges => StaysMp4 ? Mp4Changes : ContainerChanged || OrderChanged || RepairIndex || TagsChanged || TitleChanged || ChaptersChanged || RemovedFromFile.Any() ||
                              IsCut || Tracks.Any(t => t.HasChanges);

    /// <summary>
    /// A new file must be written (another container, order, removed tracks, index repair, added files, new times of
    /// subtitles); otherwise a header edit is enough.
    /// </summary>
    public bool NeedsRewrite => !StaysMp4 && (ContainerChanged || OrderChanged || RepairIndex || AddsFiles || TimingChanged || IsCut || Tracks.Any(t => t.AspectRatio is not null || t.ToSrt));

    public bool IsBlocked => Issues.Any(i => i.IsBlocking);

    public TrackTarget For(int trackId) => Tracks.First(t => t.TrackId == trackId);
}
