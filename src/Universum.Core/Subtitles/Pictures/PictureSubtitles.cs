using System.Globalization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Text;

namespace Universum.Core.Subtitles.Pictures;

/// <summary>
/// Picture subtitles for the player and the reading of text: PGS (Blu-ray) and VobSub (DVD; owner, 1. 11. 2026) of a track
/// of an MKV file (mkvextract writes PGS as ".sup", VobSub as ".idx" with ".sub") or of a ".sup" / ".idx" file.
/// </summary>
public static class PictureSubtitles
{
    private const string Pgs = "S_HDMV/PGS";
    private const string VobSub = "S_VOBSUB";

    /// <summary>The track can be shown and read by UNIVERSUM (PGS and VobSub).</summary>
    public static bool CanShow(MediaTrack track) =>
        track.ExternalFile is { } file
            ? Path.GetExtension(file).ToLowerInvariant() is ".sup" or ".idx"
            : track.CodecId is Pgs or VobSub;

    /// <summary>The subtitles of a ".sup" (PGS) or ".idx" / ".sub" (VobSub) file; <see cref="InvalidDataException"/> for another one.</summary>
    public static Task<IReadOnlyList<PictureCue>> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".sup" => PgsReader.ReadFileAsync(path, cancellationToken),
            ".idx" or ".sub" => VobSubReader.ReadFilesAsync(Path.ChangeExtension(path, ".idx"), null, cancellationToken),
            _ => throw new InvalidDataException(CoreText.F("{0}: nie sú to obrázkové titulky PGS ani VobSub.", Path.GetFileName(path))),
        };

    /// <summary>The picture subtitles of a track (mkvextract reads the whole file); empty when they cannot be read.</summary>
    public static async Task<IReadOnlyList<PictureCue>> ExtractAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, MediaFile file, MediaTrack track, CancellationToken cancellationToken = default)
    {
        if (track.ExternalFile is { } external)
        {
            return File.Exists(external) ? await ReadFileAsync(external, cancellationToken).ConfigureAwait(false) : [];
        }

        if (track.CodecId is not (Pgs or VobSub) || !file.IsMatroska)
        {
            return [];
        }

        // mkvextract writes a VobSub track as the ".idx" named and a ".sub" beside it
        var vobSub = track.CodecId == VobSub;
        var target = Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"pictures-{Guid.NewGuid():N}{(vobSub ? ".idx" : ".sup")}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var options = MkvToolNixOptionsFile.Create([file.Path, "tracks", track.Id.ToString(CultureInfo.InvariantCulture) + ":" + target]);
            var result = await runner.RunAsync(
                new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvExtract), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode is 0 or 1 && File.Exists(target) ? await ReadFileAsync(target, cancellationToken).ConfigureAwait(false) : [];
        }
        catch (InvalidDataException)
        {
            return [];
        }
        finally
        {
            foreach (var written in vobSub ? new[] { target, Path.ChangeExtension(target, ".sub") } : [target])
            {
                try
                {
                    File.Delete(written);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // a temporary file left behind is harmless
                }
            }
        }
    }
}
