using Universum.Core.Media;
using Universum.Core.Planning;
using Universum.Core.Rules;

namespace Universum.Tests;

public sealed class RulesTests
{
    [Test]
    public void SpecificationExampleWithResolvedLanguages()
    {
        var file = File(
            Video(0, "x264 by Group"),
            Audio(1, "eng", "English 2.0", isDefault: true),
            Audio(2, "und", "CZ Dabing", isDefault: false),
            Subs(3, "und", "Forced"),
            Subs(4, "cze", "Czech full"),
            Subs(5, "eng", null)) with { GlobalTagEntries = 1 };

        var target = UniversumRules.Apply(file, decisions: new TrackDecisions
        {
            Languages = new Dictionary<int, LanguageTag> { [2] = LanguageTag.FromCode("cs"), [3] = LanguageTag.FromCode("cs") },
        });

        Assert.True(!target.IsBlocked, "not blocked");
        Assert.Sequence([0, 2, 1, 3, 4, 5], target.Tracks.Select(t => t.TrackId), "order: video, CZ audio, EN audio, CZ forced, CZ, EN");
        var video = target.For(0);
        Assert.True(video.Name is { Length: 8 } && video.Name.All(char.IsAsciiLetterOrDigit), $"random video name '{video.Name}'");
        Assert.Equal(new TrackFlags(Default: true, Enabled: true, Forced: false), video.Flags with { HearingImpaired = false }, "video flags");
        AssertFlags(target.For(2), true, false, "CZ audio");
        AssertFlags(target.For(1), false, false, "EN audio");
        AssertFlags(target.For(3), true, true, "CZ forced subtitles: forced, default and enabled");
        AssertFlags(target.For(4), false, false, "CZ full subtitles: no default with Czech audio");
        AssertFlags(target.For(5), false, false, "EN subtitles");
        Assert.True(target.Tracks.Where(t => t.Type != TrackType.Video).All(t => t.Name is null), "audio and subtitle names cleared");
        Assert.Equal("cs", target.For(2).Language.Primary, "resolved language");
        Assert.True(target.TagsChanged, "tags removed");
        Assert.Equal(WriteMethod.Remux, EditPlan.For(target).Method, "order change needs a new file");
    }

    [Test]
    public void UndeterminedLanguageBlocks()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "und", null), Subs(2, "und", null)));
        Assert.True(target.IsBlocked, "blocked");
        Assert.Sequence([1, 2], target.Issues.Where(i => i.Kind == RuleIssueKind.UndeterminedLanguage).Select(i => i.TrackId!.Value), "tracks to resolve");
    }

    [Test]
    public void ForeignAudioMakesCzechSubtitlesDefault()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "eng", null), Subs(3, "cze", "Czech"), Subs(4, "cze", "Czech forced", forced: true)));
        Assert.Sequence([0, 1, 4, 3, 2], target.Tracks.Select(t => t.TrackId), "forced Czech before full Czech");
        AssertFlags(target.For(3), true, false, "full Czech subtitles are default");
        AssertFlags(target.For(4), false, true, "forced Czech subtitles stay forced but not default (R5)");
        AssertFlags(target.For(2), false, false, "English subtitles");
    }

    [Test]
    public void SlovakSubtitlesWhenThereAreNoCzech()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "eng", null), Subs(3, "slo", null)));
        AssertFlags(target.For(3), true, false, "Slovak subtitles are default (R6)");
        Assert.True(!target.Issues.Any(), "no issues");
    }

    [Test]
    public void NoPreferredSubtitlesIsReported()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "eng", null)));
        Assert.True(target.Issues.Single().Kind == RuleIssueKind.NoPreferredSubtitles && !target.IsBlocked, "information only");
        AssertFlags(target.For(2), false, false, "English subtitles");
    }

    [Test]
    public void LanguageOrderCzechSlovakOthers()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "slo", "Slovensky"), Audio(2, "cze", "Česky", isDefault: false), Audio(3, "eng", null), Audio(4, "ger", null),
            Subs(5, "slo", null), Subs(6, "eng", null), Subs(7, "cze", null)));
        Assert.Sequence([0, 2, 1, 3, 4, 7, 5, 6], target.Tracks.Select(t => t.TrackId), "order");
        Assert.Equal(1, target.Tracks.Count(t => t.Type == TrackType.Audio && t.Flags.Default), "exactly one default audio");
        AssertFlags(target.For(2), true, false, "Czech audio is default");
        Assert.True(target.Tracks.Where(t => t.Type == TrackType.Subtitles).All(t => !t.Flags.Default), "no default subtitles with Czech audio");
    }

    [Test]
    public void ForcedFromNameWithDiacritics()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "cze", null), Subs(2, "cze", "Vynútené titulky"), Subs(3, "slo", "NÚTENÉ")));
        AssertFlags(target.For(2), true, true, "Czech forced by name, default (matches the Czech audio)");
        AssertFlags(target.For(3), false, true, "Slovak forced by name, not default (R7)");
    }

    [Test]
    public void ForcedOnlyForPreferredLanguages()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "eng", "Forced", forced: true), Subs(3, "cze", null)));
        AssertFlags(target.For(2), false, false, "English forced subtitles lose the flag");
        AssertFlags(target.For(3), true, false, "Czech subtitles default");
    }

    [Test]
    public void ForcedMatchingTheDefaultAudio()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "slo", null), Subs(2, "cze", null, forced: true), Subs(3, "slo", null, forced: true)));
        AssertFlags(target.For(3), true, true, "forced Slovak matches the Slovak audio (R7)");
        AssertFlags(target.For(2), false, true, "forced Czech stays forced");
        Assert.Sequence([0, 1, 2, 3], target.Tracks.Select(t => t.TrackId), "Czech forced before Slovak forced");
    }

    [Test]
    public void CommentaryAndHearingImpaired()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "cze", "Director's Commentary"), Audio(2, "eng", null),
            Subs(3, "cze", "Czech SDH"), Subs(4, "cze", null), Subs(5, "cze", "komentár")));
        Assert.Sequence([0, 2, 1, 4, 3, 5], target.Tracks.Select(t => t.TrackId), "commentary last, SDH after full");
        AssertFlags(target.For(2), true, false, "English audio is default instead of the commentary");
        Assert.Equal(TrackRole.Commentary, target.For(1).Role);
        Assert.Equal(TrackRole.HearingImpaired, target.For(3).Role);
        AssertFlags(target.For(4), true, false, "full Czech subtitles default: the only Czech audio is commentary");
    }

    [Test]
    public void FilesNeedingReview()
    {
        Assert.True(UniversumRules.Apply(File(Video(0), Video(1), Audio(2, "cze", null))).IsBlocked, "two video tracks");
        Assert.True(UniversumRules.Apply(File(Video(0), Subs(1, "cze", null))).IsBlocked, "no audio");
    }

    [Test]
    public void CleanFileIsUnchanged()
    {
        var file = File(Video(0, "Ab12Cd34"), Audio(1, "cze", null), Subs(2, "cze", null, isDefault: false));
        var plan = EditPlan.For(UniversumRules.Apply(file));
        Assert.Equal(WriteMethod.None, plan.Method, "already clean");
        Assert.Equal(0, plan.Changes.Count, "changes");
    }

    [Test]
    public void SegmentTitleIsAlwaysRemoved()
    {
        // owner, 29. 10. 2026: the "File title" would stand for the file's name – always removed (a header edit is enough)
        var file = File(Video(0, "Ab12Cd34"), Audio(1, "cze", null), Subs(2, "cze", null, isDefault: false)) with { Title = "Epizoda 7" };
        var target = UniversumRules.Apply(file);
        var plan = EditPlan.For(target);
        Assert.True(target.TitleChanged && target.ResultTitle is null, "the title goes");
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method, "only the header");
        Assert.True(plan.Changes.Any(c => c.Kind == ChangeKind.Title), "listed as a change");
    }

    [Test]
    public void HeaderOnlyChanges()
    {
        var file = File(Video(0, "Ab12Cd34"), Audio(1, "cze", "Dabing"), Audio(2, "eng", null, isDefault: true));
        var plan = EditPlan.For(UniversumRules.Apply(file));
        Assert.Equal(WriteMethod.HeaderEdit, plan.Method);
        Assert.Sequence(
            [(1, ChangeKind.Name), (2, ChangeKind.Default)],
            plan.Changes.Select(c => (c.TrackId!.Value, c.Kind)), "changes");
    }

    [Test]
    public void ProfileWithOtherLanguages()
    {
        var profile = RuleProfile.Default with { PreferredLanguages = ["hu"] };
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Audio(2, "hun", null), Subs(3, "cze", null)), profile);
        Assert.Sequence([0, 2, 1, 3], target.Tracks.Select(t => t.TrackId), "Hungarian first");
        AssertFlags(target.For(3), false, false, "Czech subtitles are not preferred in this profile");
    }

    private static void AssertFlags(TrackTarget track, bool isDefault, bool forced, string what)
    {
        Assert.Equal(isDefault, track.Flags.Default, what + " – default");
        Assert.True(track.Flags.Enabled, what + " – enabled");
        Assert.Equal(forced, track.Flags.Forced, what + " – forced");
    }

    private static MediaFile File(params MediaTrack[] tracks) => new() { Path = "test.mkv", ContainerType = "Matroska", Tracks = tracks };

    private static MediaTrack Video(int id, string? name = null) =>
        new() { Id = id, Number = id + 1, Type = TrackType.Video, CodecId = "V_MPEG4/ISO/AVC", Name = name, Language = LanguageTag.Undetermined };

    private static MediaTrack Audio(int id, string language, string? name, bool isDefault = true) =>
        new() { Id = id, Number = id + 1, Type = TrackType.Audio, CodecId = "A_AC3", Name = name, Language = new LanguageTag(language, null), Flags = new TrackFlags(Default: isDefault) };

    private static MediaTrack Subs(int id, string language, string? name, bool forced = false, bool isDefault = true) =>
        new() { Id = id, Number = id + 1, Type = TrackType.Subtitles, CodecId = "S_TEXT/UTF8", Name = name, Language = new LanguageTag(language, null), Flags = new TrackFlags(Default: isDefault, Forced: forced) };
}
