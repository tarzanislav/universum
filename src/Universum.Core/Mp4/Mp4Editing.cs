using Universum.Core.Processes;

namespace Universum.Core.Mp4;

/// <summary>
/// Ponechať MP4 (owner, 7. 11. 2026): the header of an MP4 changed by UNIVERSUM's own code – the picture and sound
/// untouched – and checked twice: by UNIVERSUM's own reader (every value asked for, the samples the same) and,
/// independently, by ffprobe. What went wrong is written down in a záznam MP4 without names. Used by the processing of
/// Single, Hromadné and MEGA, by Editácia hlavičky and by Zlučovanie.
/// </summary>
public static class Mp4Editing
{
    /// <summary>The message naming the záznam MP4 written for the developer.</summary>
    public const string RecordPrefix = "the MP4 record: ";

    /// <summary>The header could not be changed (the file as it was).</summary>
    public const string CannotEditPrefix = "the MP4 header cannot be edited: ";

    /// <summary>Zlučovanie: the MP4 stayed MP4, only its header changed.</summary>
    public const string KeptMessage = "the MP4 stayed MP4, only its header was edited";

    /// <summary>ffprobe saw the result as planned.</summary>
    public const string ProbedMessage = "the MP4 checked by ffprobe too";

    /// <summary>
    /// Changes the header of <paramref name="path"/> and checks it against <paramref name="original"/> (or the file as it
    /// was). Problems as messages ("verification: …", the record) – empty when all is as planned – and whether ffprobe saw
    /// the result as planned. <paramref name="findFfprobe"/> gives ffprobe, or null when it is not at hand.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Problems, bool Probed)> ApplyCheckedAsync(
        string path, string? original, Mp4Change change, IProcessRunner runner, Func<Task<string?>>? findFfprobe, string? recordFolder, string version)
    {
        try
        {
            var before = await Task.Run(() => Mp4Reader.Read(original ?? path)).ConfigureAwait(false);
            await Task.Run(() => Mp4HeaderEditor.Apply(path, change)).ConfigureAwait(false);
            var after = await Task.Run(() => Mp4Reader.Read(path)).ConfigureAwait(false);
            var problems = Mp4Targets.Verify(change, before, after).ToList();
            var probed = false;
            if (problems.Count == 0 && findFfprobe is not null)
            {
                try
                {
                    if (await findFfprobe().ConfigureAwait(false) is { } ffprobe && File.Exists(ffprobe) &&
                        await Mp4Targets.ProbeAsync(runner, ffprobe, path, change, after).ConfigureAwait(false) is { } seen)
                    {
                        problems.AddRange(seen);
                        probed = seen.Count == 0;
                    }
                }
                catch (Exception ex) when (ex is IOException or ProcessStartException or TimeoutException or InvalidOperationException or Speech.SpeechException)
                {
                    // ffprobe is the second opinion; without it UNIVERSUM's own check stands
                }
            }

            return (problems.Count > 0 ? [.. problems.Select(p => "verification: " + p), .. Record(path, string.Join("; ", problems), recordFolder, version)] : [], probed);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return ([CannotEditPrefix + ex.Message, .. Record(path, ex.Message, recordFolder, version)], false);
        }
    }

    // a záznam MP4 for the developer (no names in it); the message naming it, or none
    private static IEnumerable<string> Record(string path, string problem, string? folder, string version)
    {
        if (folder is null)
        {
            yield break;
        }

        string? saved = null;
        try
        {
            saved = Mp4Record.Save(folder, Mp4Record.Describe(path, problem, version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the record is a help, not a condition
        }

        if (saved is not null)
        {
            yield return RecordPrefix + saved;
        }
    }
}
