using Universum.Core.Processes;

namespace Universum.Core.MkvToolNix;

/// <summary>
/// Finds MKVToolNix in this order: folder chosen by the user, environment variable UNIVERSUM_MKVTOOLNIX_DIR,
/// platform sources (Windows registry), default installation folders and finally the PATH.
/// </summary>
public sealed class MkvToolNixLocator(IProcessRunner runner, IEnumerable<IMkvToolNixLocationSource>? sources = null)
{
    public const string EnvironmentVariable = "UNIVERSUM_MKVTOOLNIX_DIR";

    private readonly IReadOnlyList<IMkvToolNixLocationSource> _sources = sources?.ToList() ?? [];

    public async Task<MkvToolNixInstallation?> FindAsync(string? userDirectory = null, CancellationToken cancellationToken = default)
    {
        foreach (var directory in CandidateDirectories(userDirectory))
        {
            var installation = await MkvToolNixInstallation.OpenAsync(directory, runner, cancellationToken).ConfigureAwait(false);
            if (installation is not null)
            {
                return installation;
            }
        }

        return null;
    }

    /// <summary>All candidate folders in search order, without duplicates.</summary>
    public IEnumerable<string> CandidateDirectories(string? userDirectory = null)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var candidate in Unfiltered(userDirectory))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = candidate.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private IEnumerable<string?> Unfiltered(string? userDirectory)
    {
        yield return userDirectory;
        yield return Environment.GetEnvironmentVariable(EnvironmentVariable);
        foreach (var source in _sources)
        {
            foreach (var directory in source.CandidateDirectories())
            {
                yield return directory;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MKVToolNix");
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry;
        }
    }
}
