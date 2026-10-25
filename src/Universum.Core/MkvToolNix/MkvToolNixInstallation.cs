using Universum.Core.Processes;

namespace Universum.Core.MkvToolNix;

/// <summary>A usable MKVToolNix installation: folder with the command-line tools and their version.</summary>
public sealed record MkvToolNixInstallation(string Directory, Version Version)
{
    public string ToolPath(MkvToolNixTool tool) => Path.Combine(Directory, MkvToolNixToolNames.FileName(tool));

    public bool HasTool(MkvToolNixTool tool) => File.Exists(ToolPath(tool));

    public bool IsSupportedVersion => Version >= MkvToolNixVersion.Minimum;

    public override string ToString() => $"MKVToolNix {Version} ({Directory})";

    /// <summary>
    /// Checks that the folder contains working MKVToolNix command-line tools by running "mkvmerge --version".
    /// Returns null when the folder is not usable (missing tools, Microsoft Store edition, tool does not start).
    /// </summary>
    public static async Task<MkvToolNixInstallation?> OpenAsync(
        string directory, IProcessRunner runner, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || IsStoreEdition(directory))
        {
            return null;
        }

        var mkvmerge = Path.Combine(directory, MkvToolNixToolNames.FileName(MkvToolNixTool.MkvMerge));
        var mkvpropedit = Path.Combine(directory, MkvToolNixToolNames.FileName(MkvToolNixTool.MkvPropEdit));
        if (!File.Exists(mkvmerge) || !File.Exists(mkvpropedit))
        {
            return null;
        }

        try
        {
            var result = await runner.RunAsync(
                new ProcessRequest(mkvmerge, ["--version"]) { Timeout = TimeSpan.FromSeconds(20) },
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && MkvToolNixVersion.TryParseToolOutput(result.StandardOutput, out var version)
                ? new MkvToolNixInstallation(Path.GetFullPath(directory), version)
                : null;
        }
        catch (Exception ex) when (ex is ProcessStartException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Microsoft Store edition lives under "WindowsApps" and does not allow running the command-line tools.
    /// </summary>
    public static bool IsStoreEdition(string directory) =>
        directory.Replace('/', '\\').Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
}
