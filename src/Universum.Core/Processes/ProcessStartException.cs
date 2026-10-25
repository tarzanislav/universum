namespace Universum.Core.Processes;

public sealed class ProcessStartException(string fileName, Exception inner)
    : Exception($"Program '{fileName}' could not be started: {inner.Message}", inner)
{
    public string FileName { get; } = fileName;
}
