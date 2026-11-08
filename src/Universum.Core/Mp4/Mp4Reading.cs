using Universum.Core.Media;

namespace Universum.Core.Mp4;

/// <summary>
/// What MKVToolNix does not read of an MP4, added to what it read (owner, 8. 11. 2026): the names of the tracks ('udta/name',
/// or the handler's name that is not the program's usual one), their roles (forced, hearing impaired, commentary, visual
/// impaired), the title (also QuickTime's keyed one), and whether UNIVERSUM can edit the file's header in place – so a
/// file is planned as it really is, and a file processed once is not changed again.
/// </summary>
public static class Mp4Reading
{
    public const string Fragmented = "fragmented";

    public const string BadEnd = "end";

    public const string Unreadable = "unreadable";

    public static MediaFile Complete(MediaFile file)
    {
        Mp4File mp4;
        try
        {
            mp4 = Mp4Reader.Read(file.Path);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return file with { Mp4Problem = Unreadable };
        }

        var tracks = mp4.Tracks.ToDictionary(t => t.Id);
        return file with
        {
            Title = mp4.Title ?? file.Title,
            Mp4Problem = mp4.IsFragmented ? Fragmented : Mp4HeaderEditor.CannotEdit(mp4) is not null ? BadEnd : null,
            Tracks =
            [
                .. file.Tracks.Select(t => t.Number is { } id && tracks.TryGetValue(id, out var track)
                    ? t with { Name = t.Name ?? track.DisplayName, Flags = Mp4Roles.Apply(t.Flags, track.Roles) }
                    : t),
            ],
        };
    }

    /// <summary>Why the MP4 cannot stay MP4, in words (null: it can).</summary>
    public static string? Problem(string? code) => code switch
    {
        null => null,
        Fragmented => Text.CoreText.T("fragmentované MP4 – UNIVERSUM ho na mieste neupraví"),
        BadEnd => Text.CoreText.T("MP4 nekončí celým boxom – UNIVERSUM ho na mieste neupraví"),
        _ => Text.CoreText.T("UNIVERSUM toto MP4 nevie prečítať"),
    };
}
