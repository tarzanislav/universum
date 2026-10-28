namespace Universum.Core.Processes;

/// <summary>A program to run with its arguments. Arguments are passed one by one, never as a joined command line.</summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Called for each line written to standard output (e.g. mkvmerge progress lines).</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Extra environment variables for the process.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The process is killed when it runs longer; null means no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>A file given to the program on its standard input (then closed); null = no input.</summary>
    public string? StandardInputFile { get; init; }
}
