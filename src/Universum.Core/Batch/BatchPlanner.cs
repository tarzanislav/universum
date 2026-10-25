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

    public static IReadOnlyList<BatchItem> Plan(BatchAnalysis analysis, BatchTemplate template, RuleProfile? profile = null)
    {
        var effective = template.Apply(profile ?? RuleProfile.Default);
        return analysis.Groups.SelectMany(g => g.Files.Select(f => PlanFile(f, g, template, effective))).ToList();
    }

    public static BatchItem PlanFile(MediaFile file, BatchGroup group, BatchTemplate template, RuleProfile profile)
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

        return new BatchItem(file, group, decisions, target, EditPlan.For(target), match, evidence, reasons);
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
