using System.Windows.Markup;

namespace Universum.App.Localization;

/// <summary>
/// The language of UNIVERSUM's window (owner, 31. 10. 2026: Slovak by default, Czech and English chosen in Nastavenia,
/// applied after a restart). The Slovak text is the key: a text without a translation stays Slovak. Stage 1: the menu,
/// the titles of the pages, Nastavenia and the buttons; stage 2: all texts of the windows (XAML); stage 3: the texts
/// of the pages' code (messages, choices, states).
/// </summary>
public static class Loc
{
    /// <summary>The languages offered: code and name in that language.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("sk", "Slovenčina"), ("cs", "Čeština"), ("en", "English")];

    /// <summary>"sk", "cs" or "en" – set once at the start.</summary>
    public static string Language { get; private set; } = "sk";

    private static IReadOnlyDictionary<string, string>? _table;

    /// <summary>Sets the language of this run (an unknown code: Slovak).</summary>
    public static void Init(string? language)
    {
        Language = language is "cs" or "en" ? language : "sk";
        _table = Language switch
        {
            "cs" => Translations.Czech,
            "en" => Translations.English,
            _ => null,
        };
    }

    /// <summary>The text in the language of the window (the Slovak one when it has no translation).</summary>
    public static string T(string slovak) => _table is not null && _table.TryGetValue(slovak, out var text) ? text : slovak;

    /// <summary>
    /// A text with values in the language of the window: the Slovak format is the key –
    /// <c>Loc.F("{0} je vo Fronte úloh (úloha {1}).", batch, id)</c>; the translation keeps the same {0}, {1}.
    /// </summary>
    public static string F(string slovakFormat, params object?[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(slovakFormat), values);
}

/// <summary>A text of the window in its language: <c>Content="{loc:T 'Uložiť'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
