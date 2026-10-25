using System.Text.Json;
using Universum.Core.Rules;

namespace Universum.Cli;

/// <summary>The rules profile saved in the UNIVERSUM settings (%APPDATA%\UNIVERSUM\settings.json), if any.</summary>
internal static class SettingsProfile
{
    public static RuleProfile Load()
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
                if (document.RootElement.TryGetProperty("Profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
                {
                    return profile.Deserialize<RuleProfile>() ?? RuleProfile.Default;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nastavenia sa nepodarilo prečítať, platí predvolený profil: {ex.Message}");
        }

        return RuleProfile.Default;
    }
}
