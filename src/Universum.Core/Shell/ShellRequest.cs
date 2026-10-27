namespace Universum.Core.Shell;

/// <summary>Where a file or folder from the Explorer's context menu (or the command line) goes.</summary>
public enum ShellAction
{
    /// <summary>"Universum.exe path": a folder goes to batch processing, a file to single processing (as before).</summary>
    Open,

    /// <summary>"--zlucovanie": the files are added to the multiplexer (several selected files together).</summary>
    Multiplexer,

    /// <summary>"--hromadne": a folder is analysed in batch processing; a file analyses its folder with its template.</summary>
    Batch,

    /// <summary>"--single": the file is opened in single processing (the UNIVERSUM rules for one file).</summary>
    Single,

    /// <summary>"--informacie": the file is shown on the Information page.</summary>
    Info,
}

/// <summary>
/// A request from the command line: the action and the paths. The Explorer starts one process per selected file; the
/// first UNIVERSUM receives the requests of the others (single instance) and handles files of one action together.
/// </summary>
public sealed record ShellRequest(ShellAction Action, IReadOnlyList<string> Paths)
{
    public const string MultiplexerSwitch = "--zlucovanie";
    public const string BatchSwitch = "--hromadne";
    public const string SingleSwitch = "--single";
    public const string InfoSwitch = "--informacie";

    /// <summary>The request of a command line; other switches (self-test, MKVToolNix setup) are not requests.</summary>
    public static ShellRequest Parse(IReadOnlyList<string> args)
    {
        var action = ShellAction.Open;
        foreach (var arg in args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            action = arg.ToLowerInvariant() switch
            {
                MultiplexerSwitch => ShellAction.Multiplexer,
                BatchSwitch => ShellAction.Batch,
                SingleSwitch => ShellAction.Single,
                InfoSwitch => ShellAction.Info,
                _ => action,
            };
        }

        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Select(Clean)
            .Where(p => p.Length > 0)
            .ToList();
        return new ShellRequest(action, paths);
    }

    /// <summary>
    /// Selected files in a stable order (the Explorer hands them over in any order): folders and video first, then
    /// audio, then subtitles, each by name – the film is the first input of the multiplexer, never its subtitles.
    /// </summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Rank)
            .ThenBy(p => Path.GetFileName(p.TrimEnd('\\', '/')), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mka", ".m4a", ".ogg", ".opus", ".flac", ".ac3", ".eac3", ".dts", ".aac", ".mp3", ".wav",
    };

    private static readonly HashSet<string> Subtitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mks", ".srt", ".ass", ".ssa", ".vtt", ".sup", ".idx", ".sub",
    };

    private static int Rank(string path)
    {
        var extension = Path.GetExtension(path);
        return Subtitles.Contains(extension) ? 2 : Audio.Contains(extension) ? 1 : 0;
    }

    // "C:\" arrives as C:" because of the backslash before the closing quote
    private static string Clean(string arg)
    {
        var path = arg.Trim().Trim('"');
        return path is { Length: 2 } && path[1] == ':' ? path + "\\" : path;
    }
}
