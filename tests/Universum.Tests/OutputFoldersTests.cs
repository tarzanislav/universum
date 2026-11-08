using Universum.Core.Library;

namespace Universum.Tests;

/// <summary>
/// The checks of output folders (owner, 8. 11. 2026: a source folder was added as an output, the default output replaced
/// without a question). Invented folders only.
/// </summary>
public sealed class OutputFoldersTests
{
    [Test]
    public void TheInputFolderIsNoOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "universum-vystupy-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "ZDROJ");
        Assert.True(OutputFolders.Problem(input, [input]) is not null, "the input folder itself");
        Assert.True(OutputFolders.Problem(Path.Combine(input, "VYSTUP"), [input]) is not null, "a folder in the input folder");
        Assert.True(OutputFolders.Problem(root, [input]) is not null, "a folder holding the input folder");
        Assert.True(OutputFolders.Problem(Path.Combine(root, "VYSTUP"), [input]) is null && OutputFolders.Problem(Path.Combine(root, "ZDROJ2"), [input]) is null, "a folder beside it");
        Assert.True(OutputFolders.Problem(Path.Combine(root, "VYSTUP"), []) is null, "no input folder chosen");
    }

    [Test]
    public void AFolderOfFilmsLooksLikeASource()
    {
        var root = Path.Combine(Path.GetTempPath(), "universum-vystupy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "VYSTUP");
            Directory.CreateDirectory(Path.Combine(library, "MOVIES", "Vymyslený film (2026)"));
            File.WriteAllBytes(Path.Combine(library, "MOVIES", "Vymyslený film (2026)", "Vymyslený film (2026).mkv"), [1]);
            File.WriteAllText(Path.Combine(library, "poznamka.txt"), "x");
            Assert.True(!OutputFolders.LooksLikeSource(library), "a library: films only in MOVIES");
            var source = Path.Combine(root, "ZDROJ");
            Directory.CreateDirectory(Path.Combine(source, "Vymyslený seriál"));
            File.WriteAllBytes(Path.Combine(source, "Vymyslený seriál", "Vymyslený seriál S01E01.mp4"), [1]);
            Assert.True(OutputFolders.LooksLikeSource(source), "a folder with films of its own");
            Assert.True(!OutputFolders.LooksLikeSource(Path.Combine(root, "nie-je")), "a folder that is not there");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void FoldersByDisk()
    {
        var root = Path.GetTempPath();
        var rows = OutputFolders.Rows([Path.Combine(root, "ZDROJ"), Path.Combine(root, "ZDROJ2")], Path.Combine(root, "VYSTUP"), [Path.Combine(root, "INY")]);
        Assert.True(rows is [{ IsMain: true } row] && row.Input!.EndsWith("ZDROJ", StringComparison.Ordinal) && row.Output!.EndsWith("VYSTUP", StringComparison.Ordinal) &&
                    row.ExtraInputs.Count == 1 && row.ExtraOutputs.Count == 1, "one disk: its input and output side by side, the extra ones told");
        Assert.True(OutputFolders.Rows([], null, []).Count == 0, "no folders, no rows");
        Assert.Equal(Path.Combine(root, "UNIVERSUM", "VYSTUP"), OutputFolders.SuggestedOutput(Path.Combine(root, "UNIVERSUM", "ZDROJ")), "the output beside the input");
        Assert.True(OutputFolders.InputProblem(Path.Combine(root, "VYSTUP", "MOVIES"), [Path.Combine(root, "VYSTUP")]) is not null &&
                    OutputFolders.InputProblem(Path.Combine(root, "ZDROJ"), [Path.Combine(root, "VYSTUP")]) is null, "an input is never in an output");
    }

    [Test]
    public void OutputsSharingADisk()
    {
        var root = Path.GetTempPath();
        Assert.Equal(2, OutputFolders.SharingADisk(Path.Combine(root, "VYSTUP"), [Path.Combine(root, "ZDROJ")]).Count, "two on one disk");
        Assert.Equal(0, OutputFolders.SharingADisk(Path.Combine(root, "VYSTUP"), []).Count, "one only");
    }
}
