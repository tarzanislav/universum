using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.Tests;

/// <summary>Access to the repository's testdata folder and to an installed MKVToolNix.</summary>
internal static class TestData
{
    private static readonly Lazy<Task<MkvToolNixInstallation?>> Installation = new(() => new MkvToolNixLocator(new ProcessRunner()).FindAsync());

    public static string Root { get; } = FindRoot();

    public static string Media(string fileName) => Path.Combine(Root, "media", fileName);

    public static string Text(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    /// <summary>The installed MKVToolNix (UNIVERSUM_MKVTOOLNIX_DIR or PATH); skips the test when there is none.</summary>
    public static async Task<MkvToolNixInstallation> RequireMkvToolNixAsync()
    {
        var installation = await Installation.Value;
        if (installation is null)
        {
            Assert.Skip("MKVToolNix not found (set UNIVERSUM_MKVTOOLNIX_DIR)");
        }

        return installation!;
    }

    private static string FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("UNIVERSUM_TESTDATA");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "testdata");
            if (Directory.Exists(Path.Combine(candidate, "media")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("testdata folder not found (set UNIVERSUM_TESTDATA)");
    }
}
