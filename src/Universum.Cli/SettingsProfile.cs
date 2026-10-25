using System.Text.Json;
using Universum.Core.Rules;

namespace Universum.Cli;

/// <summary>Values saved in the UNIVERSUM settings (%APPDATA%\UNIVERSUM\settings.json), if any.</summary>
internal static class SettingsProfile
{
    /// <summary>The rules profile; the default one when none is saved.</summary>
    public static RuleProfile Load() =>
        Read("Profile", e => e.ValueKind == JsonValueKind.Object ? e.Deserialize<RuleProfile>() : null) ?? RuleProfile.Default;

    /// <summary>The output folder chosen in the application.</summary>
    public static string? OutputDirectory() =>
        Read("OutputDirectory", e => e.ValueKind == JsonValueKind.String ? e.GetString() : null);

    private static T? Read<T>(string property, Func<JsonElement, T?> convert)
        where T : class
    {
        var folder = Environment.GetEnvironmentVariable("UNIVERSUM_SETTINGS_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UNIVERSUM");
        try
        {
            var path = Path.Combine(folder, "settings.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty(property, out var value))
                {
                    return convert(value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nastavenia sa nepodarilo prečítať: {ex.Message}");
        }

        return null;
    }
}
