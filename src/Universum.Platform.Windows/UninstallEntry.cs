namespace Universum.Platform.Windows;

/// <summary>Helpers for values of Windows uninstall registry entries.</summary>
public static class UninstallEntry
{
    /// <summary>Extracts the executable from values like <c>"C:\x\uninst.exe" /S</c> or <c>C:\x\a.exe,0</c>.</summary>
    public static string? ExecutablePath(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }
}
