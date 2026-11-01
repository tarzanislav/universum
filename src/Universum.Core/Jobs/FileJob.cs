using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Planning;
using Universum.Core.Text;

namespace Universum.Core.Jobs;

/// <summary>
/// A plan written by the <see cref="FileProcessor"/> as a job (rules, header editor): the same safe route as when it
/// runs directly – verification, undo record, Recycle Bin – with progress and the tools' warnings in the job output.
/// </summary>
public sealed class FileJob(string description, FileProcessor processor, EditPlan plan, Placement placement, OutputOptions output)
    : Job(description), ILoggedJob
{
    public EditPlan Plan { get; } = plan;

    /// <summary>The page that added it: "Single spracovanie", "Úprava hlavičky" (the description up to the colon).</summary>
    public string LogKind => Description.Split(':')[0].Trim();

    public IReadOnlyList<LoggedFile> LoggedFiles => Result is { } result ? [ProcessingWords.Describe(Plan.Target.Source.Path, Plan, result)]
        : Status == JobStatus.Failed ? [new LoggedFile(Plan.Target.Source.Path, null, LoggedOutcome.Failed, CoreText.T("Chyba – originál ostal nezmenený"), [],
            [.. Messages.Where(m => m.Severity == JobSeverity.Error).Select(m => new LoggedProblem(m.Text, CoreText.T("Skúste súbor spracovať znova.")))], [])]
        : [];

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
            ProcessingStatus.Done => CoreText.F("Hotovo: {0}", result.OutputPath),
            ProcessingStatus.Unchanged => CoreText.T("Bez zmien."),
            ProcessingStatus.AlreadyInLibrary => CoreText.F("Rovnaký stream už v knižnici je: {0}", result.OutputPath),
            ProcessingStatus.NeedsReview => CoreText.T("Súbor treba najprv skontrolovať."),
            ProcessingStatus.Cancelled => CoreText.T("Zrušené – originál ostal nezmenený."),
            _ when FileProcessor.NeedsAttention(result) => CoreText.T("POZOR: nepodarilo sa a originál sa nepodarilo úplne vrátiť – pozri výstup."),
            _ when result.Messages.Contains(FileProcessor.RestoredMessage) => CoreText.T("Nepodarilo sa – originál je vrátený do pôvodného stavu."),
            _ => CoreText.T("Nepodarilo sa – originál ostal nezmenený."),
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
        WriteMethod.HeaderEdit => CoreText.T("Úprava hlavičky"),
        WriteMethod.Remux => CoreText.T("Nový súbor (mkvmerge)"),
        _ => CoreText.T("Presun do knižnice"),
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
