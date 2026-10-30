using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Planning;

namespace Universum.Core.Jobs;

/// <summary>
/// A plan written by the <see cref="FileProcessor"/> as a job (rules, header editor): the same safe route as when it
/// runs directly – verification, undo record, Recycle Bin – with progress and the tools' warnings in the job output.
/// </summary>
public sealed class FileJob(string description, FileProcessor processor, EditPlan plan, Placement placement, OutputOptions output)
    : Job(description)
{
    public EditPlan Plan { get; } = plan;

    /// <summary>The result of the last run.</summary>
    public ProcessingResult? Result { get; private set; }

    /// <summary>Raised after a run with its result (the page that added the job refreshes itself).</summary>
    public event Action<FileJob, ProcessingResult>? Finished;

    protected override async Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
    {
        context.Log(JobSeverity.Info, $"{Method(Plan.Method)}: {Plan.Target.Source.Path}");
        var result = await processor.ProcessAsync(Plan, placement, output, new Reporter(context), cancellationToken);
        Result = result;
        foreach (var message in result.Messages)
        {
            context.Log(Severity(message, result.Status), message);
        }

        Outcome = result.Status switch
        {
            ProcessingStatus.Done => "Hotovo: " + result.OutputPath,
            ProcessingStatus.Unchanged => "Bez zmien.",
            ProcessingStatus.AlreadyInLibrary => "Rovnaký stream už v knižnici je: " + result.OutputPath,
            ProcessingStatus.NeedsReview => "Súbor treba najprv skontrolovať.",
            ProcessingStatus.Cancelled => "Zrušené – originál ostal nezmenený.",
            _ when FileProcessor.NeedsAttention(result) => "POZOR: nepodarilo sa a originál sa nepodarilo úplne vrátiť – pozri výstup.",
            _ when result.Messages.Contains(FileProcessor.RestoredMessage) => "Nepodarilo sa – originál je vrátený do pôvodného stavu.",
            _ => "Nepodarilo sa – originál ostal nezmenený.",
        };
        Finished?.Invoke(this, result);
        return result.Status switch
        {
            ProcessingStatus.Done or ProcessingStatus.Unchanged or ProcessingStatus.AlreadyInLibrary => JobStatus.Done,
            ProcessingStatus.Cancelled => JobStatus.Cancelled,
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

    private static string Method(WriteMethod method) => method switch
    {
        WriteMethod.HeaderEdit => "Úprava hlavičky",
        WriteMethod.Remux => "Nový súbor (mkvmerge)",
        _ => "Presun do knižnice",
    };

    /// <summary>Tool lines say what they are ("Warning: …", "#GUI#error …"); other messages count as errors when the job failed.</summary>
    private static JobSeverity Severity(string message, ProcessingStatus status) =>
        message.StartsWith("Warning:", StringComparison.Ordinal) || message.StartsWith("#GUI#warning", StringComparison.Ordinal) ? JobSeverity.Warning
        : message.StartsWith("Error:", StringComparison.Ordinal) || message.StartsWith("#GUI#error", StringComparison.Ordinal) ? JobSeverity.Error
        : status is ProcessingStatus.Failed ? JobSeverity.Error
        : message == FileProcessor.RestoredMessage || message == FileProcessor.OriginalRecycledMessage ||
          message.StartsWith(FileProcessor.ReplacedPreviousPrefix, StringComparison.Ordinal) ? JobSeverity.Info
        : JobSeverity.Warning;

    private sealed class Reporter(IJobContext context) : IProgress<double>
    {
        public void Report(double value) => context.Report(value);
    }
}
