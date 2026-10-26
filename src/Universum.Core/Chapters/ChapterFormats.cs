using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;

namespace Universum.Core.Chapters;

/// <summary>Matroska chapter XML (as mkvextract writes and mkvpropedit reads it) and the simple OGM text format.</summary>
public static class ChapterFormats
{
    /// <summary>Reads chapter XML; anything but one edition of flat chapters with one name each is marked complex.</summary>
    public static ChapterSet ParseXml(string xml)
    {
        var document = XDocument.Parse(xml.TrimStart('﻿'));
        var editions = document.Root?.Elements("EditionEntry").ToList() ?? [];
        var chapters = new List<Chapter>();
        var complex = editions.Count > 1;
        foreach (var atom in editions.FirstOrDefault()?.Elements("ChapterAtom") ?? [])
        {
            if (atom.Elements("ChapterAtom").Any() || atom.Elements("ChapterDisplay").Count() > 1 || atom.Element("ChapterTimeEnd") is not null ||
                atom.Element("ChapterFlagHidden")?.Value == "1" || atom.Element("ChapterFlagEnabled")?.Value == "0")
            {
                complex = true;
            }

            var display = atom.Element("ChapterDisplay");
            var ietf = display?.Element("ChapLanguageIETF")?.Value;
            var legacy = display?.Element("ChapterLanguage")?.Value;
            chapters.Add(new Chapter(
                ParseNanoseconds(atom.Element("ChapterTimeStart")?.Value),
                display?.Element("ChapterString")?.Value ?? "",
                !string.IsNullOrEmpty(ietf) ? LanguageTag.FromCode(ietf) : !string.IsNullOrEmpty(legacy) ? LanguageTag.FromCode(legacy) : LanguageTag.Undetermined));
        }

        return new ChapterSet(chapters, complex);
    }

    /// <summary>Chapter XML for mkvpropedit / mkvmerge: one edition, chapters sorted by their start.</summary>
    public static string WriteXml(IReadOnlyList<Chapter> chapters)
    {
        var edition = new XElement("EditionEntry");
        foreach (var chapter in chapters.OrderBy(c => c.Start))
        {
            var language = chapter.Language.IsUndetermined ? LanguageTag.Undetermined : chapter.Language;
            edition.Add(new XElement("ChapterAtom",
                new XElement("ChapterTimeStart", FormatNanoseconds(chapter.Start)),
                new XElement("ChapterDisplay",
                    new XElement("ChapterString", chapter.Name),
                    new XElement("ChapterLanguage", language.Legacy),
                    new XElement("ChapLanguageIETF", MkvToolNixArguments.Code(language)))));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Chapters", edition));
        // written through UTF-8 bytes, so the declaration says utf-8 (a string writer would declare utf-16)
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>OGM chapters: "CHAPTER01=00:00:00.000" and "CHAPTER01NAME=…".</summary>
    public static IReadOnlyList<Chapter> ParseOgm(string text, LanguageTag language)
    {
        var starts = new SortedDictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (!line.StartsWith("CHAPTER", StringComparison.OrdinalIgnoreCase) || equals < 0)
            {
                continue;
            }

            var key = line[7..equals];
            var value = line[(equals + 1)..];
            if (key.EndsWith("NAME", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[..^4], out var named))
            {
                names[named] = value;
            }
            else if (int.TryParse(key, out var number) && ChapterTime.Parse(value) is { } start)
            {
                starts[number] = start;
            }
        }

        return starts.Select(s => new Chapter(s.Value, names.GetValueOrDefault(s.Key, ""), language)).ToList();
    }

    private static TimeSpan ParseNanoseconds(string? text)
    {
        // "00:00:01.000000000" – the fraction can have up to nine digits
        if (text is null)
        {
            return TimeSpan.Zero;
        }

        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "0" : text[(dot + 1)..].PadRight(9, '0')[..9];
        var parts = whole.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var seconds = parts.Aggregate(0L, (total, part) => total * 60 + part);
        return TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond + long.Parse(fraction, CultureInfo.InvariantCulture) / 100);
    }

    private static string FormatNanoseconds(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Ticks % TimeSpan.TicksPerSecond * 100:000000000}";
}
