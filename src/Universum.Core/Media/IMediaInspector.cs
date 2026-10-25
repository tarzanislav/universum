namespace Universum.Core.Media;

/// <summary>
/// Reads the structure of a media file. Each backend (MKVToolNix now, ffprobe later) provides its own
/// implementation; the rest of UNIVERSUM works only with <see cref="MediaFile"/>.
/// </summary>
public interface IMediaInspector
{
    /// <summary>Short backend name for logs, e.g. "mkvmerge 103.0".</summary>
    string Name { get; }

    Task<MediaFile> InspectAsync(string path, CancellationToken cancellationToken = default);
}
