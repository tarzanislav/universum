using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>Header editor: targets put together by hand, the Check panel, the segment title, editing in place.</summary>
public sealed class HeaderEditorTests
{
    [Test]
    public void ManualTargetKeepsWhatTheUserChose()
    {
        var file = Sample();
        var edits = ManualTarget.AsIs(file).ToList();
        Assert.True(!ManualTarget.Create(file, edits).HasChanges, "as is: nothing changes");

        edits[1] = edits[1] with { Name = "Anglicky", Language = LanguageTag.FromCode("en") };
        edits[2] = edits[2] with { Language = LanguageTag.FromCode("cs"), Flags = edits[2].Flags with { Default = true } };
        (edits[1], edits[2]) = (edits[2], edits[1]);
        var target = ManualTarget.Create(file, edits, removed: new HashSet<int>(), title: "Nový názov");

        Assert.True(target.OrderChanged && target.NeedsRewrite, "audio swapped: a new file");
        Assert.True(!target.IsBlocked, "manual targets are never blocked");
        Assert.True(target.TitleChanged && target.ResultTitle == "Nový názov", "title");
        var plan = EditPlan.For(target);
        Assert.True(plan.Changes.Any(c => c.Kind == ChangeKind.Title && c.After == "Nový názov"), "title in the list of changes");
        Assert.True(MkvToolNixArguments.Remux(target, "in.mkv", "out.mkv").SequenceEqual(
            MkvToolNixArguments.Remux(target, "in.mkv", "out.mkv")) &&
            MkvToolNixArguments.Remux(target, "in.mkv", "out.mkv").Contains("--title"), "remux sets the title");

        var removedTitle = ManualTarget.Create(file, ManualTarget.AsIs(file), title: "");
        Assert.True(removedTitle.TitleChanged && removedTitle.ResultTitle is null, "empty title removes it");
        Assert.Sequence(["test.mkv", "--edit", "info", "--delete", "title"], MkvToolNixArguments.HeaderEdit(removedTitle, "test.mkv"), "header edit deletes the title");
        Assert.True(!ManualTarget.Create(file, ManualTarget.AsIs(file), title: "Old").TitleChanged, "same title: no change");
    }

    [Test]
    public void ManualTargetRefusesIncompleteOrders()
    {
        var file = Sample();
        var edits = ManualTarget.AsIs(file);
        Assert.Throws<ArgumentException>(() => ManualTarget.Create(file, edits.Take(3).ToList()), "a track missing");
        Assert.Throws<ArgumentException>(() => ManualTarget.Create(file, [.. edits, edits[0]]), "a track twice");
        Assert.Throws<ArgumentException>(() => ManualTarget.Create(file, edits.Skip(1).ToList(), removed: new HashSet<int> { 0 }), "video cannot be removed");

        var removed = ManualTarget.Create(file, edits.Where(e => e.TrackId != 4).ToList(), removed: new HashSet<int> { 4 });
        Assert.True(removed.Removed.Single().Id == 4 && removed.HasChanges, "subtitles left out");
        Assert.Equal(WriteMethod.Remux, EditPlan.For(removed).Method, "leaving a track out writes a new file");
    }

    [Test]
    public void CheckPanelPointsOutProblems()
    {
        var file = Sample() with { GlobalTagEntries = 2 };
        var findings = TargetChecks.Review(ManualTarget.Create(file, ManualTarget.AsIs(file)));
        Assert.True(findings.Any(f => f.Kind == CheckKind.UndeterminedLanguage && f.TrackId == 2), "und audio");
        Assert.True(findings.Any(f => f.Kind == CheckKind.SeveralDefaults && f.Type == TrackType.Audio), "two default audio tracks");
        Assert.True(findings.Any(f => f.Kind == CheckKind.TagsRemain), "tags");
        Assert.True(!findings.Any(f => f.Kind == CheckKind.OrderNotPreferred), "und and English: no preferred language, any order");
        var czech = ManualTarget.AsIs(file).Select(e => e.TrackId == 2 ? e with { Language = LanguageTag.FromCode("cs") } : e).ToList();
        Assert.True(TargetChecks.Review(ManualTarget.Create(file, czech)).Any(f => f.Kind == CheckKind.OrderNotPreferred && f.Type == TrackType.Audio), "English before Czech");

        var forced = ManualTarget.AsIs(file).Select(e => e.TrackId == 3 ? e with { Flags = new TrackFlags(Default: false, Forced: true) } : e.TrackId == 4 ? e with { Flags = new TrackFlags(Default: false) } : e).ToList();
        Assert.True(TargetChecks.Review(ManualTarget.Create(file, forced)).Any(f => f.Kind == CheckKind.ForcedWithoutDefault && f.TrackId == 3), "forced subtitles nobody sees");

        var mute = ManualTarget.AsIs(file).Select(e => file.Tracks.First(t => t.Id == e.TrackId).Type == TrackType.Audio ? e with { Flags = e.Flags with { Enabled = false } } : e).ToList();
        Assert.True(TargetChecks.Review(ManualTarget.Create(file, mute)).Any(f => f.Kind == CheckKind.NoneEnabled && f.Type == TrackType.Audio), "no enabled audio");

        // the rules themselves give nothing to complain about once the languages are known
        var rules = UniversumRules.Apply(file, decisions: new TrackDecisions { Languages = new Dictionary<int, LanguageTag> { [2] = LanguageTag.FromCode("cs") } });
        var afterRules = TargetChecks.Review(ManualTarget.Create(file, ManualTarget.From(rules), removeTags: rules.RemoveTags));
        Assert.Equal(0, afterRules.Count, string.Join(", ", afterRules));
    }

    [Test]
    public void InPlaceKeepsTheName()
    {
        var path = Path.Combine(Path.GetTempPath(), "Film.Name.2026.mkv");
        var placement = Placement.InPlace(path);
        Assert.Equal("Film.Name.2026.mkv", placement.FileName(5_000_000_000));
        Assert.Equal("Film.Name.2026 (2).mkv", placement.FileName(5_000_000_000, 2));
        Assert.True(placement.Holds(path, 123) && !placement.Holds(Path.Combine(Path.GetTempPath(), "Other.mkv"), 123), "holds only its own file");
    }

    private static MediaFile Sample() => new()
    {
        Path = "test.mkv",
        ContainerType = "Matroska",
        Title = "Old",
        Tracks =
        [
            new MediaTrack { Id = 0, Number = 1, Type = TrackType.Video, CodecId = "V_MPEG4/ISO/AVC" },
            new MediaTrack { Id = 1, Number = 2, Type = TrackType.Audio, CodecId = "A_AC3", Language = LanguageTag.FromCode("en"), Name = "English" },
            new MediaTrack { Id = 2, Number = 3, Type = TrackType.Audio, CodecId = "A_AC3", Language = LanguageTag.Undetermined, Name = "CZ dabing" },
            new MediaTrack { Id = 3, Number = 4, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Language = LanguageTag.FromCode("cs"), Flags = new TrackFlags(Default: false) },
            new MediaTrack { Id = 4, Number = 5, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Language = LanguageTag.FromCode("en"), Flags = new TrackFlags(Default: false) },
        ],
    };
}
