using System.Runtime.Versioning;
using Microsoft.Win32;
using Universum.Core.MkvToolNix;

namespace Universum.Platform.Windows;

/// <summary>Finds installed MKVToolNix through its uninstall entry in the Windows registry.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryMkvToolNixLocations : IMkvToolNixLocationSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IEnumerable<string> CandidateDirectories()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(UninstallKey);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is string displayName &&
                            displayName.StartsWith("MKVToolNix", StringComparison.OrdinalIgnoreCase) &&
                            DirectoryOf(entry) is { } directory)
                        {
                            result.Add(directory);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // an unreadable registry branch is skipped
                }
            }
        }

        return result;
    }

    private static string? DirectoryOf(RegistryKey entry)
    {
        if (entry.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
        {
            return location.Trim().Trim('"');
        }

        foreach (var valueName in new[] { "DisplayIcon", "UninstallString" })
        {
            if (entry.GetValue(valueName) is string value && UninstallEntry.ExecutablePath(value) is { } executable)
            {
                return Path.GetDirectoryName(executable);
            }
        }

        return null;
    }
}
