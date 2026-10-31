using System.Text;
using Universum.Core.Subtitles.Pictures;

namespace Universum.Core.Subtitles.Ocr;

/// <summary>A picture of a subtitle to be read, with its times.</summary>
public sealed record OcrPicture(TimeSpan Start, TimeSpan End, InkBitmap Ink)
{
    /// <summary>The pictures of PGS subtitles as ink, drawn on all processors (a subtitle that cannot be drawn is left out).</summary>
    public static IReadOnlyList<OcrPicture> Of(IReadOnlyList<PictureCue> cues, CancellationToken cancellationToken = default)
    {
        var pictures = new OcrPicture?[cues.Count];
        Parallel.For(0, cues.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            if (PgsReader.Render(cues[i]) is { } image)
            {
                pictures[i] = new OcrPicture(cues[i].Start, cues[i].End, InkBitmap.FromBgra(image.Bgra, image.Width, image.Height, image.Width * 4));
            }
        });

        return pictures.OfType<OcrPicture>().ToList();
    }
}

/// <summary>
/// One letter shape of a film (all its occurrences are the same letter): what it was read as – null while unknown – and a
/// sample to show the user when it has to be told.
/// </summary>
public sealed class OcrCluster
{
    internal OcrCluster(GlyphShape shape, InkBitmap sample, float baseline, float capHeight)
    {
        Shape = shape;
        Sample = sample;
        SampleBaseline = baseline;
        SampleCapHeight = capHeight;
    }

    public GlyphShape Shape { get; }

    /// <summary>The ink of its first occurrence (just the letter).</summary>
    public InkBitmap Sample { get; }

    internal float SampleBaseline { get; }

    internal float SampleCapHeight { get; }

    /// <summary>The text it was read as ("a", "č", "rn" for two letters that touch); null while unknown.</summary>
    public string? Text { get; internal set; }

    /// <summary>It matched an italic letter.</summary>
    public bool Italic { get; internal set; }

    /// <summary>How many times it occurs in the film.</summary>
    public int Count { get; internal set; }

    /// <summary>The user told it (it is kept for other films).</summary>
    public bool Taught { get; internal set; }

    /// <summary>Where it first occurs: the subtitle and the line (to show it in its context).</summary>
    public int Cue { get; internal set; }

    public int Line { get; internal set; }

    /// <summary>Its box in the picture of its first occurrence.</summary>
    internal (int X0, int Y0, int X1, int Y1) Box { get; init; }
}

/// <summary>
/// UNIVERSUM's own reading of picture subtitles (owner, 31. 10. 2026: own, original, clean and efficient code – no OCR
/// library): each picture is split into lines and letters; letters of the same shape are gathered (a film has a few
/// hundred), each shape is compared with letters drawn from the fonts subtitles use and with the letters the user told
/// before; a shape that is no letter may be two touching letters (split where they touch least); what is still unknown is
/// asked once for the whole film. Words are then mended by their context (I/l, O/0, ľ ť ď, quotation marks).
/// </summary>
public sealed class SubtitleOcr(OcrTemplates templates, OcrMemory? memory = null, string? language = null)
{
    /// <summary>Up to this distance a shape is the letter of the template.</summary>
    public const float Known = 0.22f;

    /// <summary>Up to this distance a shape is the letter of the template when no other letter comes near.</summary>
    public const float Likely = 0.33f;

    // the letters of the languages (a near letter of another language gives way to one of these)
    private static readonly Dictionary<string, string> Alphabets = new(StringComparer.Ordinal)
    {
        ["sk"] = "áäčďéíĺľňóôŕšťúýžÁÄČĎÉÍĹĽŇÓÔŔŠŤÚÝŽ",
        ["cs"] = "áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ",
        ["en"] = "",
    };

    /// <summary>Shapes nearer than this are the same letter of the film.</summary>
    private const float Same = 0.07f;

    /// <summary>A shape this near to one the user told is that letter.</summary>
    internal const float Told = Same * 1.5f;

    /// <summary>Reads the pictures: every shape gathered and recognised; what remains unknown is in <see cref="OcrRun.Unknown"/>.</summary>
    public OcrRun Read(IReadOnlyList<OcrPicture> pictures, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // the pictures split into lines and letters – each on its own, on all processors
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        var segmented = new Segmentation[pictures.Count];
        var done = 0;
        Parallel.For(0, pictures.Count, options, i =>
        {
            segmented[i] = Segmenter.Split(pictures[i].Ink);
            Step(ref done, pictures.Count, 0, 0.4);
        });

        // the height of capitals of the film: a line of small letters only ("ano") must not take its x-height for it
        var heights = segmented.SelectMany(s => s.Lines).Where(l => l.Glyphs.Count >= 4).Select(l => l.CapHeight).Order().ToList();
        var filmCap = heights.Count == 0 ? 0 : heights[heights.Count / 2];

        // the shape of every letter (in parallel), then letters of the same shape gathered in reading order
        var shapes = new GlyphShape[pictures.Count][][];
        done = 0;
        Parallel.For(0, pictures.Count, options, c =>
        {
            var lines = segmented[c].Lines;
            shapes[c] = new GlyphShape[lines.Count][];
            for (var l = 0; l < lines.Count; l++)
            {
                var line = lines[l];
                var cap = Cap(line, filmCap);
                shapes[c][l] = line.Glyphs.Select(g => GlyphShape.Of(segmented[c].Cut(g), line.Baseline - g.Y0, cap)).ToArray();
            }

            Step(ref done, pictures.Count, 0.4, 0.3);
        });

        var clusters = new List<OcrCluster>();
        var buckets = new Dictionary<(int, int, int), List<OcrCluster>>();
        var placed = new List<List<List<(OcrCluster Cluster, FoundGlyph Glyph)>>>(pictures.Count);
        for (var c = 0; c < segmented.Length; c++)
        {
            var lines = new List<List<(OcrCluster, FoundGlyph)>>();
            for (var l = 0; l < segmented[c].Lines.Count; l++)
            {
                var line = segmented[c].Lines[l];
                var glyphs = new List<(OcrCluster, FoundGlyph)>(line.Glyphs.Count);
                for (var g = 0; g < line.Glyphs.Count; g++)
                {
                    var glyph = line.Glyphs[g];
                    var shape = shapes[c][l][g];
                    var key = ((int)MathF.Round(MathF.Log(shape.Aspect) * 3), (int)MathF.Round(shape.Top * 4), (int)MathF.Round(shape.Bottom * 4));
                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        buckets[key] = bucket = [];
                    }

                    var cluster = bucket.FirstOrDefault(k => GlyphShape.Distance(k.Shape, shape) < Same);
                    if (cluster is null)
                    {
                        cluster = new OcrCluster(shape, segmented[c].Cut(glyph), line.Baseline - glyph.Y0, Cap(line, filmCap)) { Cue = c, Line = l, Box = (glyph.X0, glyph.Y0, glyph.X1, glyph.Y1) };
                        bucket.Add(cluster);
                        clusters.Add(cluster);
                    }

                    cluster.Count++;
                    glyphs.Add((cluster, glyph));
                }

                lines.Add(glyphs);
            }

            placed.Add(lines);
        }

        // each shape recognised on its own (the memory and the templates are only read)
        done = 0;
        Parallel.For(0, clusters.Count, options, i =>
        {
            Recognise(clusters[i]);
            Step(ref done, clusters.Count, 0.7, 0.3);
        });

        return new OcrRun(pictures, segmented, placed, clusters, memory, language);

        // progress from the parallel work: about a hundred reports, not one for every picture
        void Step(ref int count, int total, double from, double share)
        {
            var now = Interlocked.Increment(ref count);
            if (progress is not null && (now == total || now % Math.Max(1, total / 100) == 0))
            {
                progress.Report(from + (share * now / total));
            }
        }
    }

    // the line's own height of capitals, or the film's when the line has small letters only
    private static float Cap(FoundLine line, float filmCap) => line.CapHeight >= filmCap * 0.85f ? line.CapHeight : filmCap;

    // a letter with an accent not used in the film's language
    private bool Foreign(string text)
    {
        var code = language switch { "cze" or "ces" => "cs", "slo" or "slk" => "sk", "eng" => "en", _ => language };
        return code is not null && Alphabets.TryGetValue(code, out var alphabet) && text.Length == 1 && char.IsLetter(text[0]) && text[0] > 'z' &&
               !alphabet.Contains(text[0], StringComparison.Ordinal);
    }

    // the letter of a shape: a letter the user told, a template, or two touching letters
    private void Recognise(OcrCluster cluster)
    {
        if (memory?.Nearest(cluster.Shape) is { Distance: < Told } told)
        {
            cluster.Text = told.Text;
            return;
        }

        var (best, distance, second, _) = templates.NearestTwo(cluster.Shape);

        // a letter of another language (õ, ñ, ü) gives way to a near one of the film's language (ô, ň, ú)
        if (Foreign(best.Text) && second.Template.Text is { } other && !Foreign(other) && second.Distance - distance < 0.05f)
        {
            (best, distance, second) = (second.Template, second.Distance, (best, distance));
        }

        // small letters (a few pixels per stroke) differ more from any template
        var slack = cluster.Shape.Pixels < 24 ? 0.05f : 0;
        if (distance <= Known + slack)
        {
            cluster.Text = best.Text;
            cluster.Italic = best.Italic;
            return;
        }

        // wider than a letter: maybe two letters touching ("rn", "vy"), split at the column with the least ink – when the
        // two fit better than the one letter
        var split = cluster.Shape.Aspect > 0.55f ? Split(cluster) : null;
        if (split is { } two && (two.Score / 2 < distance - 0.02f || distance > Likely + slack))
        {
            cluster.Text = two.Text;
            cluster.Italic = two.Italic;
        }
        else if (distance <= Likely + slack && second.Distance - distance >= 0.04f)
        {
            cluster.Text = best.Text;
            cluster.Italic = best.Italic;
        }
    }

    private (string Text, bool Italic, float Score)? Split(OcrCluster cluster)
    {
        var ink = cluster.Sample;
        var baseline = cluster.SampleBaseline;
        var columns = new int[ink.Width];
        for (var x = 0; x < ink.Width; x++)
        {
            for (var y = 0; y < ink.Height; y++)
            {
                columns[x] += ink[x, y] ? 1 : 0;
            }
        }

        (string Text, bool Italic, float Score)? best = null;
        var candidates = Enumerable.Range(ink.Width / 4, Math.Max(1, ink.Width / 2)).OrderBy(x => columns[Math.Min(x, ink.Width - 1)]).Take(4);
        foreach (var x in candidates.Where(x => x > 1 && x < ink.Width - 1))
        {
            var left = Part(ink, 0, x);
            var right = Part(ink, x, ink.Width);
            if (left is null || right is null)
            {
                continue;
            }

            var a = templates.Nearest(GlyphShape.Of(ink.Crop(left.Value.X0, left.Value.Y0, left.Value.X1, left.Value.Y1), baseline - left.Value.Y0, cluster.SampleCapHeight));
            var b = templates.Nearest(GlyphShape.Of(ink.Crop(right.Value.X0, right.Value.Y0, right.Value.X1, right.Value.Y1), baseline - right.Value.Y0, cluster.SampleCapHeight));
            // touching letters are letters (a mark beside a letter is a piece of its own)
            if (a.Distance <= Likely && b.Distance <= Likely && a.Best.Text.All(char.IsLetter) && b.Best.Text.All(char.IsLetter) &&
                (best is null || a.Distance + b.Distance < best.Value.Score))
            {
                best = (a.Best.Text + b.Best.Text, a.Best.Italic && b.Best.Italic, a.Distance + b.Distance);
            }
        }

        return best;
    }

    // the box of the ink between two columns
    private static (int X0, int Y0, int X1, int Y1)? Part(InkBitmap ink, int from, int to)
    {
        int y0 = int.MaxValue, y1 = -1, x0 = int.MaxValue, x1 = -1;
        for (var y = 0; y < ink.Height; y++)
        {
            for (var x = from; x < to; x++)
            {
                if (ink[x, y])
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x + 1);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y + 1);
                }
            }
        }

        return x1 < 0 ? null : (x0, y0, x1, y1);
    }
}

/// <summary>
/// The reading of a film: the shapes not yet known (to be told), and the subtitles as text once they are.
/// </summary>
public sealed class OcrRun
{
    private readonly IReadOnlyList<OcrPicture> _pictures;
    private readonly IReadOnlyList<Segmentation> _segmented;
    private readonly IReadOnlyList<List<List<(OcrCluster Cluster, FoundGlyph Glyph)>>> _placed;
    private readonly OcrMemory? _memory;
    private readonly string? _language;

    internal OcrRun(IReadOnlyList<OcrPicture> pictures, IReadOnlyList<Segmentation> segmented, IReadOnlyList<List<List<(OcrCluster, FoundGlyph)>>> placed,
        IReadOnlyList<OcrCluster> clusters, OcrMemory? memory, string? language)
    {
        _pictures = pictures;
        _segmented = segmented;
        _placed = placed;
        Clusters = clusters;
        _memory = memory;
        _language = language;
    }

    /// <summary>All the letter shapes of the film.</summary>
    public IReadOnlyList<OcrCluster> Clusters { get; }

    /// <summary>The shapes still unknown, the most frequent first.</summary>
    public IReadOnlyList<OcrCluster> Unknown => Clusters.Where(c => c.Text is null).OrderByDescending(c => c.Count).ToList();

    /// <summary>The letters of the film and how many of them were read (known shapes).</summary>
    public (int Letters, int Read) Coverage => (Clusters.Sum(c => c.Count), Clusters.Where(c => c.Text is not null).Sum(c => c.Count));

    /// <summary>
    /// The user tells a shape (it is kept for other films; empty text: a speck to be left out); unknown shapes of the film
    /// as near to it are the same letter. Returns how many shapes it told.
    /// </summary>
    public int Teach(OcrCluster cluster, string text)
    {
        cluster.Text = text;
        cluster.Taught = true;
        _memory?.Add(cluster.Shape, text);
        var told = 1;
        foreach (var other in Clusters.Where(c => c.Text is null && GlyphShape.Distance(c.Shape, cluster.Shape) < SubtitleOcr.Told))
        {
            other.Text = text;
            told++;
        }

        return told;
    }

    /// <summary>
    /// The line of a shape's first occurrence (to show it in its context): the ink of the line and where the shape lies in
    /// it (columns).
    /// </summary>
    public (InkBitmap Line, int X0, int X1) Context(OcrCluster cluster)
    {
        var segmentation = _segmented[cluster.Cue];
        var glyphs = segmentation.Lines[cluster.Line].Glyphs;
        int x0 = glyphs.Min(g => g.X0), y0 = glyphs.Min(g => g.Y0), x1 = glyphs.Max(g => g.X1), y1 = glyphs.Max(g => g.Y1);
        return (segmentation.Ink.Crop(x0, y0, x1, y1), cluster.Box.X0 - x0, cluster.Box.X1 - x0);
    }

    /// <summary>The subtitles as text: unknown letters as "�", italic lines in &lt;i&gt;.</summary>
    public SubtitleDocument Document()
    {
        var gaps = Gaps();
        var cues = new List<SubtitleCue>();
        for (var c = 0; c < _pictures.Count; c++)
        {
            var lines = new List<string>();
            for (var l = 0; l < _placed[c].Count; l++)
            {
                var text = Line(_placed[c][l], _segmented[c].Lines[l], gaps);
                if (text.Length > 0)
                {
                    lines.Add(text);
                }
            }

            if (lines.Count > 0)
            {
                cues.Add(new SubtitleCue(_pictures[c].Start, _pictures[c].End, string.Join("\n", lines)));
            }
        }

        return new SubtitleDocument(cues, "OCR");
    }

    // the space between words: the threshold that splits the gaps between letters of the film best (Otsu over the gaps in
    // hundredths of the height of capitals), at least a fifth of it
    private float Gaps()
    {
        Span<int> histogram = stackalloc int[256];
        var count = 0;
        for (var c = 0; c < _placed.Count; c++)
        {
            for (var l = 0; l < _placed[c].Count; l++)
            {
                var glyphs = _placed[c][l];
                var cap = _segmented[c].Lines[l].CapHeight;
                var right = glyphs.Count == 0 ? 0 : glyphs[0].Glyph.X1;
                for (var i = 1; i < glyphs.Count; i++)
                {
                    var gap = (glyphs[i].Glyph.X0 - right) / cap;
                    right = Math.Max(right, glyphs[i].Glyph.X1);
                    histogram[Math.Clamp((int)(gap * 100), 0, 255)]++;
                    count++;
                }
            }
        }

        // the middle between the two groups Otsu finds (gaps between letters, spaces between words)
        var split = InkBitmap.Otsu(histogram, count);
        double below = 0, belowCount = 0, above = 0, aboveCount = 0;
        for (var i = 0; i < 256; i++)
        {
            if (i <= split)
            {
                below += i * (double)histogram[i];
                belowCount += histogram[i];
            }
            else
            {
                above += i * (double)histogram[i];
                aboveCount += histogram[i];
            }
        }

        var middle = belowCount == 0 || aboveCount == 0 ? split : ((below / belowCount) + (above / aboveCount)) / 2;
        return Math.Max(0.25f, (float)middle / 100f);
    }

    private string Line(List<(OcrCluster Cluster, FoundGlyph Glyph)> glyphs, FoundLine line, float space)
    {
        var text = new StringBuilder();
        var italic = 0;
        var right = 0;

        // a space is wider than the film's gaps between words and clearly wider than the line's usual gap between letters
        var usual = Median(glyphs, line.CapHeight);
        for (var i = 0; i < glyphs.Count; i++)
        {
            var (cluster, glyph) = glyphs[i];

            // from the rightmost ink before it: a caron beside "L" (Ľ) lies within the letter's box
            var gap = i == 0 ? 0 : (glyph.X0 - right) / line.CapHeight;
            right = Math.Max(right, glyph.X1);
            var piece = cluster.Text ?? "�";
            if (i > 0 && gap > space && gap > usual * 1.8f && piece.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(piece);
            italic += cluster.Italic ? 1 : 0;
        }

        var mended = OcrWords.Mend(text.ToString(), _language);
        static float Median(List<(OcrCluster Cluster, FoundGlyph Glyph)> glyphs, float cap)
        {
            var gaps = new List<float>();
            var right = glyphs.Count == 0 ? 0 : glyphs[0].Glyph.X1;
            for (var i = 1; i < glyphs.Count; i++)
            {
                gaps.Add((glyphs[i].Glyph.X0 - right) / cap);
                right = Math.Max(right, glyphs[i].Glyph.X1);
            }

            gaps.Sort();
            return gaps.Count == 0 ? 0 : MathF.Max(0.02f, gaps[gaps.Count / 2]);
        }

        return mended.Length > 0 && italic * 10 >= glyphs.Count * 6 ? $"<i>{mended}</i>" : mended;
    }
}
