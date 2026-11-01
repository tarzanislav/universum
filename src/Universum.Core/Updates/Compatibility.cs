using System.Text.Json;
using Universum.Core.Text;

namespace Universum.Core.Updates;

/// <summary>
/// Result of the weekly compatibility run in CI: UNIVERSUM tested with the newest MKVToolNix. Published as
/// compatibility.json in the branch "compatibility" of the repository.
/// </summary>
public sealed record Compatibility(Version MkvToolNix, string Universum, DateTime TestedUtc, bool Passed)
{
    public static Uri Address { get; } = new($"https://raw.githubusercontent.com/{UniversumRelease.Repository}/compatibility/compatibility.json");

    public static Compatibility? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return Version.TryParse(root.GetProperty("mkvtoolnix").GetString(), out var mkv)
                ? new Compatibility(mkv, root.GetProperty("universum").GetString() ?? "", root.GetProperty("tested").GetDateTime().ToUniversalTime(), root.GetProperty("passed").GetBoolean())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        mkvtoolnix = MkvToolNix.ToString(),
        universum = Universum,
        tested = TestedUtc.ToString("O"),
        passed = Passed,
    });

    /// <summary>Text for the update window: verified with this MKVToolNix version, or not (yet).</summary>
    public static string Describe(Compatibility? report, Version latest) => report switch
    {
        { Passed: true } r when r.MkvToolNix == latest => CoreText.F("overené s UNIVERSUM ({0:d. M. yyyy})", r.TestedUtc),
        { Passed: false } r when r.MkvToolNix == latest => CoreText.T("test s UNIVERSUM ZLYHAL – odporúčame počkať na opravu"),
        _ => CoreText.T("zatiaľ neoverené s UNIVERSUM"),
    };
}
