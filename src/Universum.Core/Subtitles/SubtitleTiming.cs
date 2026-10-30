using System.Globalization;
using System.Numerics;

namespace Universum.Core.Subtitles;

/// <summary>A frame rate to choose from (owner, 30. 10. 2026: 23,976; 24; 25; 29,97; 30; 50; 23,978 special).</summary>
/// <param name="Label">As shown: "23,976".</param>
/// <param name="Numerator">The exact rate is Numerator / Denominator frames a second (23,976 = 24000/1001).</param>
public sealed record FrameRate(string Label, long Numerator, long Denominator)
{
    /// <summary>The rates of the Zmena FPS titulkov list, in the owner's order.</summary>
    public static IReadOnlyList<FrameRate> All { get; } =
    [
        new("23,976", 24000, 1001),
        new("24", 24, 1),
        new("25", 25, 1),
        new("29,97", 30000, 1001),
        new("30", 30, 1),
        new("50", 50, 1),
        new("23,978 (špeciálne)", 23978, 1000),
    ];

    public double Value => (double)Numerator / Denominator;

    public override string ToString() => Label;
}

/// <summary>
/// A change of the times of subtitles: every time is multiplied by Numerator / Denominator and then moved by
/// <see cref="DelayMilliseconds"/> – what mkvmerge does with "--sync TID:delay,numerator/denominator", so the same change
/// is written into an MKV without touching the subtitles themselves (also pictures – PGS, VobSub) or applied to a text file.
/// </summary>
public sealed record SubtitleTiming(long DelayMilliseconds, long Numerator, long Denominator)
{
    /// <summary>No change.</summary>
    public static SubtitleTiming None { get; } = new(0, 1, 1);

    public bool IsNone => DelayMilliseconds == 0 && Numerator == Denominator;

    /// <summary>The factor the times are multiplied by (1.0427 = 4,27 % longer).</summary>
    public double Factor => (double)Numerator / Denominator;

    /// <summary>Posun (owner: "Delay in (ms)", 1000 = 1 second; negative = earlier).</summary>
    public static SubtitleTiming Delay(long milliseconds) => new(milliseconds, 1, 1);

    /// <summary>
    /// Zmena FPS titulkov: subtitles timed for a video of <paramref name="from"/> frames a second shown with a video of
    /// <paramref name="to"/> frames a second – the same frame comes at time × from / to (25 → 23,976: 4,27 % later).
    /// </summary>
    public static SubtitleTiming Fps(FrameRate from, FrameRate to) => Reduced(0, (BigInteger)from.Numerator * to.Denominator, (BigInteger)from.Denominator * to.Numerator);

    /// <summary>
    /// Zmena rýchlosti titulkov (percentá): +10 % = the subtitles run 10 % faster (every time ÷ 1,10), −10 % = slower
    /// (÷ 0,90); null for −100 % and below (the subtitles would stand still).
    /// </summary>
    public static SubtitleTiming? Speed(decimal percent)
    {
        if (percent <= -100 || percent > 1000)
        {
            return null;
        }

        // millionths of a per cent are kept exactly: 100 / (100 + p) = 10⁸ / (10⁸ + 10⁶·p)
        var millionths = (long)Math.Round(percent * 1_000_000, MidpointRounding.AwayFromZero);
        return Reduced(0, 100_000_000, 100_000_000 + millionths);
    }

    /// <summary>
    /// Synchronizácia podľa dvoch titulkov: a subtitle shown at <paramref name="first"/> belongs at
    /// <paramref name="firstTarget"/>, another one at <paramref name="second"/> belongs at <paramref name="secondTarget"/> –
    /// the speed and the shift between them (null when the two are at the same time or the order turns).
    /// </summary>
    public static SubtitleTiming? TwoPoints(TimeSpan first, TimeSpan firstTarget, TimeSpan second, TimeSpan secondTarget)
    {
        var from = (long)Math.Round((second - first).TotalMilliseconds);
        var to = (long)Math.Round((secondTarget - firstTarget).TotalMilliseconds);
        if (from <= 0 || to <= 0)
        {
            return null;
        }

        var delay = (long)Math.Round(firstTarget.TotalMilliseconds - (first.TotalMilliseconds * to / from));
        return Reduced(delay, to, from);
    }

    /// <summary>A factor found by measuring (automatic sync) and a delay: the factor kept to nine digits.</summary>
    public static SubtitleTiming FromFactor(double factor, long delayMilliseconds) =>
        Reduced(delayMilliseconds, new BigInteger(Math.Round(factor * 1_000_000_000)), 1_000_000_000);

    /// <summary>This change and then <paramref name="next"/>: (t·a + d₁)·b + d₂.</summary>
    public SubtitleTiming Then(SubtitleTiming next) =>
        Reduced((long)Math.Round((double)DelayMilliseconds * next.Numerator / next.Denominator) + next.DelayMilliseconds,
            (BigInteger)Numerator * next.Numerator, (BigInteger)Denominator * next.Denominator);

    /// <summary>The time a subtitle at <paramref name="time"/> gets.</summary>
    public TimeSpan Apply(TimeSpan time) =>
        TimeSpan.FromTicks((long)Math.Round((decimal)time.Ticks * Numerator / Denominator)) + TimeSpan.FromMilliseconds(DelayMilliseconds);

    /// <summary>The subtitles with the new times; those that would end before the start of the film are left out.</summary>
    public SubtitleDocument Apply(SubtitleDocument document) =>
        IsNone ? document : document.With(document.Cues.Select(c => c with { Start = Apply(c.Start), End = Apply(c.End) }).Where(c => c.End > TimeSpan.Zero)
            .Select(c => c.Start < TimeSpan.Zero ? c with { Start = TimeSpan.Zero } : c));

    /// <summary>The value of mkvmerge's "--sync" after "TID:": "delay,numerator/denominator".</summary>
    public string MkvMergeValue => string.Create(CultureInfo.InvariantCulture, $"{DelayMilliseconds},{Numerator}/{Denominator}");

    /// <summary>In words: "posun +1 500 ms, časy × 1,04271 (titulky o 4,096 % pomalšie)".</summary>
    public string Describe()
    {
        var sk = CultureInfo.GetCultureInfo("sk-SK");
        var parts = new List<string>();
        if (Numerator != Denominator)
        {
            var speed = ((double)Denominator / Numerator - 1) * 100;
            parts.Add($"časy × {Factor.ToString("0.#####", sk)} (titulky o {Math.Abs(speed).ToString("0.###", sk)} % {(speed > 0 ? "rýchlejšie" : "pomalšie")})");
        }

        if (DelayMilliseconds != 0)
        {
            parts.Add($"posun {(DelayMilliseconds > 0 ? "+" : "−")}{Math.Abs(DelayMilliseconds).ToString("#,0", sk)} ms");
        }

        return parts.Count == 0 ? "bez zmeny časov" : string.Join(", ", parts);
    }

    // the smallest numbers mkvmerge gets (they fit a long: the rates and per cents above are small)
    private static SubtitleTiming Reduced(long delay, BigInteger numerator, BigInteger denominator)
    {
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        if (divisor > 1)
        {
            numerator /= divisor;
            denominator /= divisor;
        }

        // too long for mkvmerge: the factor to nine digits
        while (BigInteger.Abs(numerator) > 1_000_000_000_000 || BigInteger.Abs(denominator) > 1_000_000_000_000)
        {
            numerator /= 10;
            denominator /= 10;
        }

        return new SubtitleTiming(delay, (long)numerator, (long)denominator);
    }
}
