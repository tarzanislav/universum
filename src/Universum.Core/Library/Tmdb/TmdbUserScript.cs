using System.Globalization;
using Universum.Core.Text;

namespace Universum.Core.Library.Tmdb;

/// <summary>
/// UNIVERSUM's script for the browser (owner, 8. 11. 2026; Tampermonkey or Violentmonkey run it; 1.3 – ČSFD and posters; 1.4 – what a
/// page of ČSFD says of its title, for a poster drawn by AI when the title is not on TMDB): on a page of a film or
/// series of TMDB it sends the page to the Zásobník TMDB by itself (UNIVERSUM takes it only while its automatic gathering is
/// switched on – 1.1), and shows the button "Do UNIVERSUM" (when UNIVERSUM
/// does not answer, the button starts it by the address universum://). It runs only on TMDB's film and series pages and
/// talks only to 127.0.0.1 with the key of this installation.
/// </summary>
public static class TmdbUserScript
{
    public const string Version = "1.4";

    /// <summary>
    /// The script with the port, the key and the words of this installation. Its source is universum.user.js beside this
    /// file (14. 11. 2026, step 2 of the review of the code: a file of its own, checked as JavaScript by the tests); the
    /// names __UNIVERSUM_…__ and __T_…__ in it are put in here.
    /// </summary>
    public static string Text(int port, string key, bool automatic = true) =>
        new System.Text.StringBuilder(Source.Value)
            .Replace("__UNIVERSUM_VERSION__", Version)
            .Replace("__UNIVERSUM_PORT__", port.ToString(CultureInfo.InvariantCulture))
            .Replace("__UNIVERSUM_KEY__", key)
            .Replace("__UNIVERSUM_AUTOMATIC__", automatic ? "true" : "false")
            .Replace("__T_BUTTON__", Js(CoreText.T("＋ Do UNIVERSUM")))
            .Replace("__T_ADDED__", Js(CoreText.T("✓ V zásobníku UNIVERSUM")))
            .Replace("__T_REFUSED__", Js(CoreText.T("UNIVERSUM to neprijal")))
            .Replace("__T_OPENING__", Js(CoreText.T("Otváram UNIVERSUM…")))
            .Replace("__T_SILENT__", Js(CoreText.T("UNIVERSUM neodpovedá")))
            .Replace("__T_POSTER__", Js(CoreText.T("＋ plagát do UNIVERSUM")))
            .Replace("__T_POSTERSENT__", Js(CoreText.T("✓ plagát poslaný")))
            .Replace("__T_SENDING__", Js(CoreText.T("posielam…")))
            .ToString();

    /// <summary>The source of the script (universum.user.js, built into the core).</summary>
    internal static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(TmdbUserScript).Assembly.GetManifestResourceStream("Universum.Core.Library.Tmdb.universum.user.js")
            ?? throw new InvalidOperationException("universum.user.js is not built into the core");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    });

    // a JavaScript string of a text (quotes and backslashes escaped)
    private static string Js(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>The button for the bookmarks bar ("Do UNIVERSUM"): it hands the page to UNIVERSUM by the address universum://.</summary>
    public const string Bookmarklet =
        "javascript:location.href='universum://tmdb?u='+encodeURIComponent(location.href)+'&t='+encodeURIComponent(document.title);void 0";
}
