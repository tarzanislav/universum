using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Universum.Core.Mp4;

/// <summary>
/// Záznam MP4 (owner, 7. 11. 2026): when an MP4 cannot be read or a change of it does not pass the check, the structure
/// of the file is written down for the developer – its boxes and their places, its tracks, what went wrong and where.
/// Nothing of the picture or sound, and no real name: the file is "súbor 1", the names of the tracks and the title are
/// only counted, never written (the owner sends the record on).
/// </summary>
public static class Mp4Record
{
    /// <summary>The record of a file and of what went wrong with it (or null when it is only described).</summary>
    public static string Describe(string path, string? problem, string version, string fileLabel = "súbor 1")
    {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        text.AppendLine(invariant, $"UNIVERSUM {version} – záznam MP4, {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine(invariant, $"Súbor: {fileLabel}{Path.GetExtension(path).ToLowerInvariant()}");
        if (problem is not null)
        {
            text.AppendLine(invariant, $"Problém: {problem}");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text.AppendLine(invariant, $"Veľkosť: {stream.Length} B");
            text.AppendLine("Boxy súboru (typ @ poloha, veľkosť):");
            TopLevel(stream, text);
            try
            {
                stream.Position = 0;
                var file = Mp4Reader.Read(stream);
                text.AppendLine(invariant, $"Značka: {file.Brand ?? "–"}; fragmentované: {(file.IsFragmented ? "áno" : "nie")}; popis (moov) za dátami: {(file.MovieAtEnd ? "áno" : "nie")}; názov filmu: {Hidden(file.Title)}");
                text.AppendLine("Popis filmu (moov):");
                Tree(file.Movie, 1, text);
                text.AppendLine("Stopy:");
                foreach (var track in file.Tracks)
                {
                    var picture = track.Kind == Mp4TrackKind.Video ? $" {track.Width}×{track.Height}" : track.Kind == Mp4TrackKind.Audio ? $" {track.Channels} k., {track.SampleRate} Hz" : "";
                    string samples;
                    try
                    {
                        samples = file.IsFragmented ? "vo fragmentoch" : $"{track.Samples.Count} vzoriek, {track.Samples.TotalBytes} B";
                    }
                    catch (FormatException ex)
                    {
                        samples = "tabuľka vzoriek sa nedá prečítať: " + ex.Message;
                    }

                    text.AppendLine(invariant,
                        $"  {track.Id}: {track.Handler} {track.Codec}{picture}, jazyk {track.Language}{(track.ExtendedLanguage is { } e ? " / " + e : "")}, názov {Hidden(track.Name)}, handler {Hidden(track.HandlerName)}, príznaky {track.Flags}, úlohy {(track.Kinds.Count == 0 ? "žiadne" : string.Join(",", track.Kinds.Select(k => k.Value)))}, skupina {track.AlternateGroup}, časová mierka {track.Timescale}, úpravy času {track.Edits.Count}, popisov vzoriek {track.SampleEntries}, {samples}");
                }
            }
            catch (FormatException ex)
            {
                text.AppendLine(invariant, $"Čítanie: {ex.Message}{(ex is Mp4FormatException mp4 ? $" (bajt {mp4.Position})" : "")}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine("Súbor sa nedá otvoriť: " + ex.GetType().Name);
        }

        return text.ToString();
    }

    /// <summary>Writes the record into a folder ("zaznam-mp4-2026-11-07-233012.txt"); its path.</summary>
    public static string Save(string folder, string record)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"zaznam-mp4-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
        File.WriteAllText(path, record, new UTF8Encoding(false));
        return path;
    }

    // a name is never written – only whether there is one and how long it is
    private static string Hidden(string? name) => string.IsNullOrEmpty(name) ? "žiadny" : $"[skrytý, {name.Length} znakov]";

    // the boxes at the top read one by one as far as they make sense (a broken file shows where it breaks)
    private static void TopLevel(Stream stream, StringBuilder text)
    {
        var length = stream.Length;
        long at = 0;
        Span<byte> header = stackalloc byte[16];
        for (var count = 0; at < length && count < 10_000; count++)
        {
            if (length - at < 8)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  ({length - at} B zvyšok na konci) @ {at}");
                return;
            }

            stream.Position = at;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Mp4Reader.TypeName(header.Slice(4, 4));
            if (size == 1 && length - at >= 16)
            {
                stream.ReadExactly(header.Slice(8, 8));
                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)), long.MaxValue);
            }
            else if (size == 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {type} @ {at}, do konca súboru ({length - at} B)");
                return;
            }

            var fits = size >= 8 && at + size <= length;
            text.AppendLine(CultureInfo.InvariantCulture, $"  {type} @ {at}, {size} B{(fits ? "" : $" – NESEDÍ (súbor má od tohto miesta {length - at} B)")}");
            if (!fits)
            {
                return;
            }

            at += size;
        }
    }

    private static void Tree(Mp4Box box, int depth, StringBuilder text)
    {
        foreach (var child in box.Children)
        {
            text.Append(' ', depth * 2).AppendLine(CultureInfo.InvariantCulture, $"{child.Type} @ {child.Start}, {child.Size} B");
            if (depth < 8)
            {
                Tree(child, depth + 1, text);
            }
        }
    }
}
