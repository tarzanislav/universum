using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Jobs;
using Universum.App.Localization;

namespace Universum.App.Jobs;

/// <summary>A job in the queue and output pages; follows the job's changes on the UI thread.</summary>
public sealed class JobRow : ObservableObject
{
    public JobRow(Job job)
    {
        Job = job;
        job.Changed += _ => OnUi(() => OnPropertyChanged(string.Empty));
    }

    public Job Job { get; }

    public int Number => Job.Id;

    public string Description => Job.Description;

    public string Status => Job.Status switch
    {
        JobStatus.Waiting => Loc.T("čaká"),
        JobStatus.Running => Loc.F("beží {0:P0}", Job.Progress),
        JobStatus.Done => Loc.T("hotovo"),
        JobStatus.DoneWithWarnings => Loc.F("hotovo, upozornenia: {0}", Job.Warnings),
        JobStatus.Failed => Loc.T("chyba"),
        JobStatus.Cancelled => Loc.T("zrušené"),
        _ => Job.Status.ToString(),
    };

    public double Progress => Job.Progress;

    public bool IsRunning => Job.Status == JobStatus.Running;

    public bool IsProblem => Job.Status is JobStatus.Failed || Job.Errors > 0;

    public bool HasWarnings => Job.Status == JobStatus.DoneWithWarnings;

    public string Outcome => Job.Outcome;

    public string Added => Job.AddedUtc.ToLocalTime().ToString("HH:mm:ss");

    public string Duration => Job.StartedUtc is { } start
        ? ((Job.FinishedUtc ?? DateTime.UtcNow) - start).ToString(@"m\:ss")
        : "";

    public string Display => $"{Number}. {Description}";

    /// <summary>Runs on the UI thread (jobs report progress from the tools' output threads).</summary>
    public static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>One line of a job's output.</summary>
public sealed record JobMessageRow(string Time, string Severity, string Text, bool IsWarning, bool IsError)
{
    public static JobMessageRow Of(JobMessage message) => new(
        message.TimeUtc.ToLocalTime().ToString("HH:mm:ss"),
        message.Severity switch { JobSeverity.Warning => Loc.T("upozornenie"), JobSeverity.Error => Loc.T("chyba"), _ => "info" },
        message.Text,
        message.Severity == JobSeverity.Warning,
        message.Severity == JobSeverity.Error);
}
