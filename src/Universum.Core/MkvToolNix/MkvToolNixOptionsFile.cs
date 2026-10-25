using System.Text;
using System.Text.Json;

namespace Universum.Core.MkvToolNix;

/// <summary>
/// Temporary JSON option file ("mkvmerge @file.json"). Passing arguments through a UTF-8 file avoids any
/// command-line quoting or code page problems with unusual file names. The file is deleted on dispose.
/// </summary>
public sealed class MkvToolNixOptionsFile : IDisposable
{
    private MkvToolNixOptionsFile(string path) => Path = path;

    public string Path { get; }

    /// <summary>The argument that refers to this file: "@path".</summary>
    public string Argument => "@" + Path;

    public static MkvToolNixOptionsFile Create(IEnumerable<string> arguments)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UNIVERSUM");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"options-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(arguments.ToArray()), new UTF8Encoding(false));
        return new MkvToolNixOptionsFile(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover temporary file is harmless
        }
    }
}
