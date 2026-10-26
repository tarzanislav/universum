using Universum.Core.Muxing;

namespace Universum.Core.Jobs;

/// <summary>The multiplexer as a job: mkvmerge's progress, warnings and errors go to the job output.</summary>
public sealed class MuxJob(string description, Multiplexer multiplexer, MuxSpec spec) : Job(description)
{
    public MuxSpec Spec { get; } = spec;

    public MuxResult? Result { get; private set; }

    protected override async Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
    {
        context.Log(JobSeverity.Info, $"Zlučovanie: {Spec.Sources.Count} vstup(y), {Spec.Tracks.Count} stôp → {Spec.OutputPath}");
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
            MuxStatus.Done => "Hotovo: " + result.OutputPath,
            MuxStatus.Cancelled => "Zrušené – nič sa nezapísalo.",
            _ => "Nepodarilo sa – nič sa nezapísalo.",
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
