using Universum.Core.Rules;

namespace Universum.Core.Planning;

/// <summary>What will be done with one file: the target state, the write method and the list of changes.</summary>
public sealed record EditPlan(TargetState Target, WriteMethod Method, IReadOnlyList<PlannedChange> Changes)
{
    public static EditPlan For(TargetState target)
    {
        var method = !target.HasChanges ? WriteMethod.None : target.NeedsRewrite ? WriteMethod.Remux : WriteMethod.HeaderEdit;
        return new EditPlan(target, method, ListChanges(target));
    }

    private static List<PlannedChange> ListChanges(TargetState target)
    {
        var changes = new List<PlannedChange>();
        if (target.ContainerChanged)
        {
            changes.Add(new PlannedChange(null, ChangeKind.Container, target.Source.ContainerName, "MKV"));
        }

        var sourcePositions = target.Source.Tracks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        for (var position = 0; position < target.Tracks.Count; position++)
        {
            var track = target.Tracks[position];
            var source = track.Source;
            if (track.IsAdded)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.Added, null, Path.GetFileName(source.ExternalFile)));
            }
            else if (sourcePositions[track.TrackId] != position)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.Position, (sourcePositions[track.TrackId] + 1).ToString(), (position + 1).ToString()));
            }

            if (track.TimingChanged)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.Timing, null, track.Timing!.Describe()));
            }

            if (track.NameChanged)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.Name, source.Name, track.Name));
            }

            if (track.LanguageChanged)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.Language, source.Language.ToString(), track.Language.ToString()));
            }

            AddFlag(changes, track, ChangeKind.Default, source.Flags.Default, track.Flags.Default);
            AddFlag(changes, track, ChangeKind.Enabled, source.Flags.Enabled, track.Flags.Enabled);
            AddFlag(changes, track, ChangeKind.Forced, source.Flags.Forced, track.Flags.Forced);
            if (track.OtherFlagsChanged)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.OtherFlags, OtherFlags(source.Flags), OtherFlags(track.Flags)));
            }
        }

        foreach (var removed in target.RemovedFromFile)
        {
            changes.Add(new PlannedChange(removed.Id, ChangeKind.Removed, (sourcePositions[removed.Id] + 1).ToString(), null));
        }

        if (target.RepairIndex)
        {
            changes.Add(new PlannedChange(null, ChangeKind.Index, "0", null));
        }

        if (target.ChaptersChanged)
        {
            changes.Add(new PlannedChange(null, ChangeKind.Chapters, target.Source.ChapterEntries.ToString(), target.NewChapters!.Count.ToString()));
        }

        if (target.TitleChanged)
        {
            changes.Add(new PlannedChange(null, ChangeKind.Title, target.Source.Title, target.ResultTitle));
        }

        if (target.TagsChanged)
        {
            var count = target.Source.GlobalTagEntries + target.Source.Tracks.Sum(t => t.TagEntries);
            changes.Add(new PlannedChange(null, ChangeKind.Tags, count.ToString(), "0"));
        }

        return changes;
    }

    // "hearing impaired, original" – the flags beyond default, enabled and forced that are set
    private static string OtherFlags(Media.TrackFlags flags) => string.Join(", ", new[]
    {
        flags.HearingImpaired ? "hearing impaired" : null, flags.VisualImpaired ? "visual impaired" : null,
        flags.TextDescriptions ? "text descriptions" : null, flags.Original ? "original" : null, flags.Commentary ? "commentary" : null,
    }.OfType<string>());

    private static void AddFlag(List<PlannedChange> changes, TrackTarget track, ChangeKind kind, bool before, bool after)
    {
        if (before != after)
        {
            changes.Add(new PlannedChange(track.TrackId, kind, before ? "1" : "0", after ? "1" : "0"));
        }
    }
}
