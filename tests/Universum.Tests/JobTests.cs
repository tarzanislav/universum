using Universum.Core.Execution;
using Universum.Core.Jobs;
using Universum.Core.Library;
using Universum.Core.MkvToolNix;
using Universum.Core.Planning;
using Universum.Core.Processes;
using Universum.Core.Rules;

namespace Universum.Tests;

/// <summary>Job queue and job output.</summary>
public sealed class JobTests
{
    [Test]
    public async Task JobsRunOneAfterAnotherInTheirOrder()
    {
        var queue = new JobQueue();
        var order = new List<string>();
        var a = new TestJob("A", order);
        var b = new TestJob("B", order);
        var c = new TestJob("C", order) { Warn = true };
        queue.Add(a);
        queue.Add(b);
        queue.Add(c);
        Assert.True(queue.Move(c, -1) && queue.Move(c, -1), "C moved to the front");
        Assert.True(!queue.Move(c, -1), "nothing before C");
        Assert.Equal(3, queue.WaitingCount);

        await queue.Start();
        Assert.Sequence(["C", "A", "B"], order, "in queue order, one at a time");
        Assert.True(a.Status == JobStatus.Done && a.Progress == 1 && a.StartedUtc <= a.FinishedUtc, "A done");
        Assert.Equal(JobStatus.DoneWithWarnings, c.Status, "warnings are kept");
        Assert.True(c.Messages.Any(m => m.Severity == JobSeverity.Warning && m.Text == "pozor"), "job output");
        Assert.True(!queue.IsStarted && queue.Running is null, "queue stops when nothing waits");

        queue.ClearFinished();
        Assert.Equal(0, queue.Jobs.Count, "finished jobs cleared");
    }

    [Test]
    public async Task RunningJobCanBeCancelledWaitingOneTakenOut()
    {
        var queue = new JobQueue();
        var order = new List<string>();
        var slow = new TestJob("slow", order) { Delay = TimeSpan.FromSeconds(30) };
        var next = new TestJob("next", order);
        var failing = new TestJob("failing", order) { Fail = true };
        queue.Add(slow);
        queue.Add(next);
        queue.Add(failing);
        var run = queue.Start();
        await Task.Delay(50);
        Assert.Equal<Job?>(slow, queue.Running, "slow runs");
        Assert.True(!queue.Remove(slow), "a running job cannot be removed");
        queue.Cancel(next);
        queue.Cancel(slow);
        await run;
        Assert.Equal(JobStatus.Cancelled, slow.Status, "running job cancelled");
        Assert.True(next.Status == JobStatus.Cancelled && !order.Contains("next"), "waiting job never ran");
        Assert.True(failing.Status == JobStatus.Failed && failing.Errors == 1 && failing.Messages[^1].Text == "zlyhalo", "exception fails only that job");

        Assert.True(queue.Retry(next) && next.Status == JobStatus.Waiting, "retry puts it back");
        await queue.Start();
        Assert.Equal(JobStatus.Done, next.Status, "ran on retry");
    }

    [Test]
    public async Task StoppedQueueFinishesTheRunningJobOnly()
    {
        var queue = new JobQueue();
        var order = new List<string>();
        var first = new TestJob("first", order) { Delay = TimeSpan.FromMilliseconds(100) };
        var second = new TestJob("second", order);
        queue.Add(first);
        queue.Add(second);
        var run = queue.Start();
        queue.Stop();
        await run;
        Assert.True(first.Status == JobStatus.Done && second.Status == JobStatus.Waiting, "stop waits for the running job, the rest waits");
    }

    [Test]
    public async Task FileJobWritesThroughTheProcessor()
    {
        var installation = await TestData.RequireMkvToolNixAsync();
        var work = Directory.CreateTempSubdirectory("universum-job-");
        try
        {
            var file = Path.Combine(work.FullName, "film.mkv");
            File.Copy(TestData.Media("en-und-cz-forced.mkv"), file);
            var inspector = new MkvMergeInspector(installation, new ProcessRunner());
            var source = await inspector.InspectAsync(file);
            var edits = ManualTarget.AsIs(source).ToList();
            (edits[1], edits[2]) = (edits[2], edits[1]);
            var processor = new FileProcessor(installation, new ProcessRunner(), new NoBin(), Path.Combine(work.FullName, "journal"));
            var job = new FileJob("Úprava hlavičky: film.mkv", processor, EditPlan.For(ManualTarget.Create(source, edits)), Placement.InPlace(file), new OutputOptions(OriginalHandling.Keep));
            ProcessingResult? finished = null;
            job.Finished += (_, result) => finished = result;
            var progress = new List<double>();
            job.Changed += j => progress.Add(j.Progress);

            var queue = new JobQueue();
            queue.Add(job, start: true);
            await queue.Start();
            Assert.Equal(JobStatus.Failed, job.Status, "no Recycle Bin: the old file cannot be replaced, nothing written");
            Assert.True(finished?.Status == ProcessingStatus.Failed && job.Errors > 0 && job.Outcome.StartsWith("Nepodarilo", StringComparison.Ordinal), job.Outcome);

            var header = new FileJob("title", processor, EditPlan.For(ManualTarget.Create(source, ManualTarget.AsIs(source), title: "Job")), Placement.InPlace(file), new OutputOptions(OriginalHandling.Keep));
            queue.Add(header, start: true);
            await queue.Start();
            Assert.True(header.Status == JobStatus.Done && header.Result?.UndoRecordPath is not null, "header edit as a job, with undo – " + string.Join("; ", header.Messages.Select(m => m.Text)));
            Assert.Equal("Job", (await inspector.InspectAsync(file)).Title, "written");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private sealed class NoBin : IRecycleBin
    {
        public string? CannotRecycle(string path) => "no recycle bin in tests";

        public void MoveToRecycleBin(string path) => throw new IOException("no recycle bin in tests");
    }

    private sealed class TestJob(string name, List<string> order) : Job(name)
    {
        public TimeSpan Delay { get; init; }

        public bool Warn { get; init; }

        public bool Fail { get; init; }

        protected override async Task<JobStatus> ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
        {
            order.Add(Description);
            context.Report(0.5);
            await Task.Delay(Delay, cancellationToken);
            if (Fail)
            {
                throw new InvalidOperationException("zlyhalo");
            }

            if (Warn)
            {
                context.Log(JobSeverity.Warning, "pozor");
            }

            return JobStatus.Done;
        }
    }
}
