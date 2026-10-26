using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Planning;
using Universum.Core.Rules;
using Universum.Core.Templates;
using Universum.Core.Text;

namespace Universum.Core.Batch;

/// <summary>
/// Turns an analysed folder, a template and the users' group decisions into a simulation: what will happen with each
/// file and why some files need review. Nothing is written here.
/// </summary>
public static class BatchPlanner
{
    /// <summary>Share of subtitles below which a track is suggested as forced (compared with a full track).</summary>
    public const double ForcedShare = 0.15;

    /// <param name="library">Titles and episodes for the library; without it the items have no place (rules only).</param>
    public static IReadOnlyList<BatchItem> Plan(BatchAnalysis analysis, BatchTemplate template, RuleProfile? profile = null, LibraryJob? library = null)
    {
        var effective = template.Apply(profile ?? RuleProfile.Default);
        var items = analysis.Groups.SelectMany(g => g.Files.Select(f => PlanFile(f, g, template, effective, library))).ToList();
        return library is null ? items : library.Kind == MediaKind.Series ? CheckEpisodes(items, library) : CheckFilms(items);
    }

    /// <summary>
    /// Versions of one film must be the same film: when files named after different films ("Okno (2014)", "Ticho (2010)")
    /// get the same title, the ones named otherwise go for review ("process anyway" lets them through).
    /// </summary>
    private static List<BatchItem> CheckFilms(List<BatchItem> items)
    {
        var flagged = new Dictionary<int, string>();
        foreach (var same in Enumerable.Range(0, items.Count).Where(i => items[i].Title is not null).GroupBy(i => items[i].Title!.FolderName, Paths.Comparer))
        {
            var own = same.Select(i => (Index: i, Name: TitleGuess.FromName(Path.GetFileNameWithoutExtension(items[i].File.Path), MediaKind.Movie)?.Name))
                .Where(x => x.Name is not null).ToList();
            if (own.Select(x => Words.Normalize(x.Name)).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            var title = Words.Normalize(items[same.First()].Title!.Name);
            foreach (var (index, name) in own.Where(x => Words.Normalize(x.Name) != title))
            {
                flagged[index] = name!;
            }
        }

        return items.Select((item, i) => flagged.TryGetValue(i, out var name) && !item.Group.Decisions.ProcessAnyway.Contains(item.File.Path)
            ? item with { Reasons = [.. item.Reasons, new ReviewReason(ReviewReasonKind.OtherSeries, Detail: name)] }
            : item).ToList();
    }

    /// <summary>
    /// Checks across the files of a series (specification 13.5): two files of the same episode with the same description
    /// go for review, and so does a file whose name names another series than most files (one folder is one series).
    /// "Process anyway" lets a file through; a second version of an episode then gets a number (K1).
    /// </summary>
    private static List<BatchItem> CheckEpisodes(List<BatchItem> items, LibraryJob library)
    {
        var added = items.Select(_ => new List<ReviewReason>()).ToList();
        var series = LibraryJob.SeriesNameOfFiles(items.Select(i => i.File.Path), library.Root);
        for (var i = 0; i < items.Count; i++)
        {
            var path = items[i].File.Path;
            var name = Path.GetFileNameWithoutExtension(path);
            // an episode named after another series, or a film in library form ("Okno (2014)") among the episodes
            var own = EpisodeParser.FromPath(path, library.Root) is not null ? TitleGuess.FromName(name, MediaKind.Series)
                : TitleGuess.HasYearInParentheses(name) ? TitleGuess.FromName(name, MediaKind.Movie)
                : null;
            var expected = series?.Name ?? library.Series?.Name;
            if (own is not null && expected is not null && Words.Normalize(own.Name) != Words.Normalize(expected) &&
                (library.Series is null || Words.Normalize(own.Name) != Words.Normalize(library.Series.Name)))
            {
                added[i].Add(new ReviewReason(ReviewReasonKind.OtherSeries, Detail: own.Name));
            }
        }

        // files already in their place are versions kept earlier (K1); only new results are compared
        var placed = Enumerable.Range(0, items.Count)
            .Where(i => items[i] is { Placement: not null, Episode: not null } item && !item.Placement.Holds(item.File.Path, item.FileSize))
            .ToList();
        foreach (var same in placed.GroupBy(i => Path.GetFullPath(items[i].Placement!.Directory) + "\n" + items[i].Placement!.Stem + "\n" + items[i].Placement!.Sizes.Label(items[i].FileSize), Paths.Comparer))
        {
            var list = same.ToList();
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    var (x, y) = (items[list[a]], items[list[b]]);
                    if (Overlap(x.Episode!, y.Episode!))
                    {
                        added[list[a]].Add(new ReviewReason(ReviewReasonKind.DuplicateEpisode, Detail: Path.GetFileName(y.File.Path)));
                        added[list[b]].Add(new ReviewReason(ReviewReasonKind.DuplicateEpisode, Detail: Path.GetFileName(x.File.Path)));
                    }
                }
            }
        }

        return items.Select((item, i) =>
        {
            var extra = item.Group.Decisions.ProcessAnyway.Contains(item.File.Path) ? [] : added[i].DistinctBy(r => r.Kind).ToList();
            return extra.Count == 0 ? item : item with { Reasons = [.. item.Reasons, .. extra] };
        }).ToList();
    }

    private static bool Overlap(EpisodeNumber x, EpisodeNumber y) =>
        x.Season == y.Season && x.Episode <= (y.LastEpisode ?? y.Episode) && y.Episode <= (x.LastEpisode ?? x.Episode);

    public static BatchItem PlanFile(MediaFile file, BatchGroup group, BatchTemplate template, RuleProfile profile, LibraryJob? library = null)
    {
        var evidence = new Dictionary<SlotKey, LanguageEvidence>();
        var languages = new Dictionary<int, LanguageTag>();
        var forced = new Dictionary<int, bool>();
        foreach (var (key, track) in SlotKey.Of(file))
        {
            if (Evidence(track, key, group) is { } found)
            {
                evidence[key] = found;
                if (found.IsUsable && track.Language.IsUndetermined)
                {
                    languages[track.Id] = found.Language;
                }
            }

            if (track.Type == TrackType.Subtitles && group.Decisions.Forced.TryGetValue(key, out var isForced))
            {
                forced[track.Id] = isForced;
            }
        }

        var decisions = new TrackDecisions { Languages = languages, Forced = forced };
        var target = UniversumRules.Apply(file, profile, decisions);
        var match = template.Evaluate(target);
        if (template.Unlisted == UnlistedTracks.Remove && match.Unlisted.Count > 0)
        {
            decisions = decisions with { Removed = match.Unlisted.Select(t => t.TrackId).ToHashSet() };
            target = UniversumRules.Apply(file, profile, decisions);
            match = template.Evaluate(target);
        }

        var slots = SlotKey.Of(file).ToDictionary(x => x.Track.Id, x => x.Key);
        var reasons = new List<ReviewReason>();
        foreach (var issue in target.Issues.Where(i => i.IsBlocking))
        {
            reasons.Add(issue.Kind switch
            {
                RuleIssueKind.UndeterminedLanguage => new ReviewReason(ReviewReasonKind.UndeterminedLanguage, issue.TrackId is { } id ? slots[id] : null),
                RuleIssueKind.MultipleVideoTracks => new ReviewReason(ReviewReasonKind.MultipleVideoTracks),
                _ => new ReviewReason(ReviewReasonKind.NoAudio),
            });
        }

        if (!group.Decisions.ProcessAnyway.Contains(file.Path))
        {
            reasons.AddRange(match.MissingRequired.Select(s => new ReviewReason(ReviewReasonKind.MissingRequiredSlot, TemplateSlot: s)));
        }

        var item = new BatchItem(file, group, decisions, target, EditPlan.For(target), match, evidence, reasons);
        return library is null ? item : Place(item, library, profile, reasons);
    }

    /// <summary>Title, episode and place in the library (specification 13.5); missing decisions become review reasons.</summary>
    private static BatchItem Place(BatchItem item, LibraryJob library, RuleProfile profile, List<ReviewReason> reasons)
    {
        var path = item.File.Path;
        if (!library.KindConfirmed)
        {
            reasons.Add(new ReviewReason(ReviewReasonKind.KindNotConfirmed, Detail: library.Kind == MediaKind.Series ? "seriál" : "filmy"));
        }

        var anyway = item.Group.Decisions.ProcessAnyway.Contains(path);
        if (EpisodeParser.IsExtra(path, library.Root) && !anyway)
        {
            reasons.Add(new ReviewReason(ReviewReasonKind.ExtraFile));
        }

        // films: an episode would become a version of a film
        if (library.Kind == MediaKind.Movie && !anyway &&
            (LibraryJob.LooksLikeEpisode(path, library.Root) || EpisodeParser.SeasonOfPath(path, library.Root) is not null))
        {
            reasons.Add(new ReviewReason(ReviewReasonKind.EpisodeAmongFilms));
        }

        var title = library.TitleFor(path);
        if (title is null || title.Problem is not null)
        {
            reasons.Add(new ReviewReason(ReviewReasonKind.MissingTitle, Detail: title?.Problem));
            return item;
        }

        EpisodeNumber? episode = null;
        if (title.Kind == MediaKind.Series)
        {
            var (number, suggested) = library.EpisodeFor(path);
            if (number is null || suggested)
            {
                reasons.Add(new ReviewReason(ReviewReasonKind.MissingEpisode, Detail: library.EpisodeDoubt(path)));
                return item with { Title = title, Episode = number };
            }

            episode = number;
            if (!library.TmdbKnows(number) && !item.Group.Decisions.ProcessAnyway.Contains(path))
            {
                reasons.Add(new ReviewReason(ReviewReasonKind.UnknownEpisode));
            }
        }

        var placement = library.Layout.Place(item.Target, title, episode, profile);
        if (placement.EstimatedPathLength(new FileInfo(path).Length) > LibraryNames.MaxPathLength)
        {
            reasons.Add(new ReviewReason(ReviewReasonKind.PathTooLong));
        }

        return item with { Title = title, Episode = episode, Placement = placement };
    }

    /// <summary>
    /// Best evidence for a track language: the file itself, a decision for the group, the original track name,
    /// or a suggestion waiting for confirmation.
    /// </summary>
    public static LanguageEvidence? Evidence(MediaTrack track, SlotKey key, BatchGroup group)
    {
        if (!track.Language.IsUndetermined)
        {
            return new LanguageEvidence(track.Language, Confidence.High, EvidenceSource.File);
        }

        if (group.Decisions.Languages.TryGetValue(key, out var decided))
        {
            return new LanguageEvidence(decided, Confidence.Confirmed, EvidenceSource.GroupDecision);
        }

        if (LanguageGuess.FromName(track.Name) is { } named)
        {
            return new LanguageEvidence(named, Confidence.High, EvidenceSource.TrackName);
        }

        return group.Suggestions.TryGetValue(key, out var suggestion) ? suggestion : null;
    }

    /// <summary>
    /// Fills <see cref="BatchGroup.Suggestions"/> and <see cref="BatchGroup.ForcedSuggestions"/> (specification 7.3, 7.4); call
    /// again after decisions change, so a decision in one group can suggest languages for another:
    /// undetermined tracks resembling a track with a known language elsewhere in the folder, and subtitles with very
    /// few entries compared with a full track of the same language. Suggestions are never used without confirmation.
    /// </summary>
    public static void Suggest(BatchAnalysis analysis, RuleProfile? profile = null)
    {
        profile ??= RuleProfile.Default;
        var references = new List<(Fingerprint Print, string Language, BatchGroup Group)>();
        foreach (var group in analysis.Groups)
        {
            foreach (var file in group.Files)
            {
                foreach (var (key, track) in SlotKey.Of(file))
                {
                    if (Evidence(track, key, group) is { IsUsable: true } known)
                    {
                        references.Add((Fingerprint.Of(track), known.Language.Primary, group));
                    }
                }
            }
        }

        foreach (var group in analysis.Groups)
        {
            // similarity suggestions are recalculated; suggestions from reading subtitle text are kept (expensive)
            foreach (var key in group.Suggestions.Where(s => s.Value.Source == EvidenceSource.Similarity).Select(s => s.Key).ToList())
            {
                group.Suggestions.Remove(key);
            }

            group.ForcedSuggestions.Clear();
            var representative = group.Representative;
            foreach (var (key, track) in SlotKey.Of(representative))
            {
                if (Evidence(track, key, group) is { IsUsable: true })
                {
                    continue;
                }

                var print = Fingerprint.Of(track);
                var candidates = references.Where(r => r.Group != group && r.Print == print).Select(r => r.Language).Distinct().ToList();
                if (candidates.Count == 1)
                {
                    group.Suggestions[key] = new LanguageEvidence(LanguageTag.FromCode(candidates[0]), Confidence.Medium, EvidenceSource.Similarity);
                }
            }

            SuggestForced(group, representative, profile);
        }
    }

    private static void SuggestForced(BatchGroup group, MediaFile file, RuleProfile profile)
    {
        var subtitles = SlotKey.Of(file).Where(x => x.Key.Type == TrackType.Subtitles)
            .Select(x => (x.Key, x.Track, Language: Evidence(x.Track, x.Key, group)?.Language))
            .ToList();
        foreach (var (key, track, language) in subtitles)
        {
            if (language is not { } tag || !profile.IsPreferred(tag.Primary) || track.Flags.Forced ||
                Words.ContainsAny(track.Name, profile.ForcedKeywords) || group.Decisions.Forced.ContainsKey(key) || track.StatisticsFrames is not { } frames)
            {
                continue;
            }

            var full = subtitles.Where(s => s.Key != key && s.Language?.Primary == tag.Primary && s.Track.StatisticsFrames is > 0)
                .Select(s => s.Track.StatisticsFrames!.Value)
                .DefaultIfEmpty(0)
                .Max();
            if (full > 0 && frames < full * ForcedShare)
            {
                group.ForcedSuggestions.Add(key);
            }
        }
    }

    /// <summary>What makes two tracks "the same kind": type, codec, channels, sampling rate and the name (normalised).</summary>
    private readonly record struct Fingerprint(TrackType Type, string? CodecId, int? Channels, int? Sampling, string Name)
    {
        public static Fingerprint Of(MediaTrack track) =>
            new(track.Type, track.CodecId, track.AudioChannels, track.AudioSamplingFrequency, Words.Normalize(track.Name));
    }
}
