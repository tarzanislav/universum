using System.Globalization;

namespace Universum.Core.Subtitles.Sync;

/// <summary>
/// Activity of one part of the film, a hundred values a second: +1 where someone speaks (or, for reference subtitles, a
/// subtitle is shown), −1 elsewhere.
/// </summary>
/// <param name="Start">Where the part starts in the film.</param>
public sealed record ActivityWindow(TimeSpan Start, sbyte[] Values)
{
    /// <summary>Values a second.</summary>
    public const int Rate = 100;

    public TimeSpan Length => TimeSpan.FromSeconds((double)Values.Length / Rate);

    /// <summary>The activity of subtitles shown in the part (reference subtitles that fit the film).</summary>
    public static ActivityWindow FromCues(IEnumerable<(TimeSpan Start, TimeSpan End)> cues, TimeSpan start, TimeSpan length)
    {
        var values = new sbyte[(int)(length.TotalSeconds * Rate)];
        Array.Fill(values, (sbyte)-1);
        foreach (var (from, to) in cues)
        {
            var first = Math.Max(0, (int)((from - start).TotalSeconds * Rate));
            var last = Math.Min(values.Length, (int)((to - start).TotalSeconds * Rate));
            for (var i = first; i < last; i++)
            {
                values[i] = 1;
            }
        }

        return new ActivityWindow(start, values);
    }
}

/// <summary>Where one part of the film says the subtitles are: they come <see cref="Lag"/> late (negative = early).</summary>
/// <param name="Coverage">How much of the subtitles' time has speech at the best lag (0–1).</param>
/// <param name="Margin">How much better the best lag is than any other one more than a second away.</param>
public sealed record SyncWindow(TimeSpan Center, TimeSpan Lag, double Coverage, double Margin)
{
    public bool IsClear => Margin >= SubtitleSync.MinimumMargin && Coverage >= SubtitleSync.MinimumCoverage;
}

/// <summary>The result of automatic sync: the change of the times, how sure it is, and why.</summary>
/// <param name="Timing">The change that makes the subtitles fit (SubtitleTiming.None = they fit).</param>
/// <param name="Reliable">Enough parts of the film agree.</param>
/// <param name="Detail">In Slovak: the method, the frame rate found, the parts that agree.</param>
public sealed record SyncResult(SubtitleTiming Timing, bool Reliable, string Detail, IReadOnlyList<SyncWindow> Windows)
{
    /// <summary>The lag the subtitles have now at a time of the film (what <see cref="Timing"/> removes).</summary>
    public TimeSpan LagAt(TimeSpan time) => time - Timing.Apply(time);
}

/// <summary>
/// Automatic sync of subtitles (owner, 30. 10. 2026): the subtitles are compared with what happens in the film – where
/// people speak (the audio track) or where other subtitles that fit the film are shown – in several parts of the film.
/// Each part says how late the subtitles are; the frame rates of films (25 ↔ 23,976 …) are tried first, then the lags of
/// the parts give the shift and a small change of speed. A part counts only when its best lag is clearly better than
/// any other; the result is reliable when most parts agree.
/// </summary>
public static class SubtitleSync
{
    /// <summary>How far the subtitles are looked for around each part (late or early).</summary>
    public static readonly TimeSpan SearchRange = TimeSpan.FromSeconds(90);

    /// <summary>A part counts when its best lag is this much better than any other lag more than a second away.</summary>
    public const double MinimumMargin = 0.04;

    /// <summary>…and when at least this share of the subtitles' time has speech at it.</summary>
    public const double MinimumCoverage = 0.45;

    // parts that agree: their lags differ from the line by at most this
    private static readonly TimeSpan Agreement = TimeSpan.FromMilliseconds(350);

    /// <summary>The ratios of frame rates tried (subtitles made for one video shown with another), 1 first.</summary>
    public static IReadOnlyList<double> Scales { get; } = FrameRate.All.SelectMany(from => FrameRate.All.Select(to => from.Value / to.Value))
        .Where(r => r is > 0.9 and < 1.1).Aggregate(new List<double> { 1.0 }, (list, r) =>
        {
            if (list.All(known => Math.Abs(known - r) > 0.0005))
            {
                list.Add(r);
            }

            return list;
        });

    /// <summary>
    /// The change that makes the subtitles fit the activity of the film; null when the parts say nothing (no speech, no
    /// subtitles in them).
    /// </summary>
    /// <param name="cues">The subtitles to fit (their times now).</param>
    /// <param name="windows">Activity of parts of the film (speech of the audio, or reference subtitles).</param>
    /// <param name="method">"podľa zvuku" / "podľa titulkov" – for the detail.</param>
    /// <param name="tryScales">Try the frame rates (false: only a shift – checking subtitles of the video).</param>
    public static SyncResult? Align(IReadOnlyList<(TimeSpan Start, TimeSpan End)> cues, IReadOnlyList<ActivityWindow> windows, string method, bool tryScales = true)
    {
        if (cues.Count == 0 || windows.Count == 0)
        {
            return null;
        }

        (double Scale, List<SyncWindow> Parts, int Agreeing, double Score)? best = null;
        foreach (var scale in tryScales ? Scales : [1.0])
        {
            var parts = windows.Select(w => Measure(cues, w, scale)).OfType<SyncWindow>().ToList();
            var clear = parts.Where(p => p.IsClear).ToList();
            if (clear.Count == 0)
            {
                continue;
            }

            var median = Median(clear.Select(p => p.Lag.TotalSeconds));
            var agreeing = clear.Count(p => Math.Abs(p.Lag.TotalSeconds - median) <= 2 * Agreement.TotalSeconds);
            var score = clear.Sum(p => p.Coverage + p.Margin);
            if (best is null || agreeing > best.Value.Agreeing || (agreeing == best.Value.Agreeing && score > best.Value.Score))
            {
                best = (scale, parts, agreeing, score);
            }
        }

        if (best is not { } found)
        {
            return new SyncResult(SubtitleTiming.None, false, $"{method}: v skúmaných častiach filmu sa nedalo nič zistiť (bez reči alebo bez titulkov).", []);
        }

        // the line through the lags of the clear parts that agree: shift and the rest of the drift
        var good = found.Parts.Where(p => p.IsClear).ToList();
        var center = Median(good.Select(p => p.Lag.TotalSeconds));
        good = good.Where(p => Math.Abs(p.Lag.TotalSeconds - center) <= 2 * Agreement.TotalSeconds).ToList();
        var (offset, slope) = Line(good.Select(p => (p.Center.TotalSeconds, p.Lag.TotalSeconds)).ToList());
        good = good.Where(p => Math.Abs(p.Lag.TotalSeconds - (offset + (slope * p.Center.TotalSeconds))) <= Agreement.TotalSeconds).ToList();
        (offset, slope) = Line(good.Select(p => (p.Center.TotalSeconds, p.Lag.TotalSeconds)).ToList());

        // a lag d(u) = a + b·u at the scaled time u = k·t: the film's time is u − d = k(1 − b)·t − a
        var factor = found.Scale * (1 - slope);
        var timing = Math.Abs(factor - 1) < 0.00002 && Math.Abs(offset) < 0.04
            ? SubtitleTiming.None
            : SubtitleTiming.FromFactor(factor, (long)Math.Round(-offset * 1000));
        var clearCount = found.Parts.Count(p => p.IsClear);
        var reliable = good.Count >= Math.Max(2, (windows.Count + 1) / 2) || (windows.Count <= 2 && good.Count == windows.Count);
        var sk = CultureInfo.GetCultureInfo("sk-SK");
        var rate = Math.Abs(found.Scale - 1) > 0.0005 ? $"FPS sa líšia (pomer {found.Scale.ToString("0.#####", sk)}), " : "";
        var detail = $"{method}: {rate}" + (timing.IsNone ? "titulky sedia" : timing.Describe()) +
                     $" – zhoduje sa {good.Count} z {windows.Count} častí filmu" + (clearCount > good.Count ? $" ({clearCount - good.Count} iné)" : "") +
                     (reliable ? "." : " – NEISTÉ, overte prehraním.");
        return new SyncResult(timing, reliable, detail, found.Parts);
    }

    /// <summary>
    /// Both together (owner: audio and other subtitles): when they agree the result is sure; otherwise the more reliable
    /// one, with a warning.
    /// </summary>
    public static SyncResult? Combine(SyncResult? byAudio, SyncResult? bySubtitles, TimeSpan duration)
    {
        if (byAudio is null || !byAudio.Reliable)
        {
            return bySubtitles is { Reliable: true } || byAudio is null ? bySubtitles : byAudio;
        }

        if (bySubtitles is null || !bySubtitles.Reliable)
        {
            return byAudio;
        }

        // the two changes compared at the start, the middle and the end of the film
        var checks = new[] { TimeSpan.FromMinutes(1), duration / 2, duration };
        var apart = checks.Max(t => Math.Abs((byAudio.Timing.Apply(t) - bySubtitles.Timing.Apply(t)).TotalMilliseconds));
        return apart <= 300
            ? byAudio with { Detail = byAudio.Detail + " Titulky v inom jazyku to potvrdzujú – isté." }
            : byAudio with { Reliable = false, Detail = byAudio.Detail + $" POZOR: podľa iných titulkov to vychádza inak (rozdiel {apart / 1000:0.0} s) – overte prehraním." };
    }

    // one part of the film: the lag of the subtitles (scaled) with the most speech under them
    private static SyncWindow? Measure(IReadOnlyList<(TimeSpan Start, TimeSpan End)> cues, ActivityWindow window, double scale)
    {
        var values = window.Values;
        var length = values.Length;
        var prefix = new int[length + 1];
        for (var i = 0; i < length; i++)
        {
            prefix[i + 1] = prefix[i] + values[i];
        }

        var range = (int)(SearchRange.TotalSeconds * ActivityWindow.Rate);
        var start = window.Start.TotalSeconds * ActivityWindow.Rate;

        // the subtitles that can fall into the part at any lag of the range, in samples of the film (scaled)
        var spans = new List<(int From, int To)>();
        foreach (var (from, to) in cues)
        {
            var a = (int)Math.Round((from.TotalSeconds * scale * ActivityWindow.Rate) - start);
            var b = (int)Math.Round((to.TotalSeconds * scale * ActivityWindow.Rate) - start);
            if (b > -range && a < length + range && b > a)
            {
                spans.Add((a, b));
            }
        }

        if (spans.Count < 3)
        {
            return null;
        }

        // score(d): speech minus silence under the subtitles moved d samples earlier (they come d late)
        var scores = new double[(2 * range) + 1];
        var covered = new int[scores.Length];
        for (var d = -range; d <= range; d++)
        {
            long sum = 0;
            var inside = 0;
            foreach (var (a, b) in spans)
            {
                var from = Math.Clamp(a - d, 0, length);
                var to = Math.Clamp(b - d, 0, length);
                sum += prefix[to] - prefix[from];
                inside += to - from;
            }

            covered[d + range] = inside;
            // only lags that keep most of the part's subtitles inside it compare fairly
            scores[d + range] = inside == 0 ? double.MinValue : (double)sum / inside;
        }

        var typical = Median(covered.Where(c => c > 0).Select(c => (double)c));
        var bestIndex = -1;
        for (var i = 0; i < scores.Length; i++)
        {
            if (covered[i] >= typical * 0.6 && (bestIndex < 0 || scores[i] > scores[bestIndex]))
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || scores[bestIndex] == double.MinValue)
        {
            return null;
        }

        var second = double.MinValue;
        for (var i = 0; i < scores.Length; i++)
        {
            if (Math.Abs(i - bestIndex) > ActivityWindow.Rate && covered[i] >= typical * 0.6)
            {
                second = Math.Max(second, scores[i]);
            }
        }

        // scores are in −1 … +1; coverage = the share of the subtitles' time with speech
        var coverage = (scores[bestIndex] + 1) / 2;
        var margin = second == double.MinValue ? 1 : (scores[bestIndex] - second) / 2;
        var lag = TimeSpan.FromSeconds((double)(bestIndex - range) / ActivityWindow.Rate);
        return new SyncWindow(window.Start + (window.Length / 2), lag, coverage, margin);
    }

    // least squares through the points (one point: no slope)
    private static (double Offset, double Slope) Line(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0)
        {
            return (0, 0);
        }

        if (points.Count == 1 || points.Max(p => p.X) - points.Min(p => p.X) < 60)
        {
            return (points.Average(p => p.Y), 0);
        }

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var slope = points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return (meanY - (slope * meanX), slope);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
