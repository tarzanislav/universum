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

    /// <summary>The user told it – in this film or before (the shapes told are kept for other films).</summary>
    public bool Taught { get; internal set; }

    /// <summary>Where it first occurs: the subtitle and the line (to show it in its context).</summary>
    public int Cue { get; internal set; }

    public int Line { get; internal set; }

    /// <summary>The ink of the line it first occurs in, and its columns there (to show it in its context).</summary>
    internal (InkBitmap Line, int X0, int X1) Context { get; init; }

    /// <summary>How far its shape is from the letter it was read as (0 when told by the user).</summary>
    public float Distance { get; internal set; }

    /// <summary>The nearest letters of the templates – different texts, the nearest first: what it may be instead.</summary>
    public IReadOnlyList<(string Text, float Distance)> Candidates { get; internal set; } = [];

    /// <summary>Two touching letters split apart (never sure).</summary>
    public bool Split { get; internal set; }

    /// <summary>Read surely: told by the user, or near its letter and clearly nearer than to any other.</summary>
    public bool Sure => Taught || (Text is not null && !Split && Distance <= SubtitleOcr.Known &&
                                   Candidates.Where(c => c.Text != Text).Select(c => c.Distance).DefaultIfEmpty(float.MaxValue).First() - Distance >= 0.05f);
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
                        cluster = new OcrCluster(shape, segmented[c].Cut(glyph), line.Baseline - glyph.Y0, Cap(line, filmCap))
                        {
                            Cue = c, Line = l, Context = LineContext(segmented[c], line, glyph),
                        };
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

        return new OcrRun(pictures.Select(p => (p.Start, p.End)).ToList(), segmented.Select(s => s.Lines.Select(l => Cap(l, filmCap)).ToArray()).ToList(),
            placed, clusters, memory, language);

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

    // the line's own height of capitals near the film's; the film's when the line has small letters only (or a few letters
    // measured too tall)
    private static float Cap(FoundLine line, float filmCap) =>
        filmCap <= 0 || (line.CapHeight >= filmCap * 0.85f && line.CapHeight <= filmCap * 1.15f) ? line.CapHeight : filmCap;

    // the ink of a line and the columns of one of its letters there
    private static (InkBitmap Line, int X0, int X1) LineContext(Segmentation segmentation, FoundLine line, FoundGlyph glyph)
    {
        int x0 = line.Glyphs.Min(g => g.X0), y0 = line.Glyphs.Min(g => g.Y0), x1 = line.Glyphs.Max(g => g.X1), y1 = line.Glyphs.Max(g => g.Y1);
        return (segmentation.Ink.Crop(x0, y0, x1, y1), glyph.X0 - x0, glyph.X1 - x0);
    }

    // a letter with an accent not used in the film's language
    private bool Foreign(string text) => text is { Length: 1 } && Foreign(text[0], language);

    /// <summary>A letter with an accent not used in the language (Czech, Slovak, English; any letter of another language is not).</summary>
    internal static bool Foreign(char letter, string? language)
    {
        var code = language switch { "cze" or "ces" => "cs", "slo" or "slk" => "sk", "eng" => "en", _ => language };
        return code is not null && Alphabets.TryGetValue(code, out var alphabet) && char.IsLetter(letter) && letter > 'z' && !alphabet.Contains(letter, StringComparison.Ordinal);
    }

    // the letter of a shape: a letter the user told, a template, or two touching letters
    private void Recognise(OcrCluster cluster)
    {
        var ranked = templates.Ranked(cluster.Shape, 5);
        cluster.Candidates = [.. ranked.Select(r => (r.Template.Text, r.Distance))];
        if (memory?.Nearest(cluster.Shape) is { Distance: < Told } told)
        {
            cluster.Text = told.Text;
            cluster.Distance = told.Distance;
            cluster.Taught = true;
            return;
        }

        if (ranked.Count == 0)
        {
            return;
        }

        var (best, distance) = ranked[0];
        (OcrTemplate Template, float Distance) second = ranked.Count > 1 ? ranked[1] : (default, float.MaxValue);

        // a letter of another language (õ, ñ, ü) gives way to a near one of the film's language (ô, ň, ú)
        if (Foreign(best.Text) && second.Template.Text is { } other && !Foreign(other) && second.Distance - distance < 0.05f)
        {
            (best, distance, second) = (second.Template, second.Distance, (best, distance));
        }

        // small letters (a small font: a few pixels per stroke) differ more from any template
        var slack = cluster.Shape.CapPixels() is > 0 and < 24 ? 0.05f : 0;
        if (distance <= Known + slack)
        {
            cluster.Text = best.Text;
            cluster.Italic = best.Italic;
            cluster.Distance = distance;
            return;
        }

        // wider than a letter: maybe two letters touching ("rn", "vy"), split at the column with the least ink – when the
        // two fit better than the one letter
        var split = cluster.Shape.Aspect > 0.55f ? Split(cluster) : null;
        if (split is { } two && (two.Score / 2 < distance - 0.02f || distance > Likely + slack))
        {
            cluster.Text = two.Text;
            cluster.Italic = two.Italic;
            cluster.Distance = two.Score / 2;
            cluster.Split = true;
            cluster.Candidates = [.. two.Others.Concat(cluster.Candidates).OrderBy(c => c.Distance)];
        }
        else if (distance <= Likely + slack && second.Distance - distance >= 0.04f)
        {
            cluster.Text = best.Text;
            cluster.Italic = best.Italic;
            cluster.Distance = distance;
        }
    }

    // two letters touching, split at one of the columns with the least ink in the middle half: the best pair, and the
    // other pairs of their next nearest letters (what the checking of the text may choose instead)
    private (string Text, bool Italic, float Score, List<(string Text, float Distance)> Others)? Split(OcrCluster cluster)
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

        // small letters (a few pixels per stroke) with the same slack as a letter alone
        var slack = cluster.Shape.CapPixels() is > 0 and < 24 ? 0.05f : 0;
        (string Text, bool Italic, float Score)? best = null;
        var others = new List<(string Text, float Distance)>();
        var cuts = Enumerable.Range(ink.Width / 4, Math.Max(1, ink.Width / 2)).OrderBy(x => columns[Math.Min(x, ink.Width - 1)]).Take(4);
        foreach (var x in cuts.Where(x => x > 1 && x < ink.Width - 1))
        {
            if (Part(ink, 0, x) is not { } left || Part(ink, x, ink.Width) is not { } right)
            {
                continue;
            }

            var a = templates.Ranked(GlyphShape.Of(ink.Crop(left.X0, left.Y0, left.X1, left.Y1), baseline - left.Y0, cluster.SampleCapHeight), 3);
            var b = templates.Ranked(GlyphShape.Of(ink.Crop(right.X0, right.Y0, right.X1, right.Y1), baseline - right.Y0, cluster.SampleCapHeight), 3);
            if (a.Count == 0 || b.Count == 0)
            {
                continue;
            }

            // touching letters are letters (a mark beside a letter is a piece of its own)
            foreach (var (first, firstDistance) in a.Where(t => t.Template.Text.All(char.IsLetter)))
            {
                foreach (var (second, secondDistance) in b.Where(t => t.Template.Text.All(char.IsLetter)))
                {
                    others.Add((first.Text + second.Text, (firstDistance + secondDistance) / 2));
                }
            }

            var (one, oneDistance) = a[0];
            var (two, twoDistance) = b[0];
            if (oneDistance <= Likely + slack && twoDistance <= Likely + slack && one.Text.All(char.IsLetter) && two.Text.All(char.IsLetter) &&
                (best is null || oneDistance + twoDistance < best.Value.Score))
            {
                best = (one.Text + two.Text, one.Italic && two.Italic, oneDistance + twoDistance);
            }
        }

        var pairs = others.GroupBy(o => o.Text).Select(g => (Text: g.Key, Distance: g.Min(o => o.Distance))).OrderBy(o => o.Distance).Take(9).ToList();
        return best is { } found ? (found.Text, found.Italic, found.Score, pairs) : null;
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

/// <summary>A word the checking of the text changed: its subtitle (the place in the document and its time), what was read, what it became and why.</summary>
public sealed record OcrCorrection(int Cue, TimeSpan At, string Before, string After, string Why);

/// <summary>
/// The reading of a film: the shapes not yet known (to be told), the checking of the text by the language, and the
/// subtitles as text. The checking (owner, 31. 10. 2026: errors of reading mended by the language, own code) changes
/// only what the reading was not sure of: a shape read wrongly in the whole film whose other reading makes many of its
/// words words of the language; a doubtful word that another reading of its letters (the next nearest shapes, the
/// letter with another accent, touching letters) makes a word of the language – of the user's lexicon or read surely
/// elsewhere in the film; a letter still unknown, when the letters of the language make one guess clearly the likeliest.
/// A sentence starts with a capital where the shape cannot tell (c/C, s/S, ž/Ž …).
/// </summary>
public sealed class OcrRun
{
    private readonly IReadOnlyList<(TimeSpan Start, TimeSpan End)> _times;
    private readonly IReadOnlyList<float[]> _caps;
    private readonly IReadOnlyList<List<List<(OcrCluster Cluster, FoundGlyph Glyph)>>> _placed;
    private readonly OcrMemory? _memory;
    private readonly string? _language;
    private readonly Dictionary<(int Cue, int Line, int First), (int Last, string Text, string Why)> _fixes = [];
    private readonly Dictionary<OcrCluster, string?> _relabeled = [];
    private readonly List<OcrCorrection> _corrections = [];
    private bool _checked;
    private float? _space;

    /// <param name="caps">The height of capitals of each line (the film's where the line's own is off): the measure of its gaps.</param>
    internal OcrRun(IReadOnlyList<(TimeSpan Start, TimeSpan End)> times, IReadOnlyList<float[]> caps,
        IReadOnlyList<List<List<(OcrCluster, FoundGlyph)>>> placed, IReadOnlyList<OcrCluster> clusters, OcrMemory? memory, string? language)
    {
        _times = times;
        _caps = caps;
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

    /// <summary>What the checking changed, as <see cref="Document"/> wrote it (empty without <see cref="Check"/>).</summary>
    public IReadOnlyList<OcrCorrection> Corrections => _corrections;

    private bool Slavic => _language is "cs" or "sk" or "cze" or "ces" or "slo" or "slk";

    /// <summary>
    /// The user tells a shape (it is kept for other films; empty text: a speck to be left out); unknown shapes of the film
    /// as near to it are the same letter. Returns how many shapes it told.
    /// </summary>
    public int Teach(OcrCluster cluster, string text)
    {
        _relabeled.Remove(cluster);
        cluster.Text = text;
        cluster.Taught = true;
        cluster.Distance = 0;
        _memory?.Add(cluster.Shape, text);
        var told = 1;
        foreach (var other in Clusters.Where(c => c.Text is null && GlyphShape.Distance(c.Shape, cluster.Shape) < SubtitleOcr.Told))
        {
            other.Text = text;
            told++;
        }

        return told;
    }

    /// <summary>The line of a shape's first occurrence (to show it in its context): its ink and where the shape lies in it (columns).</summary>
    public (InkBitmap Line, int X0, int X1) Context(OcrCluster cluster) => cluster.Context;

    /// <summary>
    /// Checks the text by the language: <paramref name="lexicon"/> – the words of the user's subtitles in the film's
    /// language (may be null or empty) – and the words read surely in the film itself. Checking again starts anew (after
    /// the user told unknown shapes).
    /// </summary>
    public void Check(OcrLexicon? lexicon)
    {
        foreach (var (cluster, text) in _relabeled)
        {
            cluster.Text = text;
        }

        _relabeled.Clear();
        _fixes.Clear();
        var words = Words();
        if (lexicon is { Count: >= 500 })
        {
            Relabel(words, lexicon);
        }

        // the film's own words: those read surely (no letter of another language)
        var film = new OcrLexicon();
        foreach (var word in words.Where(w => w.Pieces.All(p => p.Sure)))
        {
            var text = MendWord(Raw(word.Pieces));
            if (!text.Any(c => SubtitleOcr.Foreign(c, _language)))
            {
                film.Learn(text);
            }
        }

        foreach (var word in words)
        {
            Fix(word, film, lexicon);
        }

        _checked = true;
    }

    /// <summary>The subtitles as text: unknown letters as "�", italic lines in &lt;i&gt;; after <see cref="Check"/> with its corrections.</summary>
    public SubtitleDocument Document()
    {
        _corrections.Clear();
        var space = Space();
        var cues = new List<SubtitleCue>();
        var ended = true;
        for (var c = 0; c < _times.Count; c++)
        {
            var lines = new List<string>();
            var changes = new List<(string Before, string After, string Why)>();
            for (var l = 0; l < _placed[c].Count; l++)
            {
                var (text, italic) = Line(c, l, space, changes);
                if (text.Length == 0)
                {
                    continue;
                }

                if (_checked)
                {
                    text = SentenceStart(text, ref ended, changes);
                }

                lines.Add(italic ? $"<i>{text}</i>" : text);
            }

            if (lines.Count > 0)
            {
                _corrections.AddRange(changes.Where(ch => ch.Before != ch.After).Select(ch => new OcrCorrection(cues.Count, _times[c].Start, ch.Before, ch.After, ch.Why)));
                cues.Add(new SubtitleCue(_times[c].Start, _times[c].End, string.Join("\n", lines)));
            }
        }

        return new SubtitleDocument(cues, "OCR");
    }

    private readonly record struct WordAt(int Cue, int Line, int First, int Last, OcrCluster[] Pieces);

    // every word of the film: letters (an unknown shape too) with no space between; an apostrophe right after a letter
    // belongs to it (the caron of ď ť ľ Ľ beside the letter, "don't")
    private List<WordAt> Words()
    {
        var space = Space();
        var words = new List<WordAt>();
        for (var c = 0; c < _placed.Count; c++)
        {
            for (var l = 0; l < _placed[c].Count; l++)
            {
                var arranged = Arrange(c, l, space);
                foreach (var (first, last) in WordsOf(arranged))
                {
                    words.Add(new WordAt(c, l, first, last, arranged.Skip(first).Take(last - first + 1).Select(a => a.Cluster).ToArray()));
                }
            }
        }

        return words;
    }

    private static IEnumerable<(int First, int Last)> WordsOf(List<(OcrCluster Cluster, bool Space)> arranged)
    {
        var first = -1;
        for (var i = 0; i < arranged.Count; i++)
        {
            var (cluster, space) = arranged[i];
            if (space && first >= 0)
            {
                yield return (first, i - 1);
                first = -1;
            }

            var letter = cluster.Text is null || (cluster.Text.Length > 0 && cluster.Text.All(char.IsLetter)) || (first >= 0 && cluster.Text is "'" or "’");
            if (letter && first < 0)
            {
                first = i;
            }
            else if (!letter && first >= 0)
            {
                yield return (first, i - 1);
                first = -1;
            }
        }

        if (first >= 0)
        {
            yield return (first, arranged.Count - 1);
        }
    }

    // the text of letters as read – one of them read otherwise when asked
    private static string Raw(IEnumerable<OcrCluster> pieces, OcrCluster? instead = null, string? text = null) =>
        string.Concat(pieces.Select(p => (ReferenceEquals(p, instead) ? text : p.Text) ?? "�"));

    private string MendWord(string raw) => OcrWords.MendWord(raw, Slavic);

    // how often the lexicon saw the word: every run of its letters (0 when one of them is not known)
    private static int Frequency(OcrLexicon lexicon, string word)
    {
        var frequency = int.MaxValue;
        foreach (var part in OcrLexicon.Words(word))
        {
            frequency = Math.Min(frequency, lexicon.Frequency(part));
        }

        return frequency == int.MaxValue || word.Contains('�') ? 0 : frequency;
    }

    // a shape read wrongly in the whole film: another of its readings makes many more of its words words of the lexicon
    private void Relabel(List<WordAt> words, OcrLexicon lexicon)
    {
        var byShape = new Dictionary<OcrCluster, List<WordAt>>();
        foreach (var word in words)
        {
            foreach (var piece in word.Pieces.Distinct())
            {
                if (!piece.Taught && piece.Candidates.Count > 1 && (piece.Text is null || piece.Text is [var letter] && char.IsLetter(letter)) &&
                    !(piece.Sure && piece.Distance < 0.08f))
                {
                    if (!byShape.TryGetValue(piece, out var list))
                    {
                        byShape[piece] = list = [];
                    }

                    list.Add(word);
                }
            }
        }

        foreach (var (shape, occurrences) in byShape.Where(s => s.Value.Count >= 3))
        {
            int KnownWith(string? text) => occurrences.Count(w => Frequency(lexicon, MendWord(Raw(w.Pieces, shape, text))) > 0);
            var now = shape.Text is null ? 0 : KnownWith(shape.Text);
            var reference = shape.Text is null ? shape.Candidates[0].Distance : shape.Distance;
            string? best = null;
            var bestKnown = now;
            var other = now;
            foreach (var (text, distance) in shape.Candidates)
            {
                if (text == shape.Text || text is not [var letter] || !char.IsLetter(letter) || distance > reference + 0.2f)
                {
                    continue;
                }

                var known = KnownWith(text);
                if (known > bestKnown)
                {
                    other = Math.Max(other, bestKnown);
                    (best, bestKnown) = (text, known);
                }
                else
                {
                    other = Math.Max(other, known);
                }
            }

            if (best is not null && bestKnown >= 3 && bestKnown >= (2 * other) + 2)
            {
                _relabeled[shape] = shape.Text;
                shape.Text = best;
            }
        }
    }

    // a doubtful word: another reading of its letters that is a word of the language; an unknown letter guessed by the
    // letters of the language
    private void Fix(WordAt word, OcrLexicon film, OcrLexicon? lexicon)
    {
        if (word.Pieces.All(p => p.Sure))
        {
            // read surely: kept – a name, a rare word
            return;
        }

        var raw = Raw(word.Pieces);
        if (Frequency(film, MendWord(raw)) > 0 || (lexicon is not null && Frequency(lexicon, MendWord(raw)) > 0))
        {
            return;
        }

        var variants = Variants(word.Pieces, raw);
        (string Raw, double Score)? best = null;
        foreach (var (text, cost) in variants)
        {
            var mended = MendWord(text);
            var seen = Frequency(film, mended);
            var told = lexicon is null ? 0 : Frequency(lexicon, mended);
            if (seen + told == 0)
            {
                continue;
            }

            var score = Math.Log(1 + (5.0 * seen) + told) - (8 * cost);
            if (best is null || score > best.Value.Score)
            {
                best = (text, score);
            }
        }

        if (best is { } found)
        {
            _fixes[(word.Cue, word.Line, word.First)] = (word.Last, found.Raw, "slovo jazyka");
            return;
        }

        // no such word: an unknown letter guessed when the letters of the language make one guess clearly the likeliest
        var models = new[] { film, lexicon }.Where(m => m is { Seen: >= 300 }).Select(m => m!).ToList();
        if (!raw.Contains('�') || models.Count == 0)
        {
            return;
        }

        var guesses = variants.Where(v => !v.Raw.Contains('�'))
            .Select(v => (v.Raw, Score: models.Max(m => m.Likeness(MendWord(v.Raw))) - (2 * v.Cost)))
            .OrderByDescending(v => v.Score).Take(2).ToList();
        if (guesses.Count > 0 && guesses[0].Score >= -3 && (guesses.Count == 1 || guesses[0].Score - guesses[1].Score >= 0.25))
        {
            _fixes[(word.Cue, word.Line, word.First)] = (word.Last, guesses[0].Raw, "podobnosť slovám jazyka");
        }
    }

    // touching letters read as one and the other way round (raw text, before the context mends it)
    private static readonly (string From, string To)[] Touching = [("rn", "m"), ("m", "rn"), ("w", "vy"), ("w", "vv"), ("vv", "w"), ("cl", "d"), ("ri", "n")];

    // other readings of a word: each letter as one of its next nearest shapes or with another accent – the cheapest
    // (sum of how much farther the shapes are) – touching letters, and the caron of ť ď ľ Ľ fallen on the next letter
    private static List<(string Raw, float Cost)> Variants(OcrCluster[] pieces, string raw)
    {
        var caron = Caron(pieces);
        const int width = 256;
        var beam = new List<(string Raw, float Cost)> { ("", 0) };
        foreach (var piece in pieces)
        {
            var options = Options(piece);
            var next = new List<(string Raw, float Cost)>(beam.Count * options.Count);
            foreach (var (text, cost) in beam)
            {
                foreach (var (option, extra) in options)
                {
                    next.Add((text + option, cost + extra));
                }
            }

            beam = next.Count <= width ? next : next.OrderBy(n => n.Cost).Take(width).ToList();
        }

        foreach (var (from, to) in Touching)
        {
            for (var at = raw.IndexOf(from, StringComparison.Ordinal); at >= 0; at = raw.IndexOf(from, at + 1, StringComparison.Ordinal))
            {
                beam.Add((raw[..at] + to + raw[(at + from.Length)..], 0.1f));
            }
        }

        beam.AddRange(caron);
        beam.RemoveAll(v => v.Raw == raw);
        return beam;
    }

    // "ťa" read as "t" and one shape: the caron beside "t" (d, l, L) touched the next letter and made it a letter with a
    // mark (ã, á, ä) – or a shape not known at all
    private static List<(string Raw, float Cost)> Caron(OcrCluster[] pieces)
    {
        var variants = new List<(string Raw, float Cost)>();
        for (var i = 1; i < pieces.Length; i++)
        {
            if (pieces[i - 1].Text is not ("t" or "d" or "l" or "L") || pieces[i].Taught)
            {
                continue;
            }

            var marked = (pieces[i].Text is null ? [] : new[] { (Text: pieces[i].Text!, Distance: pieces[i].Distance) })
                .Concat(pieces[i].Candidates.Take(3)).Where(c => c.Text is [var letter] && char.IsLetter(letter) && Accents.Base(letter) != letter);
            foreach (var (text, distance) in marked)
            {
                var before = string.Concat(pieces.Take(i).Select(p => p.Text ?? "�"));
                var after = string.Concat(pieces.Skip(i + 1).Select(p => p.Text ?? "�"));
                variants.Add((before + "'" + Accents.Base(text[0]) + after, 0.1f + Math.Max(0, distance - pieces[i].Distance)));
            }
        }

        return variants;
    }

    // what one letter may be: as read; a doubtful one also its next nearest shapes; a letter also with another accent
    private static List<(string Text, float Cost)> Options(OcrCluster piece)
    {
        var options = new List<(string Text, float Cost)> { (piece.Text ?? "�", 0) };
        if (piece.Taught)
        {
            return options;
        }

        var reference = piece.Text is null ? piece.Candidates.FirstOrDefault().Distance : piece.Distance;
        if (!piece.Sure)
        {
            foreach (var (text, distance) in piece.Candidates)
            {
                if (text != piece.Text && text.Length > 0 && distance <= reference + 0.15f && options.All(o => o.Text != text))
                {
                    options.Add((text, Math.Max(0, distance - reference)));
                }
            }
        }

        if (piece.Text is [var letter] && char.IsLetter(letter))
        {
            foreach (var other in Accents.Family(letter))
            {
                if (options.All(o => o.Text != other))
                {
                    options.Add((other, piece.Sure ? 0.12f : 0.05f));
                }
            }
        }

        // a speck inside a word (a dot of a neighbouring letter, a piece of the outline) may be no letter at all
        if (piece.Text is "'" or "’" or "." or "," or "`" || (piece.Text is null && piece.Shape.Pixels < piece.Shape.CapPixels() * 0.35f))
        {
            options.Add(("", 0.1f));
        }

        return options;
    }

    // a sentence starts with a capital: a letter whose capital has its shape (c/C, s/S, ž/Ž) was read either way
    private static string SentenceStart(string text, ref bool ended, List<(string Before, string After, string Why)> changes)
    {
        var at = 0;
        while (at < text.Length && !char.IsLetter(text[at]))
        {
            at++;
        }

        var starts = ended || text.TrimStart().StartsWith('-');
        if (at < text.Length && starts && char.IsLower(text[at]) && OcrWords.SameShape.Contains(char.ToUpperInvariant(text[at]), StringComparison.Ordinal))
        {
            var end = at;
            while (end < text.Length && char.IsLetter(text[end]))
            {
                end++;
            }

            var word = text[at..end];
            var capital = char.ToUpperInvariant(word[0]) + word[1..];
            changes.Add((word, capital, "veľké písmeno na začiatku vety"));
            text = text[..at] + capital + text[end..];
        }

        var trimmed = text.TrimEnd();
        ended = trimmed.EndsWith('!') || trimmed.EndsWith('?') || (trimmed.EndsWith('.') && !trimmed.EndsWith("..", StringComparison.Ordinal));
        return text;
    }

    // the space between words: the threshold that splits the gaps between letters of the film best (Otsu over the gaps in
    // hundredths of the height of capitals – the middle of the two groups), at least a quarter of it
    private float Space()
    {
        if (_space is { } known)
        {
            return known;
        }

        Span<int> histogram = stackalloc int[256];
        var count = 0;
        for (var c = 0; c < _placed.Count; c++)
        {
            for (var l = 0; l < _placed[c].Count; l++)
            {
                var glyphs = _placed[c][l];
                var cap = _caps[c][l];
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
        _space = Math.Max(0.25f, (float)middle / 100f);
        return _space.Value;
    }

    // the letters of a line, each with whether a space comes before it: wider than the film's space between words and
    // clearly wider than the line's usual gap between letters (measured from the rightmost ink before it: a caron beside
    // "L" lies within the letter's box)
    private List<(OcrCluster Cluster, bool Space)> Arrange(int cue, int line, float space)
    {
        var glyphs = _placed[cue][line];
        var cap = _caps[cue][line];
        var gaps = new List<float>();
        var right = glyphs.Count == 0 ? 0 : glyphs[0].Glyph.X1;
        for (var i = 1; i < glyphs.Count; i++)
        {
            gaps.Add((glyphs[i].Glyph.X0 - right) / cap);
            right = Math.Max(right, glyphs[i].Glyph.X1);
        }

        var sorted = gaps.Order().ToList();
        var usual = sorted.Count == 0 ? 0 : MathF.Max(0.02f, sorted[sorted.Count / 2]);
        var arranged = new List<(OcrCluster, bool)>(glyphs.Count);
        for (var i = 0; i < glyphs.Count; i++)
        {
            var cluster = glyphs[i].Cluster;
            arranged.Add((cluster, i > 0 && gaps[i - 1] > space && gaps[i - 1] > usual * 1.8f && (cluster.Text ?? "�").Length > 0));
        }

        return arranged;
    }

    // a line as text (mended by its context) and whether it is italic; the corrections of its words noted
    private (string Text, bool Italic) Line(int cue, int line, float space, List<(string Before, string After, string Why)> changes)
    {
        var arranged = Arrange(cue, line, space);
        var words = WordsOf(arranged).ToDictionary(w => w.First, w => w.Last);
        var text = new StringBuilder();
        var italic = 0;
        for (var i = 0; i < arranged.Count; i++)
        {
            if (arranged[i].Space)
            {
                text.Append(' ');
            }

            var last = i;
            if (_fixes.TryGetValue((cue, line, i), out var fix))
            {
                last = fix.Last;
                text.Append(fix.Text);
                changes.Add((MendWord(Original(arranged, i, last)), MendWord(fix.Text), fix.Why));
            }
            else if (words.TryGetValue(i, out var wordLast) && Enumerable.Range(i, wordLast - i + 1).Any(k => _relabeled.ContainsKey(arranged[k].Cluster)))
            {
                last = wordLast;
                var now = Raw(arranged.Skip(i).Take(last - i + 1).Select(a => a.Cluster));
                text.Append(now);
                changes.Add((MendWord(Original(arranged, i, last)), MendWord(now), "tvar prečítaný inak v celom filme"));
            }
            else
            {
                text.Append(arranged[i].Cluster.Text ?? "�");
            }

            for (var k = i; k <= last; k++)
            {
                italic += arranged[k].Cluster.Italic ? 1 : 0;
            }

            i = last;
        }

        var mended = OcrWords.Mend(text.ToString(), _language);
        return (mended, mended.Length > 0 && italic * 10 >= arranged.Count * 6);
    }

    // the letters as the reading gave them, before the checking
    private string Original(List<(OcrCluster Cluster, bool Space)> arranged, int first, int last) =>
        string.Concat(arranged.Skip(first).Take(last - first + 1).Select(a => (_relabeled.TryGetValue(a.Cluster, out var before) ? before : a.Cluster.Text) ?? "�"));
}

/// <summary>Letters that differ only by an accent (a, á, ä; t, ť; e, é, ě …) – of the letters the reading knows.</summary>
internal static class Accents
{
    private static readonly Dictionary<char, string[]> Families = OcrTemplates.Characters.Where(char.IsLetter)
        .GroupBy(c => (Base(c), char.IsUpper(c)))
        .SelectMany(g => g.Select(c => (Letter: c, Others: g.Where(o => o != c).Select(o => o.ToString()).ToArray())))
        .ToDictionary(x => x.Letter, x => x.Others);

    /// <summary>The other letters with the same base letter and case.</summary>
    public static IReadOnlyList<string> Family(char letter) => Families.TryGetValue(letter, out var others) ? others : [];

    /// <summary>The letter without its accent.</summary>
    public static char Base(char letter) => letter.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
}
