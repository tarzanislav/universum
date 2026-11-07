using Universum.Core.Mp4;
using Universum.Core.Text;

namespace Universum.Core.Muxing;

/// <summary>
/// Ponechať MP4 in Zlučovanie (owner, 7. 11. 2026: "by sa dal editovať rovno MP4 súbor v Multiplexingu a zachovať MP4
/// kontajner?"): one MP4 with all its tracks whose header only changes – the languages, names, default tracks, order and
/// title – stays MP4; anything MP4 cannot hold makes an MKV as always, and says why.
/// </summary>
public static class Mp4Mux
{
    /// <summary>
    /// The change of the MP4's header (null when the result cannot stay MP4) and why it cannot (null as well when the
    /// first source is no MP4).
    /// </summary>
    public static (Mp4Change? Change, string? Obstacle) Plan(MuxSpec spec)
    {
        if (spec.Sources.FirstOrDefault() is not { } source || !source.File.IsMp4)
        {
            return (null, null);
        }

        if (Obstacle(spec, source) is { } why)
        {
            return (null, why);
        }

        var changes = new List<Mp4TrackChange>();
        var order = new List<int>();
        foreach (var track in spec.Tracks)
        {
            var was = source.File.Tracks.Single(t => t.Id == track.TrackId);
            if (was.Number is not { } id)
            {
                return (null, CoreText.T("stopa MP4 bez čísla"));
            }

            order.Add(id);
            var change = new Mp4TrackChange(id)
            {
                Language = !string.Equals(was.Language.Primary, track.Language.Primary, StringComparison.OrdinalIgnoreCase) ? track.Language : null,
                Name = (track.Name ?? "") != (was.Name ?? "") ? track.Name ?? "" : null,
                Enabled = track.Flags.Default != was.Flags.Enabled ? track.Flags.Default : null,
            };
            if (change.Language is not null || change.Name is not null || change.Enabled is not null)
            {
                changes.Add(change);
            }
        }

        var original = source.File.Tracks.Select(t => t.Number!.Value).ToList();
        // the title of a source is never taken over (owner, 29. 10. 2026): the result has the one typed, or none
        return (new Mp4Change(changes, spec.Title ?? "", order.SequenceEqual(original) ? null : order), null);
    }

    // the first thing MP4 cannot hold, in words
    private static string? Obstacle(MuxSpec spec, MuxSource source)
    {
        var file = source.File;
        var tracks = spec.Tracks.Select(t => (Track: t, Was: t.Source == 0 ? file.Tracks.FirstOrDefault(s => s.Id == t.TrackId) : null)).ToList();
        return spec.Sources.Count > 1 || tracks.Any(t => t.Track.Source != 0) ? CoreText.T("zlučujú sa viaceré súbory")
            : source.AdditionalParts.Count > 0 || source.Appended.Count > 0 ? CoreText.T("pripájajú sa ďalšie súbory")
            : tracks.Any(t => t.Was is null) || tracks.Count != file.Tracks.Count || tracks.Select(t => t.Track.TrackId).Distinct().Count() != tracks.Count
                ? CoreText.T("odstraňujú sa stopy")
            : tracks.Any(t => !t.Track.Options.IsEmpty) ? CoreText.T("vlastnosti stopy (oneskorenie, FPS, pomer strán, orez, SRT…)")
            : tracks.Any(t => t.Track.Flags.Forced != t.Was!.Flags.Forced) ? CoreText.T("príznak vynútené (Forced)")
            : tracks.Any(t => (t.Track.Flags with { Default = false, Enabled = false, Forced = false }) != (t.Was!.Flags with { Default = false, Enabled = false, Forced = false }))
                ? CoreText.T("príznak pre nepočujúcich, komentár a podobné")
            : tracks.Any(t => t.Track.Flags.Default && !t.Track.Flags.Enabled) ? CoreText.T("predvolená stopa je vypnutá")
            : !string.IsNullOrEmpty(spec.ChaptersFile) || (!source.KeepChapters && file.ChapterEntries > 0) ? CoreText.T("kapitoly")
            : spec.Attachments.Count > 0 || (!source.KeepAttachments && file.AttachmentCount > 0) ? CoreText.T("prílohy")
            : null;
    }

    /// <summary>The extension of an MP4 that stays MP4: the source's own (.mp4, .m4v, .mov).</summary>
    public static string Extension(MuxSpec spec) => Path.GetExtension(spec.Sources[0].File.Path).ToLowerInvariant();
}
