using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.Infrastructure.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SharedTaskCoordinatorTests
{
    [TestMethod]
    public async Task QueuedSourceDisappears_FailsBeforeExecutionAndReleasesTheSlot()
    {
        using var root = new TestRoot();
        var tasks = new TaskCoordinator(new Settings());
        var first = root.Video();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = tasks.RunAsync(first, async (_, _) => { entered.SetResult(); await release.Task; return 1; }, Done);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = root.Audio();
        var ran = false;
        var pendingRun = tasks.RunAsync(pending, (_, _) => { ran = true; return Task.FromResult(2); }, Done);
        File.Delete(pending.SourcePath);
        release.SetResult();

        await firstRun;
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => pendingRun.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(ran);
        Assert.AreEqual(EncodingJobState.Failed, tasks.Get(pending.TaskId).State);
        var following = root.Video();
        Assert.AreEqual(3, await tasks.RunAsync(following, (_, _) => Task.FromResult(3), Done).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(tasks.HasActiveTasks);
    }

    [TestMethod]
    public async Task IdleActionAndNewTaskAdmission_CannotOverlap()
    {
        using var root = new TestRoot();
        var tasks = new TaskCoordinator(new Settings());
        var active = root.Video();
        var receipt = tasks.Reserve(active, TaskOrigin.Mcp, key: "already-accepted");
        Assert.IsFalse(await tasks.TryRunWhenIdleAsync(() => throw new AssertFailedException("The queue has an active task.")));
        tasks.Cancel(active.TaskId);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleAction = tasks.TryRunWhenIdleAsync(() => release.Task);
        var incoming = root.Video();
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(incoming, TaskOrigin.Mcp));
            Assert.AreEqual(receipt.TaskId, tasks.FindSubmission("already-accepted")?.TaskId);
        }
        finally
        {
            release.SetResult();
        }

        Assert.IsTrue(await idleAction);
        Assert.AreEqual(incoming.TaskId, tasks.Reserve(incoming, TaskOrigin.Mcp).TaskId);
    }

    [TestMethod]
    public async Task ShutdownCommand_KeepsQueueClosedThroughHandoffAndReopensAfterCancellation()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This test requires Windows.");

        using var root = new TestRoot();
        var tasks = new TaskCoordinator(new Settings());
        var incoming = root.Video();
        using var shuttingDown = new ManualResetEventSlim();
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? error = null;
        var action = tasks.TryRunWhenIdleAsync(async () =>
        {
            error = await WindowsQueueCompletionActionService.ExecuteShutdownAsync(
                new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit /b 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                }, () =>
                {
                    polled.TrySetResult();
                    if (!shuttingDown.IsSet) return false;
                    sawShutdown.TrySetResult();
                    return true;
                }, TimeSpan.FromSeconds(5));
        });

        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(incoming, TaskOrigin.Mcp));
        shuttingDown.Set();
        try
        {
            await sawShutdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(incoming, TaskOrigin.Mcp));
        }
        finally
        {
            shuttingDown.Reset();
        }

        Assert.IsTrue(await action.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("Windows cancelled shutdown.", error);
        Assert.AreEqual(incoming.TaskId, tasks.Reserve(incoming, TaskOrigin.Mcp).TaskId);
    }

    [TestMethod]
    public async Task ShutdownCommand_WithNonzeroExitCodeReportsFailure()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This test requires Windows.");

        var error = await WindowsQueueCompletionActionService.ExecuteShutdownAsync(
            new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit /b 17")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }, () => false, TimeSpan.FromSeconds(5));

        StringAssert.Contains(error, "exit code 17");
    }

    [TestMethod]
    public async Task RunnerReturningNonterminalState_CannotLeavePhantomRunningTask()
    {
        using var root = new TestRoot();
        var tasks = new TaskCoordinator(new Settings());
        var request = root.Video();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => tasks.RunAsync(request, (_, _) => Task.FromResult(1),
            _ => new(EncodingJobState.Running, "still running", "", [])));
        Assert.AreEqual(EncodingJobState.Failed, tasks.Get(request.TaskId).State);
        Assert.IsFalse(tasks.HasActiveTasks);
    }

    [TestMethod]
    public async Task VideoAndAudio_UseOneConcurrencyBudget_AndCancellationTargetsOnlyItsId()
    {
        using var root = new TestRoot();
        var tasks = new TaskCoordinator(new Settings());
        var first = root.Video(); var second = root.Audio();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = tasks.RunAsync(first, async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return 1; }, Done);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = tasks.RunAsync(second, async (_, token) => { secondEntered.SetResult(); await secondRelease.Task.WaitAsync(token); return 2; }, Done);
        Assert.AreEqual(EncodingJobState.Queued, tasks.Get(second.TaskId).State);
        Assert.IsFalse(secondEntered.Task.IsCompleted);
        release.SetResult(); Assert.AreEqual(1, await firstRun.WaitAsync(TimeSpan.FromSeconds(5)));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(tasks.Cancel(first.TaskId));
        Assert.AreEqual(EncodingJobState.Running, tasks.Get(second.TaskId).State);
        secondRelease.SetResult(); Assert.AreEqual(2, await secondRun.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task QueuedCancellation_NeverRunsItsBody()
    {
        using var root = new TestRoot();
        var tasks = new TaskCoordinator(new Settings()); var first = root.Video(); var second = root.Audio();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = tasks.RunAsync(first, async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return 1; }, Done);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ran = false;
        var secondRun = tasks.RunAsync(second, (_, _) => { ran = true; return Task.FromResult(2); }, Done);
        tasks.Cancel(second.TaskId); tasks.Cancel(first.TaskId);
        await Assert.ThrowsAsync<OperationCanceledException>(() => secondRun);
        await Assert.ThrowsAsync<OperationCanceledException>(() => firstRun);
        Assert.IsFalse(ran); Assert.AreEqual(EncodingJobState.Cancelled, tasks.Get(second.TaskId).State);
    }

    [TestMethod]
    public async Task ConcurrentPipeClients_SameOutputAcceptsExactlyOne_AndDuplicateKeyFindsOriginal()
    {
        using var root = new TestRoot(); var tasks = new TaskCoordinator(new Settings());
        var output = Path.Combine(root.Path, "same.264"); var plan = Guid.NewGuid();
        await using var server = new DesktopAutomationServer("FlowEncode.Test." + Guid.NewGuid().ToString("N"), (method, data, _) =>
        {
            var request = data!.Value.Deserialize<TaskWorkload>(DesktopAutomationProtocol.JsonOptions)!;
            return Task.FromResult<object?>(tasks.Reserve(request, TaskOrigin.Mcp, plan, request.TaskId.ToString()));
        });
        server.Start();
        var accepted = new ConcurrentBag<TaskSubmission>(); var rejected = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            try { accepted.Add(await new DesktopAutomationClient(server.PipeName).SendAsync<TaskSubmission>("reserve", root.Video(output))); }
            catch (DesktopAutomationException ex) { rejected.Add(ex.Code); }
        })).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(1, accepted.Count); Assert.AreEqual(15, rejected.Count);
        var winner = accepted.Single();
        Assert.AreEqual(winner.TaskId, tasks.FindSubmission(winner.TaskId.ToString(), plan)!.TaskId);
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.FindSubmission(winner.TaskId.ToString(), Guid.NewGuid()));
    }

    [TestMethod]
    public void DurableReceipt_AfterDesktopRestartDoesNotResubmitInterruptedTask()
    {
        using var root = new TestRoot();
        var paths = new LocalAppPaths(root.Path); var store = new TaskHistoryStore(paths);
        var first = new TaskCoordinator(new Settings(), store); var request = root.Video(); var plan = Guid.NewGuid();
        first.Reserve(request, TaskOrigin.Mcp, plan, "lost-response");
        var restarted = new TaskCoordinator(new Settings(), store);
        var receipt = restarted.FindSubmission("lost-response", plan)!;
        Assert.IsTrue(receipt.AlreadyAccepted); Assert.AreEqual(request.TaskId, receipt.TaskId);
        Assert.AreEqual(EncodingJobState.Failed, restarted.Get(receipt.TaskId).State);
        Assert.IsFalse(restarted.HasActiveTasks);
    }

    [TestMethod]
    public void DirectoryClaims_BlockOverlappingManualAndAiOutputs()
    {
        using var root = new TestRoot(); var tasks = new TaskCoordinator(new Settings());
        var resultDir = Path.Combine(root.Path, "result");
        var ddpRequest = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = resultDir } };
        tasks.Reserve(ddpRequest);

        // Same directory with overlapping DEEW DDP extension (.ec3) or another DEEW DDP task must be blocked.
        var conflictingFile = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Eac3To, OutputPath = Path.Combine(resultDir, "track.ec3") } };
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(conflictingFile, TaskOrigin.Mcp));

        var secondDdp = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = resultDir } };
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(secondDdp, TaskOrigin.Mcp));
    }

    [TestMethod]
    public void SameDirectory_NonOverlappingVideoAndDeewAudioTasks_AreAllowed()
    {
        using var root = new TestRoot(); var tasks = new TaskCoordinator(new Settings());
        var projectDir = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(projectDir);

        // Video encode writing movie.265 into projectDir does NOT conflict with DEEW audio in projectDir.
        var video = root.Video(Path.Combine(projectDir, "movie.265"));
        tasks.Reserve(video);

        var deewDdp = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = projectDir, Deew = new DeewOptions(Format: "ddp") } };
        var ddpReceipt = tasks.Reserve(deewDdp);
        Assert.AreEqual(deewDdp.TaskId, ddpReceipt.TaskId);

        // A different DEEW format (TrueHD -> .thd) and a measure-only DEEW task in the same directory also do not collide.
        var deewThd = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = projectDir, Deew = new DeewOptions(Format: "thd") } };
        Assert.AreEqual(deewThd.TaskId, tasks.Reserve(deewThd).TaskId);

        var deewMeasure = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = projectDir, Deew = new DeewOptions(MeasureOnly: true) } };
        Assert.AreEqual(deewMeasure.TaskId, tasks.Reserve(deewMeasure).TaskId);

        // Conversely, starting another video encode (different filename) while DEEW is active in the same folder is allowed.
        var video2 = root.Video(Path.Combine(projectDir, "movie_av1.ivf"));
        Assert.AreEqual(video2.TaskId, tasks.Reserve(video2).TaskId);
    }

    [TestMethod]
    public void BluRayWildcardOutputClaim_BlocksOverlappingConcreteAndDeewTasks()
    {
        using var root = new TestRoot(); var tasks = new TaskCoordinator(new Settings());
        var discDir = Path.Combine(root.Path, "disc");
        var outDir = Path.Combine(root.Path, "demux");
        Directory.CreateDirectory(discDir);
        Directory.CreateDirectory(outDir);
        var playlist = new BluRayPlaylistItem("00001", "00001.mpls", "10:00", discDir, "00001.mpls", "10:00", TimeSpan.FromMinutes(10), 1);
        var track = new BluRayTrackItem("4113", 1, "4113", BluRayTrackKind.Video, "Video", "H.264", "eng");
        var bluray = new TaskWorkload(TaskKind.BluRay, BluRay: new BluRayDemuxRequest(
            Guid.NewGuid(),
            BluRayDemuxBackend.DgDemux,
            discDir,
            outDir,
            Path.Combine(outDir, "00001"),
            playlist,
            [new BluRayTrackSelection(track, Path.Combine(outDir, "00001.*"))],
            OverwriteOutput: false));

        tasks.Reserve(bluray);

        var collidingVideo = root.Video(Path.Combine(outDir, "00001.264"));
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(collidingVideo));

        var collidingPidVideo = root.Video(Path.Combine(outDir, "00001 PID 4113.264"));
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(collidingPidVideo));

        var collidingDeew = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = outDir, Deew = new DeewOptions(Format: "ddp") } };
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(collidingDeew));

        var nonCollidingVideo = root.Video(Path.Combine(outDir, "00001 PID 4114.265"));
        Assert.AreEqual(nonCollidingVideo.TaskId, tasks.Reserve(nonCollidingVideo).TaskId);
    }

    [TestMethod]
    public void FinalPublish_RacingExistingOutputIsPreservedAndEncodedFileIsRecoverable()
    {
        using var root = new TestRoot(); var final = Path.Combine(root.Path, "movie.264"); var staged = Path.Combine(root.Path, "staged.264");
        File.WriteAllText(staged, "new encoded bytes"); File.WriteAllText(final, "other program's file");
        var failure = ExecutionOutputStaging.FinalizeFileOrPreserve(staged, final, Guid.NewGuid(), false, null);
        Assert.IsNotNull(failure); Assert.IsNotNull(failure.PreservedPath);
        Assert.AreEqual("other program's file", File.ReadAllText(final)); Assert.AreEqual("new encoded bytes", File.ReadAllText(failure.PreservedPath));
    }

    [TestMethod]
    public void DirectoryPublish_PreflightsEntireManifestWithoutPartialMoves()
    {
        using var root = new TestRoot(); var output = Path.Combine(root.Path, "out"); var stage = Path.Combine(root.Path, "stage");
        Directory.CreateDirectory(output); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "a.ec3"), "new a"); File.WriteAllText(Path.Combine(stage, "b.ec3"), "new b");
        File.WriteAllText(Path.Combine(output, "b.ec3"), "old b");
        Assert.ThrowsExactly<IOException>(() => ExecutionOutputStaging.FinalizeDirectory(stage, output, false));
        Assert.IsFalse(File.Exists(Path.Combine(output, "a.ec3"))); Assert.AreEqual("old b", File.ReadAllText(Path.Combine(output, "b.ec3")));
        Assert.AreEqual(2, Directory.GetFiles(stage).Length);
    }

    private static TaskCompletion Done(int _) => new(EncodingJobState.Completed, "done", "", []);
    internal sealed class Settings : IAppSettingsService
    {
        public AppSettings Value = AppSettings.Default;
        public AppSettings Load() => Value;
        public void Save(AppSettings value) => Value = value;
    }
    internal sealed class TestRoot : IDisposable
    {
        public string Path { get; } = Deew5ContractTests.NewRoot();
        public TestRoot()
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "source.y4m"), "YUV4MPEG2 W16 H16 F24:1 C420\n");
            File.WriteAllText(System.IO.Path.Combine(Path, "source.wav"), "audio");
        }
        public TaskWorkload Video(string? output = null) => new(TaskKind.Video, Video: new(Guid.NewGuid(), DefaultEncodingProfiles.GetDefault(EncoderKind.X264),
            System.IO.Path.Combine(Path, "source.y4m"), output ?? System.IO.Path.Combine(Path, Guid.NewGuid() + ".264"), InputPipelineKind.Y4mFile, EncoderArchitecture.X64, OverwriteOutput: false));
        public TaskWorkload Audio() => new(TaskKind.Audio, Audio: new(Guid.NewGuid(), System.IO.Path.Combine(Path, "source.wav"), System.IO.Path.Combine(Path, Guid.NewGuid() + ".opus"),
            AudioProcessingMode.Opus, null, [], null, 2, "stereo", 192, false, OverwriteOutput: false));
        public void Dispose() => Directory.Delete(Path, true);
    }
}
