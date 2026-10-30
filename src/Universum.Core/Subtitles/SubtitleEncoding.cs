using System.Text;

namespace Universum.Core.Subtitles;

/// <summary>
/// The text of a subtitle file in any encoding: UTF-8 and UTF-16 (with or without a byte order mark), and – for older
/// Czech and Slovak subtitles – Windows-1250 or ISO-8859-2, told apart by which of them gives Czech and Slovak letters
/// (Š, š, Ž, ž, ľ, ť have other codes in each); Windows-1252 for western texts.
/// </summary>
public static class SubtitleEncoding
{
    private const string CzechAndSlovak = "áäčďéěíĺľňóôŕřšťúůýžÁÄČĎÉĚÍĹĽŇÓÔŔŘŠŤÚŮÝŽ";

    // characters a wrongly read Czech or Slovak text shows instead of its letters
    private const string Wrong = "©®±µ¶·¸¤¦¨°¹ºª«»¼½¾¿ąęłńśźĄĘŁŃŚŹ¥¼ŞşŢţ";

    static SubtitleEncoding() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The text and the name of the encoding it was read with.</summary>
    public static (string Text, string Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        }

        // UTF-16 without a mark: every other byte zero in a text of Latin letters
        if (bytes.Length >= 8 && bytes.Length % 2 == 0)
        {
            var oddZeros = 0;
            var evenZeros = 0;
            for (var i = 0; i < Math.Min(bytes.Length, 4096); i += 2)
            {
                evenZeros += bytes[i] == 0 ? 1 : 0;
                oddZeros += bytes[i + 1] == 0 ? 1 : 0;
            }

            var pairs = Math.Min(bytes.Length, 4096) / 2;
            if (oddZeros > pairs * 0.4 && evenZeros == 0)
            {
                return (System.Text.Encoding.Unicode.GetString(bytes), "UTF-16");
            }

            if (evenZeros > pairs * 0.4 && oddZeros == 0)
            {
                return (System.Text.Encoding.BigEndianUnicode.GetString(bytes), "UTF-16");
            }
        }

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // an 8-bit encoding: the one that reads as Czech or Slovak
        }

        (string Text, string Name, int Score)? best = null;
        foreach (var (page, name) in new[] { (1250, "Windows-1250"), (28592, "ISO-8859-2"), (1252, "Windows-1252") })
        {
            var text = System.Text.Encoding.GetEncoding(page).GetString(bytes);
            var score = 0;
            foreach (var c in text)
            {
                score += CzechAndSlovak.Contains(c) ? 2 : Wrong.Contains(c) || c is >= '\u0080' and <= '\u009F' ? -3 : 0;
            }

            if (best is null || score > best.Value.Score)
            {
                best = (text, name, score);
            }
        }

        return (best!.Value.Text, best.Value.Name);
    }
}
