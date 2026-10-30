using System.Globalization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;

namespace Universum.Core.Subtitles.Pictures;

/// <summary>Picture subtitles for the player: PGS of a track of an MKV file (mkvextract writes it as ".sup") or a ".sup" file.</summary>
public static class PictureSubtitles
{
    /// <summary>The track can be shown by UNIVERSUM (PGS; VobSub not yet).</summary>
    public static bool CanShow(MediaTrack track) =>
        track.CodecId == "S_HDMV/PGS" || (track.ExternalFile is { } file && Path.GetExtension(file).Equals(".sup", StringComparison.OrdinalIgnoreCase));

    /// <summary>The PGS subtitles of a track (mkvextract reads the whole file); empty when it cannot be read.</summary>
    public static async Task<IReadOnlyList<PictureCue>> ExtractAsync(
        MkvToolNixInstallation installation, IProcessRunner runner, MediaFile file, MediaTrack track, CancellationToken cancellationToken = default)
    {
        if (track.ExternalFile is { } external)
        {
            return File.Exists(external) ? await PgsReader.ReadFileAsync(external, cancellationToken).ConfigureAwait(false) : [];
        }

        if (track.CodecId != "S_HDMV/PGS" || !file.IsMatroska)
        {
            return [];
        }

        var target = Path.Combine(Path.GetTempPath(), "UNIVERSUM", $"pgs-{Guid.NewGuid():N}.sup");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var options = MkvToolNixOptionsFile.Create([file.Path, "tracks", track.Id.ToString(CultureInfo.InvariantCulture) + ":" + target]);
            var result = await runner.RunAsync(
                new ProcessRequest(installation.ToolPath(MkvToolNixTool.MkvExtract), [options.Argument]) { Timeout = TimeSpan.FromMinutes(30) },
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode is 0 or 1 && File.Exists(target) ? await PgsReader.ReadFileAsync(target, cancellationToken).ConfigureAwait(false) : [];
        }
        finally
        {
            try
            {
                File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a temporary file left behind is harmless
            }
        }
    }
}
