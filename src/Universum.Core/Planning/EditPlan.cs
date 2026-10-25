using Universum.Core.Rules;

namespace Universum.Core.Planning;

/// <summary>What will be done with one file: the target state, the write method and the list of changes.</summary>
public sealed record EditPlan(TargetState Target, WriteMethod Method, IReadOnlyList<PlannedChange> Changes)
{
    public static EditPlan For(TargetState target)
    {
        var method = !target.HasChanges ? WriteMethod.None : target.OrderChanged ? WriteMethod.Remux : WriteMethod.HeaderEdit;
        return new EditPlan(target, method, ListChanges(target));
    }

    private static List<PlannedChange> ListChanges(TargetState target)
    {
        var changes = new List<PlannedChange>();
        var sourcePositions = target.Source.Tracks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        for (var position = 0; position < target.Tracks.Count; position++)
        {
            var track = target.Tracks[position];
            var source = track.Source;
            if (sourcePositions[track.TrackId] != position)
            {
                changes.Add(new PlannedChange(track.TrackId, ChangeKind.Position, (sourcePositions[track.TrackId] + 1).ToString(), (position + 1).ToString()));
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
        }

        foreach (var removed in target.Removed)
        {
            changes.Add(new PlannedChange(removed.Id, ChangeKind.Removed, (sourcePositions[removed.Id] + 1).ToString(), null));
        }

        if (target.TagsChanged)
        {
            var count = target.Source.GlobalTagEntries + target.Source.Tracks.Sum(t => t.TagEntries);
            changes.Add(new PlannedChange(null, ChangeKind.Tags, count.ToString(), "0"));
        }

        return changes;
    }

    private static void AddFlag(List<PlannedChange> changes, TrackTarget track, ChangeKind kind, bool before, bool after)
    {
        if (before != after)
        {
            changes.Add(new PlannedChange(track.TrackId, kind, before ? "1" : "0", after ? "1" : "0"));
        }
    }
}
