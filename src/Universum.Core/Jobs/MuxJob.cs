using Universum.Core.Muxing;
using Universum.Core.Text;

namespace Universum.Core.Jobs;

/// <summary>The multiplexer as a job: mkvmerge's progress, warnings and errors go to the job output.</summary>
public sealed class MuxJob(string description, Multiplexer multiplexer, MuxSpec spec) : Job(description), ILoggedJob
{
    public MuxSpec Spec { get; } = spec;

    public string LogKind => CoreText.T("Zlučovanie");

    public IReadOnlyList<LoggedFile> LoggedFiles => Result is { } result ? [Describe(Spec, result)] : [];

    /// <summary>A run of the multiplexer in words (here and in MEGA spracovanie).</summary>
    public static LoggedFile Describe(MuxSpec spec, MuxResult result)
    {
        var failed = result.Status != MuxStatus.Done;
        var explained = result.Messages.Select(m => (Message: m, Words: Execution.ProcessingWords.Explain(m, failed))).ToList();
        var problems = explained.Where(e => e.Words is (_, { } problem)).Select(e => e.Words!.Value.Problem!).ToList();
        if (result.Status == MuxStatus.Failed && problems.Count == 0)
        {
            problems.Add(new LoggedProblem(CoreText.T("Zlučovanie sa nepodarilo (podrobnosti sú v technických riadkoch)."), CoreText.T("Skontrolujte vstupné súbory a voľné miesto na disku a skúste znova.")));
        }

        return new LoggedFile(spec.Sources.FirstOrDefault()?.File.Path ?? "", result.OutputPath,
            result.Status switch { MuxStatus.Done => LoggedOutcome.Done, MuxStatus.Cancelled => LoggedOutcome.Cancelled, _ => LoggedOutcome.Failed },
            result.Status switch { MuxStatus.Done => CoreText.T("Hotovo – nový súbor"), MuxStatus.Cancelled => CoreText.T("Zrušené – nič sa nezapísalo"), _ => CoreText.T("Chyba – nič sa nezapísalo") },
            [CoreText.F("Zlúčené: {0} vstup(y), {1} stôp.", spec.Sources.Count, spec.Tracks.Count), .. explained.Where(e => e.Words is ({ } step, null)).Select(e => e.Words!.Value.Step!)],
            problems, [.. explained.Where(e => e.Words is null or (_, not null)).Select(e => e.Message)]);
    }

    public MuxResult? Result { get; private set; }

    protected override async Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
    {
        var output = Spec.Placement is { } placement
            ? Path.Combine(placement.Directory, placement.Prefix) + CoreText.T(" … (názov podľa pravidiel, veľkosť sa doplní po zápise)")
            : Spec.OutputPath;
        context.Log(JobSeverity.Info, CoreText.F("Zlučovanie: {0} vstup(y), {1} stôp → {2}", Spec.Sources.Count, Spec.Tracks.Count, output));
        var result = await multiplexer.MuxAsync(Spec, new Reporter(context), cancellationToken);
        Result = result;
        foreach (var message in result.Messages)
        {
            context.Log(
                message.StartsWith("Warning:", StringComparison.Ordinal) || message.StartsWith("#GUI#warning", StringComparison.Ordinal)
                    ? JobSeverity.Warning
                    : result.Status == MuxStatus.Done ? JobSeverity.Warning : JobSeverity.Error,
                message);
        }

        Outcome = result.Status switch
        {
            MuxStatus.Done => CoreText.F("Hotovo: {0}", result.OutputPath),
            MuxStatus.Cancelled => CoreText.T("Zrušené – nič sa nezapísalo."),
            _ => CoreText.T("Nepodarilo sa – nič sa nezapísalo."),
        };
        return result.Status switch
        {
            MuxStatus.Done => JobStatus.Done,
            MuxStatus.Cancelled => JobStatus.Cancelled,
            _ => JobStatus.Failed,
        };
    }

    internal override bool Reset()
    {
        if (!base.Reset())
        {
            return false;
        }

        Result = null;
        return true;
    }

    private sealed class Reporter(IJobContext context) : IProgress<double>
    {
        public void Report(double value) => context.Report(value);
    }
}
