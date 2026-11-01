using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Jobs;
using Universum.Core.Library;
using Universum.Core.Media;
using Universum.Core.Muxing;
using Universum.Core.Planning;
using Universum.Core.Rules;
using Universum.Core.Templates;

namespace Universum.Core.Mega;

/// <summary>
/// A MEGA batch as a job of the job queue. Right before writing, every file is read again: a file that is missing,
/// changed (size, time of the last change, tracks) or whose plan no longer holds is skipped and marked – never written
/// with decisions made for another file. The rest is written by the <see cref="BatchRunner"/> as in Hromadné spracovanie;
/// the files of Single spracovanie keep their originals; the results of Zlučovanie are written by the multiplexer from
/// their recipes when every file they read is as it was.
/// </summary>
/// <param name="inspect">Reads a file (mkvmerge identification).</param>
/// <param name="changed">Called after a file's state changed (the page saves the list).</param>
/// <param name="keeping">The processor that never deletes an original (files of Single spracovanie); null: <paramref name="processor"/>.</param>
/// <param name="mux">The multiplexer (results of Zlučovanie); null: they are skipped.</param>
public sealed class MegaJob(
    MegaBatch batch, FileProcessor processor, Func<string, CancellationToken, Task<MediaFile>> inspect, Action<MegaBatch>? changed = null,
    FileProcessor? keeping = null, Func<MuxSpec, IProgress<double>?, CancellationToken, Task<MuxResult>>? mux = null)
    : Job($"MEGA {batch.Label}: {batch.Name}"), ILoggedJob
{
    private readonly List<LoggedFile> _logged = [];

    public MegaBatch Batch { get; } = batch;

    public string LogKind => $"MEGA spracovanie – {Batch.Label}";

    public IReadOnlyList<LoggedFile> LoggedFiles
    {
        get
        {
            lock (_logged)
            {
                return [.. _logged];
            }
        }
    }

    private void Log(LoggedFile file)
    {
        lock (_logged)
        {
            _logged.Add(file);
        }
    }

    /// <summary>The file and its prepared item, or why it cannot be written now (null item).</summary>
    /// <param name="index">The library index: a result made from this original after the batch was prepared is never replaced.</param>
    public static async Task<(BatchItem? Item, string Problem)> CheckAsync(
        MegaFile file, MegaBatch batch, Func<string, CancellationToken, Task<MediaFile>> inspect, CancellationToken cancellationToken,
        LibraryIndex? index = null)
    {
        if (!File.Exists(file.Path))
        {
            return (null, "súbor chýba");
        }

        var info = new FileInfo(file.Path);
        if (info.Length != file.Size || info.LastWriteTimeUtc != file.ModifiedUtc)
        {
            return (null, "súbor sa medzičasom zmenil (veľkosť alebo čas zmeny)");
        }

        MediaFile media;
        try
        {
            media = await inspect(file.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "súbor sa nedá prečítať: " + ex.Message);
        }

        // the same checks as a run of Hromadné spracovanie: the file read now against the file that was analysed
        if (FileProcessor.StructureOf(media) != file.Structure)
        {
            return (null, "súbor sa medzičasom zmenil (stopy, názvy, príznaky alebo tagy) – je to iný súbor, než ktorý sa pripravil");
        }

        // external subtitle files of the video: only as they were when the batch was made
        if (file.Externals.FirstOrDefault(e => !e.Unchanged) is { } changed)
        {
            return (null, $"pridaný súbor {System.IO.Path.GetFileName(changed.Path)} (titulky alebo zvuk) sa medzičasom zmenil alebo chýba – treba ho znova pripraviť v Hromadnom spracovaní");
        }

        media = media with { Tracks = [.. media.Tracks, .. file.Externals.Select(e => e.ToTrack())] };
        var profile = file.Profile ?? batch.Profile;
        var target = UniversumRules.Apply(media, profile, file.Decisions());
        if (target.IsBlocked)
        {
            return (null, "pravidlá ho blokujú – treba ho znova pripraviť v Hromadnom spracovaní");
        }

        var placement = new LibraryLayout(batch.LibraryRoot).Place(target, file.Title, file.Episode, profile).WithAddition(file.Addition);
        var group = new BatchGroup("MEGA", 0);
        group.Files.Add(media);
        var item = new BatchItem(media, group, file.Decisions(), target, EditPlan.For(target) with { Revision = file.Revision }, new TemplateMatch([], [], []),
            new Dictionary<SlotKey, LanguageEvidence>(), [])
        {
            Placement = placement,
            Title = file.Title,
            Episode = file.Episode,
        };
        if (!item.NeedsWork)
        {
            return (null, AlreadyInLibrary);
        }

        // processed meanwhile (Hromadné spracovanie, another batch): the same result is there, or one made with newer
        // decisions – the batch's older decisions never replace it
        if (index is not null && index.MadeSince(placement, await LibraryIndex.FingerprintAsync(file.Path, cancellationToken).ConfigureAwait(false), batch.CreatedUtc) is { } made)
        {
            return made == LibraryIndex.TargetSignature(target)
                ? (null, AlreadyInLibrary)
                : (null, "medzičasom sa spracoval inak (napr. v Hromadnom spracovaní) – dávka by novší výsledok prepísala staršími rozhodnutiami");
        }

        return (item, "");
    }

    /// <summary>The reason of a file whose result is already in its place.</summary>
    public const string AlreadyInLibrary = "už je v knižnici";

    protected override async Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
    {
        // the check reads files (also from a cache): off the thread that added the job (the window's)
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var waiting = Batch.Files.Where(f => f.State == MegaFileState.Waiting).ToList();
        context.Log(JobSeverity.Info, $"MEGA {Batch.Label}: {waiting.Count} súborov – kontrola pred spustením.");
        var items = new List<(BatchItem Item, MegaFile File)>();
        var skipped = 0;
        foreach (var file in waiting.Where(f => f.Mux is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, problem) = await CheckAsync(file, Batch, inspect, cancellationToken, processor.Index).ConfigureAwait(false);
            if (item is null)
            {
                skipped++;
                file.State = problem == AlreadyInLibrary ? MegaFileState.AlreadyInLibrary : MegaFileState.Skipped;
                file.Note = problem;
                Log(ProcessingWords.Skipped(file.Path, problem, file.State == MegaFileState.AlreadyInLibrary));
                context.Log(file.State == MegaFileState.AlreadyInLibrary ? JobSeverity.Info : JobSeverity.Warning,
                    $"{(file.State == MegaFileState.AlreadyInLibrary ? "Už v knižnici" : "PRESKOČENÝ")}: {Path.GetFileName(file.Path)} – {problem}");
                changed?.Invoke(Batch);
            }
            else
            {
                items.Add((item, file));
            }
        }

        var byPath = items.ToDictionary(x => x.Item.File.Path, x => x.File, StringComparer.Ordinal);
        var results = new List<ProcessingResult>();
        var muxes = waiting.Where(f => f.Mux is not null).ToList();
        var total = Math.Max(1, items.Count + muxes.Count);
        var finished = 0;
        if (items.Count > 0)
        {
            new LibraryLayout(Batch.LibraryRoot).EnsureCreated();
        }

        // the files of Hromadné spracovanie as the batch says; those of Single spracovanie always keep their originals
        foreach (var (run, keep) in new[] { (items.Where(x => !x.File.KeepOriginal).ToList(), false), (items.Where(x => x.File.KeepOriginal).ToList(), true) })
        {
            if (run.Count == 0)
            {
                continue;
            }

            var deletes = !keep && Batch.Originals == OriginalsMode.DeleteAutomatically;
            var start = (double)finished / total;
            results.AddRange(await BatchRunner.RunAsync(
                run.Select(x => x.Item).ToList(), keep ? keeping ?? processor : processor, new OutputOptions(deletes ? OriginalHandling.Delete : OriginalHandling.Keep, MoveIntoLibrary: false),
                new Reporter(context, start, (double)run.Count / total, (item, result) =>
                {
                    var file = byPath[item.File.Path];
                    file.State = result.Status switch
                    {
                        ProcessingStatus.Done or ProcessingStatus.Unchanged => MegaFileState.Done,
                        ProcessingStatus.AlreadyInLibrary => MegaFileState.AlreadyInLibrary,
                        ProcessingStatus.Cancelled => MegaFileState.Cancelled,
                        _ => MegaFileState.Failed,
                    };
                    file.Note = result.OutputPath ?? string.Join(" | ", result.Messages);
                    file.ResultPath = result.Status is ProcessingStatus.Done or ProcessingStatus.AlreadyInLibrary ? result.OutputPath : null;
                    file.OriginalRemoved = result.Messages.Contains(FileProcessor.OriginalRecycledMessage);
                    Log(ProcessingWords.Describe(item.File.Path, item.Plan, result));
                    changed?.Invoke(Batch);
                }),
                null, cancellationToken).ConfigureAwait(false));
            finished += run.Count;
        }

        // the results of Zlučovanie, one after another, each from its recipe
        var merged = 0;
        foreach (var file in muxes)
        {
            var share = 1.0 / total;
            var from = (double)finished++ / total;
            if (cancellationToken.IsCancellationRequested)
            {
                file.State = MegaFileState.Cancelled;
                file.Note = "zrušené";
                changed?.Invoke(Batch);
                continue;
            }

            var recipe = file.Mux!;
            var problem = mux is null ? "Zlučovanie tu nie je k dispozícii"
                : recipe.Changed() is { } moved ? $"súbor {Path.GetFileName(moved)} sa medzičasom zmenil alebo chýba – výsledok Zlučovania treba pripraviť znova"
                : null;
            if (problem is not null)
            {
                skipped++;
                file.State = MegaFileState.Skipped;
                file.Note = problem;
                Log(ProcessingWords.Skipped(file.Path, problem, false));
                context.Log(JobSeverity.Warning, $"PRESKOČENÝ (Zlučovanie): {Path.GetFileName(file.Path)} – {problem}");
                changed?.Invoke(Batch);
                continue;
            }

            context.Log(JobSeverity.Info, $"Zlučovanie: {recipe.Spec.Sources.Count} vstup(y), {recipe.Spec.Tracks.Count} stôp → {file.PlannedPath}");
            MuxResult result;
            try
            {
                result = await mux!(recipe.Spec, new Share(context, from, share), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new MuxResult(MuxStatus.Cancelled, null, []);
            }

            foreach (var message in result.Messages)
            {
                context.Log(result.Status == MuxStatus.Done ? JobSeverity.Warning : JobSeverity.Error, message);
            }

            file.State = result.Status switch
            {
                MuxStatus.Done => MegaFileState.Done,
                MuxStatus.Cancelled => MegaFileState.Cancelled,
                _ => MegaFileState.Failed,
            };
            file.Note = result.OutputPath ?? (result.Status == MuxStatus.Cancelled ? "zrušené" : string.Join(" | ", result.Messages));
            file.ResultPath = result.Status == MuxStatus.Done ? result.OutputPath : null;
            merged += result.Status == MuxStatus.Done ? 1 : 0;
            Log(MuxJob.Describe(recipe.Spec, result));
            context.Log(result.Status == MuxStatus.Done ? JobSeverity.Info : JobSeverity.Error,
                $"{finished} / {total} · Zlučovanie: " + (result.Status == MuxStatus.Done ? "hotovo – " + result.OutputPath : result.Status == MuxStatus.Cancelled ? "zrušené" : "chyba"));
            changed?.Invoke(Batch);
        }

        var done = results.Count(r => r.Status == ProcessingStatus.Done) + merged;
        var present = waiting.Count(f => f.State == MegaFileState.AlreadyInLibrary);
        var failed = results.Count(r => r.Status == ProcessingStatus.Failed) + muxes.Count(f => f.State == MegaFileState.Failed);
        var problems = waiting.Count(f => f.State == MegaFileState.Skipped);
        var cancelled = cancellationToken.IsCancellationRequested;
        Outcome = $"{done} z {waiting.Count} súborov v knižnici" + (present > 0 ? $", {present} tam už bolo" : "") +
                  (problems > 0 ? $", {problems} preskočených pri kontrole" : "") + (failed > 0 ? $", {failed} s chybou" : "") +
                  (cancelled ? " – zrušené." : ".");
        return cancelled ? JobStatus.Cancelled
            : failed + problems > 0 ? JobStatus.DoneWithWarnings
            : JobStatus.Done;
    }

    /// <summary>A finished batch is not repeated from the queue: it is started again on the MEGA page.</summary>
    internal override bool Reset() => false;

    // the progress of one part of the batch (a run, a result of Zlučovanie) in its share of the whole
    private sealed class Share(IJobContext context, double from, double share) : IProgress<double>
    {
        public void Report(double value) => context.Report(from + (Math.Clamp(value, 0, 1) * share));
    }

    private sealed class Reporter(IJobContext context, double from, double share, Action<BatchItem, ProcessingResult> finished) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value)
        {
            context.Report(from + (share * (value.Total == 0 ? 0 : (value.Finished + (value.Result is null ? value.FileFraction : 0)) / value.Total)));
            if (value.Result is { } result)
            {
                var (severity, text) = BatchJob.Line(value.Item, result);
                context.Log(severity, $"{value.Finished} / {value.Total} · {text}");
                finished(value.Item, result);
            }
        }
    }
}
