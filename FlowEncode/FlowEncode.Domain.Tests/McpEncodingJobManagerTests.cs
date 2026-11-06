using System.Collections.Concurrent;
using FlowEncode.Application;
using FlowEncode.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class McpEncodingJobManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Enqueue_RunsJobAndRecordsProgressAndResult()
    {
        var runner = new FakeEncodingJobRunner();
        using var manager = CreateManager(runner, maxConcurrentJobs: 1);

        var job = manager.Enqueue(CreateRequest());
        var control = await runner.WaitForStartAsync(job.Request.JobId);
        control.Progress.Report(new EncodingJobProgress(
            job.Request.JobId,
            EncodingJobState.Running,
            0.5,
            "Encoding 50%",
            "frame 50",
            new EncodingProgressSnapshot(50, 100, 24.0, 3000, TimeSpan.FromSeconds(3), null)));

        var running = job.GetStatus(logLineCount: 10);
        Assert.AreEqual("Running", running.State);
        Assert.AreEqual(50.0, running.ProgressPercent);
        Assert.AreEqual(100L, running.TotalFrames);
        CollectionAssert.Contains(running.RecentLogLines.ToList(), "frame 50");

        control.Complete(EncodingJobState.Completed);
        await job.Completion.WaitAsync(Timeout);

        var completed = job.GetStatus(logLineCount: 0);
        Assert.AreEqual("Completed", completed.State);
        Assert.AreEqual(100.0, completed.ProgressPercent);
        Assert.AreEqual(0, completed.ExitCode);
        Assert.IsNotNull(completed.FinishedAt);
    }

    [TestMethod]
    public async Task Enqueue_WhenConcurrencyLimitReached_QueuesUntilASlotFrees()
    {
        var runner = new FakeEncodingJobRunner();
        using var manager = CreateManager(runner, maxConcurrentJobs: 1);

        var first = manager.Enqueue(CreateRequest());
        var second = manager.Enqueue(CreateRequest());
        var firstControl = await runner.WaitForStartAsync(first.Request.JobId);

        Assert.AreEqual("Queued", second.GetStatus(0).State);
        Assert.IsFalse(runner.HasStarted(second.Request.JobId));

        firstControl.Complete(EncodingJobState.Completed);
        var secondControl = await runner.WaitForStartAsync(second.Request.JobId);
        secondControl.Complete(EncodingJobState.Completed);
        await second.Completion.WaitAsync(Timeout);

        Assert.AreEqual("Completed", second.GetStatus(0).State);
    }

    [TestMethod]
    public async Task Cancel_WhenQueued_MarksCancelledWithoutStartingTheRunner()
    {
        var runner = new FakeEncodingJobRunner();
        using var manager = CreateManager(runner, maxConcurrentJobs: 1);

        var first = manager.Enqueue(CreateRequest());
        var queued = manager.Enqueue(CreateRequest());
        await runner.WaitForStartAsync(first.Request.JobId);

        Assert.IsTrue(manager.Cancel(queued.Request.JobId));
        await queued.Completion.WaitAsync(Timeout);

        Assert.AreEqual("Cancelled", queued.GetStatus(0).State);
        Assert.IsFalse(runner.HasStarted(queued.Request.JobId));
        Assert.IsTrue(runner.AbortedJobIds.Contains(queued.Request.JobId));
    }

    [TestMethod]
    public async Task Cancel_WhenRunning_CancelsTokenAndAbortsRunner()
    {
        var runner = new FakeEncodingJobRunner();
        using var manager = CreateManager(runner, maxConcurrentJobs: 1);

        var job = manager.Enqueue(CreateRequest());
        await runner.WaitForStartAsync(job.Request.JobId);

        Assert.IsTrue(manager.Cancel(job.Request.JobId));
        await job.Completion.WaitAsync(Timeout);

        Assert.AreEqual("Cancelled", job.GetStatus(0).State);
        Assert.IsTrue(runner.AbortedJobIds.Contains(job.Request.JobId));
        Assert.IsFalse(manager.Cancel(job.Request.JobId), "A finished job cannot be cancelled again.");
    }

    [TestMethod]
    public async Task Enqueue_WhenRunnerThrows_MarksFailedWithMessage()
    {
        var runner = new FakeEncodingJobRunner { FailureMessage = "No usable x264 executable was found." };
        using var manager = CreateManager(runner, maxConcurrentJobs: 1);

        var job = manager.Enqueue(CreateRequest());
        await job.Completion.WaitAsync(Timeout);

        var status = job.GetStatus(logLineCount: 5);
        Assert.AreEqual("Failed", status.State);
        StringAssert.Contains(status.Summary, "No usable x264 executable was found.");
    }

    [TestMethod]
    public async Task StopAsync_AbortsActiveJobs()
    {
        var runner = new FakeEncodingJobRunner();
        using var manager = CreateManager(runner, maxConcurrentJobs: 2);

        var job = manager.Enqueue(CreateRequest());
        await runner.WaitForStartAsync(job.Request.JobId);

        await manager.StopAsync(CancellationToken.None);

        Assert.AreEqual("Cancelled", job.GetStatus(0).State);
        Assert.IsTrue(runner.AbortedJobIds.Contains(job.Request.JobId));
    }

    [TestMethod]
    public void ProfileResolver_ParsesEncoderAliases()
    {
        Assert.AreEqual(EncoderKind.SvtAv1, EncodingProfileResolver.ParseEncoder("SVT-AV1"));
        Assert.AreEqual(EncoderKind.X265, EncodingProfileResolver.ParseEncoder("hevc"));
        Assert.AreEqual(RateControlMode.TwoPass, EncodingProfileResolver.ParseRateControl("2-pass"));
        Assert.ThrowsExactly<McpException>(() => EncodingProfileResolver.ParseEncoder("vp9"));
    }

    [TestMethod]
    public async Task ProfileResolver_AppliesOverridesOnTopOfDefaultProfile()
    {
        var profile = await EncodingProfileResolver.ResolveAsync(
            new EmptyProfileLibrary(),
            templateId: null,
            encoder: "x264",
            new EncodingProfileOptions { Quality = 21.5, Preset = "slow", OutputContainer = ".mkv" },
            CancellationToken.None);

        var defaults = DefaultEncodingProfiles.GetDefault(EncoderKind.X264);
        Assert.AreEqual(21.5, profile.Quality);
        Assert.AreEqual("slow", profile.Preset);
        Assert.AreEqual("mkv", profile.OutputContainer);
        Assert.AreEqual(defaults.AdditionalArguments, profile.AdditionalArguments);
    }

    [TestMethod]
    public async Task ProfileResolver_WhenNeitherTemplateNorEncoder_Throws()
    {
        await Assert.ThrowsExactlyAsync<McpException>(() => EncodingProfileResolver.ResolveAsync(
            new EmptyProfileLibrary(),
            templateId: null,
            encoder: null,
            options: null,
            CancellationToken.None));
    }

    private static EncodingJobManager CreateManager(FakeEncodingJobRunner runner, int maxConcurrentJobs) =>
        new(runner, new FixedSettingsService(maxConcurrentJobs), NullLogger<EncodingJobManager>.Instance);

    private static EncodingJobRequest CreateRequest() => new(
        Guid.NewGuid(),
        DefaultEncodingProfiles.GetDefault(EncoderKind.X264),
        @"C:\media\source.mkv",
        $@"C:\media\out-{Guid.NewGuid():N}.264",
        InputPipelineKind.FfmpegPipe,
        EncoderArchitecture.X64);

    private sealed class FakeEncodingJobRunner : IEncodingJobRunner
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JobControl>> _started = new();

        public string? FailureMessage { get; init; }

        public ConcurrentBag<Guid> AbortedJobIds { get; } = new();

        public bool HasStarted(Guid jobId) => _started.TryGetValue(jobId, out var started) && started.Task.IsCompleted;

        public Task<JobControl> WaitForStartAsync(Guid jobId) => GetStartSignal(jobId).Task.WaitAsync(Timeout);

        public async Task<EncodingJobResult> RunAsync(
            EncodingJobRequest request,
            IProgress<EncodingJobProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (FailureMessage is not null)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            var control = new JobControl(progress!);
            GetStartSignal(request.JobId).TrySetResult(control);
            var state = await control.Result.Task.WaitAsync(cancellationToken);
            return new EncodingJobResult(request.JobId, state, 0, state.ToString(), string.Empty, string.Empty);
        }

        public string BuildDisplayCommand(EncodingJobRequest request) => "x264 --fake";

        public void AbortJob(Guid jobId) => AbortedJobIds.Add(jobId);

        private TaskCompletionSource<JobControl> GetStartSignal(Guid jobId) =>
            _started.GetOrAdd(jobId, static _ => new TaskCompletionSource<JobControl>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class JobControl(IProgress<EncodingJobProgress> progress)
    {
        public IProgress<EncodingJobProgress> Progress { get; } = progress;

        public TaskCompletionSource<EncodingJobState> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(EncodingJobState state) => Result.TrySetResult(state);
    }

    private sealed class FixedSettingsService(int maxConcurrentJobs) : IAppSettingsService
    {
        public AppSettings Load() => AppSettings.Default with { MaxConcurrentEncodingJobs = maxConcurrentJobs };

        public void Save(AppSettings settings)
        {
        }
    }

    private sealed class EmptyProfileLibrary : IProfileLibraryService
    {
        public Task<IReadOnlyList<SavedTemplate>> GetUserTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SavedTemplate>>([]);

        public Task<SavedTemplate> ReadTemplateAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ExportTemplateAsync(SavedTemplate template, string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SavedTemplate> SaveTemplateAsync(string name, string notes, EncodingProfile profile, string? templateId = null, bool isPinned = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SavedTemplate> SetTemplatePinnedAsync(string templateId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommandPreview> BuildPreviewAsync(EncodingProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
