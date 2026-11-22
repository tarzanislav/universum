using Universum.Core.Media;

namespace Universum.Core.Batch;

/// <summary>
/// One title of the batch (owner, 21. 11. 2026 – one row for a series or a film): the groups (its variants) holding the
/// files of one title – they differ in the tracks they carry (a subtitle more, a sound without a language) or lie on two
/// disks. The decisions stay with each group; the page shows and decides them together.
/// </summary>
public sealed class BatchTitle(string key, IReadOnlyList<BatchGroup> variants)
{
    public string Key { get; } = key;

    /// <summary>The groups of the title, in the order of the batch.</summary>
    public IReadOnlyList<BatchGroup> Variants { get; } = variants;

    /// <summary>The variant with the most files (the first of them on a tie): the others are compared with it.</summary>
    public BatchGroup Main { get; } = variants.OrderByDescending(v => v.Files.Count).First();

    public int FileCount => Variants.Sum(v => v.Files.Count);

    public IEnumerable<MediaFile> Files => Variants.SelectMany(v => v.Files);

    public bool HasVariants => Variants.Count > 1;
}

/// <summary>
/// A track of a title: the same track in several variants (<see cref="BatchTitles.Slots"/>); a decision made for it is
/// written to each variant that carries it.
/// </summary>
public sealed class TitleSlot(BatchGroup group, SlotKey key, MediaTrack track)
{
    /// <summary>The variant the track is shown from (the main one when it carries the track).</summary>
    public BatchGroup Group { get; } = group;

    public SlotKey Key { get; } = key;

    public MediaTrack Track { get; } = track;

    /// <summary>The same track in the other variants.</summary>
    public List<(BatchGroup Group, SlotKey Key)> Peers { get; } = [];

    /// <summary>Every variant carrying the track, with its place there.</summary>
    public IEnumerable<(BatchGroup Group, SlotKey Key)> Places => [(Group, Key), .. Peers];

    /// <summary>How many files of the title carry the track.</summary>
    public int FileCount => Group.Files.Count + Peers.Sum(p => p.Group.Files.Count);
}

public static class BatchTitles
{
    /// <summary>
    /// The groups gathered into titles, in the order of each title's first group; <paramref name="keyOf"/> gives the title
    /// of a group, null = a title of its own (a group of several films).
    /// </summary>
    public static IReadOnlyList<BatchTitle> Of(IEnumerable<BatchGroup> groups, Func<BatchGroup, string?> keyOf)
    {
        var order = new List<(string Key, List<BatchGroup> Groups)>();
        var byKey = new Dictionary<string, List<BatchGroup>>(StringComparer.Ordinal);
        var alone = 0;
        foreach (var group in groups)
        {
            var key = keyOf(group) ?? "\u0001" + alone++;
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = [];
                order.Add((key, list));
            }

            list.Add(group);
        }

        return [.. order.Select(o => new BatchTitle(o.Key, o.Groups))];
    }

    /// <summary>
    /// The tracks of a title across its variants: its pictures (the n-th video track of each variant together), then the
    /// tracks of the main variant in its order, each with the same track of the other variants, then the tracks only some
    /// variants carry. The same track = the same type, the same language
    /// written in the file, the same Forced and kind (inside the file or a file of its own), the n-th such track of the
    /// variant with the n-th of the other (a film with two English sounds – the film and a commentary). A track without a
    /// language is never paired: what it is, is decided for its own variant (nothing is guessed blindly).
    /// </summary>
    public static IReadOnlyList<TitleSlot> Slots(BatchTitle title)
    {
        var main = title.Main;
        // the pictures first (owner, 22. 11. 2026): the n-th video track of a variant is the n-th of the main one
        var pictures = SlotKey.Videos(main.Representative).Select(x => new TitleSlot(main, x.Key, x.Track)).ToList();
        foreach (var variant in title.Variants.Where(v => !ReferenceEquals(v, main)))
        {
            foreach (var (key, track) in SlotKey.Videos(variant.Representative))
            {
                if (pictures.FirstOrDefault(p => p.Key == key && p.Group != variant && p.Peers.All(x => x.Group != variant)) is { } same)
                {
                    same.Peers.Add((variant, key));
                }
                else
                {
                    pictures.Add(new TitleSlot(variant, key, track));
                }
            }
        }

        var slots = SlotKey.Of(main.Representative).Select(x => new TitleSlot(main, x.Key, x.Track)).ToList();
        foreach (var variant in title.Variants.Where(v => !ReferenceEquals(v, main)))
        {
            var used = new HashSet<TitleSlot>();
            foreach (var (key, track) in SlotKey.Of(variant.Representative))
            {
                var same = Pairable(track) ? slots.FirstOrDefault(s => !used.Contains(s) && s.Group != variant && Same(s.Track, track) &&
                                                                      s.Peers.All(p => p.Group != variant)) : null;
                if (same is null)
                {
                    same = new TitleSlot(variant, key, track);
                    slots.Add(same);
                }
                else
                {
                    same.Peers.Add((variant, key));
                }

                used.Add(same);
            }
        }

        return [.. pictures, .. slots];
    }

    private static bool Pairable(MediaTrack track) => !track.Language.IsUndetermined;

    private static bool Same(MediaTrack a, MediaTrack b) =>
        a.Type == b.Type && Pairable(a) && string.Equals(a.Language.Primary, b.Language.Primary, StringComparison.OrdinalIgnoreCase) &&
        a.Flags.Forced == b.Flags.Forced && (a.ExternalFile is null) == (b.ExternalFile is null);
}
