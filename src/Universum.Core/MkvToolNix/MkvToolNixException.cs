namespace Universum.Core.MkvToolNix;

/// <summary>An MKVToolNix tool failed or returned data UNIVERSUM cannot use.</summary>
public sealed class MkvToolNixException : Exception
{
    public MkvToolNixException(string message)
        : base(message)
    {
    }

    public MkvToolNixException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
