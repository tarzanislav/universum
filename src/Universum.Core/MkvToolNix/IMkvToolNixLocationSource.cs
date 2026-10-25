namespace Universum.Core.MkvToolNix;

/// <summary>Supplies candidate MKVToolNix folders, e.g. from the Windows registry.</summary>
public interface IMkvToolNixLocationSource
{
    IEnumerable<string> CandidateDirectories();
}
