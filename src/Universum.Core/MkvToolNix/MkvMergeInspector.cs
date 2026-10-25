using Universum.Core.Media;
using Universum.Core.Processes;

namespace Universum.Core.MkvToolNix;

/// <summary>Reads the structure of a file with "mkvmerge --identify" in JSON format.</summary>
public sealed class MkvMergeInspector(MkvToolNixInstallation installation, IProcessRunner runner) : IMediaInspector
{
    /// <summary>Identification only reads headers; even very large files finish in seconds.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public string Name => $"mkvmerge {installation.Version}";

    public async Task<MediaFile> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The file does not exist.", fullPath);
        }

        using var options = MkvToolNixOptionsFile.Create(
            ["--ui-language", "en_US", "--identification-format", "json", "--identify", fullPath]);
        var result = await runner.RunAsync(
            new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvMerge), [options.Argument]) { Timeout = Timeout },
            cancellationToken).ConfigureAwait(false);

        // exit code 1 = warnings only; the JSON is still complete
        if (result.ExitCode is not (0 or 1) || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new MkvToolNixException($"mkvmerge could not identify '{fullPath}' (exit code {result.ExitCode}): {detail.Trim()}");
        }

        return MkvMergeIdentificationParser.Parse(result.StandardOutput, fullPath);
    }
}
