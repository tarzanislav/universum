using System.Globalization;

namespace Universum.Core.Text;

/// <summary>
/// The texts of the core in the language of UNIVERSUM's window (owner, 1. 11. 2026: Rozhranie CZ/EN, etapa 4). The Slovak
/// text is the key, as in the window: <c>CoreText.T("hotovo")</c>, <c>CoreText.F("{0} z {1} súborov", done, all)</c>. The app
/// sets the language and its dictionary at the start (<see cref="Use"/>); without it – the tests, the tools – the texts stay
/// Slovak.
/// </summary>
public static class CoreText
{
    private static Func<string, string> _translate = text => text;
    private static Func<string, bool> _own = _ => false;

    /// <summary>"sk", "cs" or "en".</summary>
    public static string Language { get; private set; } = "sk";

    /// <summary>The language of the window and its translation of a Slovak text (the Slovak one when it has none).</summary>
    /// <param name="own">Tells a text of UNIVERSUM's own in that language (a translation) from a tool's line.</param>
    public static void Use(string language, Func<string, string> translate, Func<string, bool>? own = null)
    {
        Language = language;
        _translate = translate;
        _own = own ?? (_ => false);
    }

    /// <summary>
    /// The text is UNIVERSUM's own sentence in the language of the window (a translation – not a line of mkvmerge or
    /// ffmpeg). A Slovak sentence is told by its letters; this is for Czech and English.
    /// </summary>
    public static bool IsOwn(string text) => _own(text);

    /// <summary>The text in the language of the window.</summary>
    public static string T(string slovak) => slovak.Length == 0 ? slovak : _translate(slovak);

    /// <summary>A text with values in the language of the window (the translation keeps the same {0}, {1}).</summary>
    public static string F(string slovakFormat, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, _translate(slovakFormat), values);
}
