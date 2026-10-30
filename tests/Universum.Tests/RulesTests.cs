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
        Assert.True(target.Tracks.Where(t => t.Type != TrackType.Video && t.TrackId != 3).All(t => t.Name is null), "audio and subtitle names cleared");
        Assert.Equal("Forced", target.For(3).Name, "forced subtitles are named Forced (owner, 29. 10. 2026)");
        Assert.Equal("Forced", UniversumRules.Apply(file with { Tracks = file.Tracks.Select(t => t.Id == 3 ? t with { Name = "Forced", Flags = t.Flags with { Forced = true } } : t).ToList() },
            decisions: new TrackDecisions { Languages = new Dictionary<int, LanguageTag> { [2] = LanguageTag.FromCode("cs"), [3] = LanguageTag.FromCode("cs") } }).For(3).Name,
            "and stay so on the next pass");
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
        AssertFlags(target.For(3), false, false, "owner, 30. 10. 2026: beside Czech forced the Slovak ones are not active");
        Assert.True(target.For(2).Name == "Forced" && target.For(3).Name == "Forced", "both named Forced");
    }

    [Test]
    public void ForcedOnlyForPreferredLanguages()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "eng", "Forced", forced: true), Subs(3, "cze", null)));
        AssertFlags(target.For(2), false, false, "English forced subtitles lose the flag");
        Assert.Equal("Forced", target.For(2).Name, "owner, 30. 10. 2026: but are named Forced");
        AssertFlags(target.For(3), true, false, "Czech subtitles default");
    }

    [Test]
    public void CzechForcedWinOverSlovak()
    {
        // owner, 30. 10. 2026: with Czech and Slovak forced subtitles only the Czech ones are active – also with Slovak audio
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "slo", null), Subs(2, "cze", null, forced: true), Subs(3, "slo", null, forced: true)));
        Assert.Equal(new TrackFlags(Default: true, Enabled: true, Forced: true), target.For(2).Flags, "Czech forced: default, enabled, forced, nothing else");
        AssertFlags(target.For(3), false, false, "Slovak forced: not default, not forced");
        Assert.Sequence([0, 1, 2, 3], target.Tracks.Select(t => t.TrackId), "Czech forced before Slovak forced");
    }

    [Test]
    public void SlovakForcedWhenThereAreNoCzech()
    {
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "cze", null), Subs(2, "slo", "forced"), Subs(3, "eng", "Forced")));
        AssertFlags(target.For(2), true, true, "only Slovak forced: active");
        AssertFlags(target.For(3), false, false, "English forced: named, not active");
        Assert.True(target.For(2).Name == "Forced" && target.For(3).Name == "Forced", "both named Forced");
    }

    [Test]
    public void ForeignAudioShowsFullSubtitlesForcedStayForced()
    {
        // owner, 30. 10. 2026: foreign audio – the full Czech subtitles are the default ones, the Czech forced keep Forced
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "cze", "Forced"), Subs(3, "cze", null), Subs(4, "slo", "forced")));
        AssertFlags(target.For(3), true, false, "full Czech: default");
        AssertFlags(target.For(2), false, true, "Czech forced: forced, not default");
        AssertFlags(target.For(4), false, false, "Slovak forced: not active");

        // no full Czech or Slovak subtitles: the forced ones are shown
        var only = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), Subs(2, "cze", "Forced"), Subs(3, "eng", null)));
        AssertFlags(only.For(2), true, true, "only Czech forced: default and forced");
        Assert.True(!only.Issues.Any(i => i.Kind == RuleIssueKind.NoPreferredSubtitles), "nothing to report");
    }

    [Test]
    public void TrackNamesWrittenByTheUser()
    {
        // owner, 30. 10. 2026: Track name – a name written for audio or subtitles wins over the rules' (also over Forced)
        var file = File(Video(0), Audio(1, "cze", "AC3 5.1"), Subs(2, "cze", "Forced"), Subs(3, "eng", null));
        var rules = UniversumRules.Apply(file);
        Assert.True(rules.For(1).Name is null && rules.For(2).Name == "Forced", "the rules' names");
        var named = UniversumRules.Apply(file, decisions: new TrackDecisions
        {
            Names = new Dictionary<int, string> { [0] = "nie pre video", [1] = "Český dabing", [2] = "", [3] = "English SDH" },
        });
        Assert.True(named.For(1).Name == "Český dabing" && named.For(2).Name is null && named.For(2).Role == TrackRole.Forced && named.For(3).Name == "English SDH",
            "written names win; empty = no name; still forced");
        Assert.True(named.For(0).Name != "nie pre video", "the video is named by the rules (not a track name of this kind)");
    }

    [Test]
    public void ForcedByTheAmountOfSubtitles()
    {
        // owner, 30. 10. 2026: without the word Forced, the small number of subtitles (and bytes) tells forced subtitles
        var full = Subs(3, "cze", null) with { StatisticsFrames = 1240, StatisticsBytes = 52_000 };
        var few = Subs(2, "cze", null) with { StatisticsFrames = 85, StatisticsBytes = 3_100 };
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "cze", null), few, full));
        Assert.True(target.For(2).Role == TrackRole.Forced && target.For(2).Name == "Forced", "few subtitles: forced");
        AssertFlags(target.For(2), true, true, "active Czech forced");
        Assert.True(ForcedSubtitles.ByAmount(File(Video(0), few, full), few)!.Contains("85", StringComparison.Ordinal), "the reason says the numbers");
        Assert.True(target.For(3).Role == TrackRole.Main, "the full ones are not");

        // the user's choice wins
        var chosen = UniversumRules.Apply(File(Video(0), Audio(1, "cze", null), few, full), decisions: new TrackDecisions { Forced = new Dictionary<int, bool> { [2] = false } });
        Assert.True(chosen.For(2).Role == TrackRole.Main && chosen.For(2).Name is null, "not forced when the user says so");

        // the only track: compared with the length of the film
        var film = File(Video(0), Audio(1, "eng", null), few) with { Duration = TimeSpan.FromMinutes(110) };
        Assert.True(ForcedSubtitles.ByAmount(film, few) is not null, "85 subtitles in 110 minutes: forced");
        var normal = few with { StatisticsFrames = 900 };
        Assert.True(ForcedSubtitles.ByAmount(film with { Tracks = [Video(0), normal] }, normal) is null, "900 in 110 minutes: full");
        Assert.True(ForcedSubtitles.ByAmount(File(Video(0), few), few) is null, "without the length or another track: unknown");
    }

    [Test]
    public void ImageSubtitlesAreNotComparedWithText()
    {
        // review: PGS counts two frames for a subtitle and megabytes of pictures – full Czech SRT beside English SDH in
        // PGS stays full; image tracks are compared with image tracks
        var czech = Subs(2, "cze", null) with { StatisticsFrames = 1_000, StatisticsBytes = 45_000, IsTextSubtitles = true };
        var pgs = Subs(3, "eng", "SDH") with { CodecId = "S_HDMV/PGS", StatisticsFrames = 3_600, StatisticsBytes = 40_000_000 };
        var film = File(Video(0), Audio(1, "cze", null), czech, pgs) with { Duration = TimeSpan.FromMinutes(110) };
        Assert.True(ForcedSubtitles.ByAmount(film, czech) is null, "full Czech SRT is not forced beside PGS");
        Assert.True(UniversumRules.Apply(film).For(2).Role == TrackRole.Main, "and stays the main Czech subtitles");

        var forcedPgs = Subs(4, "cze", null) with { CodecId = "S_HDMV/PGS", StatisticsFrames = 120, StatisticsBytes = 1_500_000 };
        Assert.True(ForcedSubtitles.ByAmount(film with { Tracks = [.. film.Tracks, forcedPgs] }, forcedPgs)?.Contains("obrázkovej", StringComparison.Ordinal) == true,
            "few PGS frames against the full PGS track: forced");
        var forcedText = Subs(5, "cze", null) with { StatisticsFrames = 40, IsTextSubtitles = true };
        var alone = File(Video(0), forcedText, pgs) with { Duration = TimeSpan.FromMinutes(110) };
        Assert.True(ForcedSubtitles.ByAmount(alone, forcedText)?.Contains("minút", StringComparison.Ordinal) == true,
            "the only text track is compared with the length of the film");
    }

    [Test]
    public void EmptySubtitlesAreRemoved()
    {
        // owner, 30. 10. 2026: a subtitle track without a single subtitle is removed from the container
        var empty = Subs(3, "cze", null) with { StatisticsFrames = 0, StatisticsBytes = 0 };
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "cze", null), Subs(2, "cze", null) with { StatisticsFrames = 900 }, empty));
        Assert.Sequence([3], target.RemovedEmpty.Select(t => t.Id), "the empty one");
        Assert.True(target.Removed.Any(t => t.Id == 3) && target.Tracks.All(t => t.TrackId != 3), "left out of the result");
        Assert.True(UniversumRules.Apply(File(Video(0), Audio(1, "cze", null), Subs(2, "cze", null))).RemovedEmpty.Count == 0, "without statistics nothing is removed");
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
        Assert.True(target.For(3).Flags.HearingImpaired, "owner, 30. 10. 2026: the flag of subtitles for the hearing impaired is set");
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

    [Test]
    public void SubtitlesKeptByLanguageListAndAudio()
    {
        // owner, 29. 10. 2026: CZ, SK, EN, PL, HU, RU in this order, then the audio's language; the rest is left out
        var file = File(Video(0), Audio(1, "kor", null), Subs(2, "rus", null), Subs(3, "ger", null), Subs(4, "kor", null), Subs(5, "eng", null),
            Subs(6, "cze", null), Subs(7, "fre", null), Subs(8, "slo", null), Subs(9, "und", null));
        var target = UniversumRules.Apply(file, decisions: new TrackDecisions { Languages = new Dictionary<int, LanguageTag> { [9] = LanguageTag.FromCode("pl") } });
        Assert.Sequence([0, 1, 6, 8, 5, 9, 2, 4], target.Tracks.Select(t => t.TrackId), "CZ, SK, EN, PL, RU, then Korean like the audio");
        Assert.Sequence([3, 7], target.RemovedByLanguage.Select(t => t.Id), "German and French are left out");
        Assert.Sequence([3, 7], target.Removed.Select(t => t.Id), "and removed from the result");
        Assert.Equal(WriteMethod.Remux, EditPlan.For(target).Method, "removing tracks needs a new file");

        var kept = UniversumRules.Apply(file, decisions: new TrackDecisions { Languages = new Dictionary<int, LanguageTag> { [9] = LanguageTag.FromCode("pl") }, Kept = new HashSet<int> { 3 } });
        Assert.Sequence([7], kept.Removed.Select(t => t.Id), "a track the user keeps stays");

        var undetermined = UniversumRules.Apply(file);
        Assert.True(undetermined.Tracks.Any(t => t.TrackId == 9) && undetermined.IsBlocked, "an undetermined subtitle waits for its language, it is not removed");

        var all = UniversumRules.Apply(file with { Tracks = file.Tracks.Where(t => t.Id != 9).ToList() }, RuleProfile.Default with { SubtitleLanguages = [] });
        Assert.Equal(0, all.Removed.Count, "without subtitle languages every subtitle stays");
    }

    [Test]
    public void ShownSubtitlesHaveNoOtherFlags()
    {
        // owner, 29. 10. 2026: with foreign audio the Czech subtitles are default and enabled, every other flag is No
        var czech = Subs(2, "cze", null) with { Flags = new TrackFlags(Default: false, Enabled: false, Original: true, VisualImpaired: true) };
        var target = UniversumRules.Apply(File(Video(0), Audio(1, "eng", null), czech, Subs(3, "eng", null)));
        Assert.Equal(new TrackFlags(Default: true, Enabled: true), target.For(2).Flags, "default, enabled, nothing else");
        Assert.True(target.For(2).OtherFlagsChanged, "the other flags change");
        var plan = EditPlan.For(target);
        Assert.True(plan.Changes.Any(c => c.TrackId == 2 && c.Kind == ChangeKind.OtherFlags && c.Before == "visual impaired, original" && c.After == ""), "listed as a change");
        var arguments = Universum.Core.MkvToolNix.MkvToolNixArguments.HeaderEdit(target, "test.mkv");
        Assert.True(arguments.Contains("flag-original=0") && arguments.Contains("flag-visual-impaired=0") && !arguments.Contains("flag-commentary=0"),
            "the header edit clears the flags set: " + string.Join(" ", arguments));
        var remux = Universum.Core.MkvToolNix.MkvToolNixArguments.Remux(target, "test.mkv", "out.mkv");
        Assert.True(remux.Contains("2:0") && remux.Contains("--original-flag") && remux.Count(a => a == "--original-flag") == 1,
            "a new file clears them only at that track");
    }

    [Test]
    public void TheUsersOrderWins()
    {
        // owner, 29. 10. 2026: tracks dragged up or down keep that order; tracks not named follow as the rules have them
        var file = File(Video(0), Audio(1, "eng", null), Audio(2, "cze", null), Subs(3, "eng", null), Subs(4, "cze", null), Subs(5, "slo", null));
        Assert.Sequence([0, 2, 1, 4, 5, 3], UniversumRules.Apply(file).Tracks.Select(t => t.TrackId), "rules");
        var target = UniversumRules.Apply(file, decisions: new TrackDecisions { Order = [1, 3, 5] });
        Assert.Sequence([0, 1, 2, 3, 5, 4], target.Tracks.Select(t => t.TrackId), "the user's order, the rest after it");
        AssertFlags(target.For(1), true, false, "the first audio is the default one");
        AssertFlags(target.For(1 + 1), false, false, "Czech audio second");
        AssertFlags(target.For(4), false, false, "Czech audio is there: no full subtitles by default, as before");
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
