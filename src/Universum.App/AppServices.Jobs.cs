using System.IO;
using System.Net.Http;
using System.Text.Json;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Histra;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Processes;
using Universum.Core.Speech;
using Universum.Core.Subtitles;
using Universum.Core.Weather;
using Universum.Platform.Windows;
using Universum.App.Localization;

namespace Universum.App;

// AppServices: the queue of jobs, the processing logs, the MEGA batches
public sealed partial class AppServices
{
    /// <summary>The job queue (one for the whole program; jobs run one after another).</summary>
    public Universum.Core.Jobs.JobQueue Jobs => _jobs ??= Queue();

    private Universum.Core.Jobs.JobQueue? _jobs;

    /// <summary>How the end of the queue is announced (the self-test records it instead of a sound).</summary>
    public Action<bool> PlayFinishSound { get; set; } = FinishSound.Play;

    /// <summary>
    /// The logs of processing (owner, 31. 10. 2026): every run of Hromadné and Single spracovanie, MEGA, the studios and the
    /// other editors, in words; kept in the settings folder until the user deletes them.
    /// </summary>
    public Universum.Core.Jobs.ProcessingLogStore ProcessingLogs => _processingLogs ??= new(Path.Combine(AppSettings.Directory, "zaznamy"));

    private Universum.Core.Jobs.ProcessingLogStore? _processingLogs;

    // jobs already in the logs: the job and the end of its run (a job run again is logged again)
    private readonly HashSet<(int, DateTime?)> _loggedJobs = [];

    /// <summary>A file processed directly by a page (not through the queue) into the logs.</summary>
    public void LogProcessing(string kind, Universum.Core.Planning.EditPlan plan, Universum.Core.Execution.ProcessingResult result, DateTime startedUtc)
    {
        var file = Universum.Core.Execution.ProcessingWords.Describe(plan.Target.Source.Path, plan, result);
        KeepLog(new Universum.Core.Jobs.ProcessingLog(kind, startedUtc, DateTime.UtcNow, file.Headline, [file]));
    }

    // a log written to its file; a log that cannot be written is not worth a failure of the processing
    private void KeepLog(Universum.Core.Jobs.ProcessingLog log)
    {
        try
        {
            ProcessingLogs.Add(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the processing itself is done; only its log is missing
        }
    }

    // the queue, with the sound of its end (owner, 29. 10. 2026: Hromadné spracovanie and the queue; can be switched off) and
    // the logs of its jobs
    private Universum.Core.Jobs.JobQueue Queue()
    {
        var queue = new Universum.Core.Jobs.JobQueue();
        queue.JobChanged += job =>
        {
            if (job.IsFinished && job is Universum.Core.Jobs.ILoggedJob logged)
            {
                lock (_loggedJobs)
                {
                    if (!_loggedJobs.Add((job.Id, job.FinishedUtc)))
                    {
                        return;
                    }
                }

                KeepLog(new Universum.Core.Jobs.ProcessingLog(logged.LogKind, job.StartedUtc ?? job.AddedUtc, job.FinishedUtc ?? DateTime.UtcNow, job.Outcome, logged.LoggedFiles));
            }
        };
        queue.Finished += jobs =>
        {
            if (Settings.FinishSound)
            {
                PlayFinishSound(jobs.Any(j => j.Status is Universum.Core.Jobs.JobStatus.Failed || j.Errors > 0));
            }
        };
        return queue;
    }

    /// <summary>Adds a job; starts the queue when the settings say so.</summary>
    public void Enqueue(Universum.Core.Jobs.Job job) => Jobs.Add(job, Settings.AutoStartQueue);

    private Universum.Core.Mega.MegaStore? _mega;

    /// <summary>
    /// The batches of MEGA spracovanie (owner, 29. 10. 2026). A list that cannot be read is put aside (renamed, never
    /// deleted) and a new one starts; <see cref="MegaProblem"/> says so.
    /// </summary>
    public Universum.Core.Mega.MegaStore Mega
    {
        get
        {
            if (_mega is null)
            {
                try
                {
                    _mega = new Universum.Core.Mega.MegaStore(AppSettings.MegaFile);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    var aside = AppSettings.MegaFile + ".poskodeny-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    var file = AppSettings.MegaFile;
                    try
                    {
                        File.Move(AppSettings.MegaFile, aside);
                    }
                    catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                    {
                        // it stays where it is; the new list is kept beside it
                        aside = AppSettings.MegaFile;
                        file = AppSettings.MegaFile + ".novy";
                    }

                    MegaProblem = Loc.F("Zoznam dávok sa nedal prečítať ({0}); pôvodný je odložený v {1}.", ex.Message, aside);
                    _mega = new Universum.Core.Mega.MegaStore(file);
                }
            }

            return _mega;
        }
    }

    /// <summary>Why the list of MEGA batches could not be read (empty when it was).</summary>
    public string MegaProblem { get; private set; } = "";

    /// <summary>Raised when a batch was added or changed outside the MEGA page (Hromadné spracovanie sent one).</summary>
    public event Action? MegaChanged;

    public void NotifyMegaChanged() => MegaChanged?.Invoke();
}
