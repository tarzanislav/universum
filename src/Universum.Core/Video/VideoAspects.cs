namespace Universum.Core.Video;

/// <summary>
/// The proportions a picture can be shown at without writing it anew (Úprava videostopy: Pomer strán): the value Matroska
/// takes ("16/9") and its name ("16:9"). One list for the studio and for the edits it saves into a task.
/// </summary>
public static class VideoAspects
{
    public static IReadOnlyList<(string Value, string Name)> All { get; } =
    [
        ("16/9", "16:9"), ("4/3", "4:3"), ("37/20", "1,85:1"), ("47/20", "2,35:1"), ("12/5", "2,40:1"), ("64/27", "21:9"), ("1/1", "1:1"),
    ];

    /// <summary>"16/9" → "16:9", "37/20" → "1,85:1"; another value with a colon.</summary>
    public static string NameOf(string value) => All.FirstOrDefault(a => a.Value == value).Name ?? value.Replace('/', ':');

    /// <summary>The proportions of the picture turned by 90°: "16/9" → "9/16".</summary>
    public static string Upright(string value) => value.Split('/') is [var width, var height] ? $"{height}/{width}" : value;
}
