using System.Collections.Concurrent;
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
        var request = root.Audio() with { Audio = root.Audio().Audio! with { Mode = AudioProcessingMode.Ddp, OutputPath = Path.Combine(root.Path, "result") } };
        tasks.Reserve(request);
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Reserve(root.Video(Path.Combine(root.Path, "result", "movie.264")), TaskOrigin.Mcp));
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
        public TaskWorkload Video(string? output = null) => new(TaskKind.Video, Video: new(Guid.NewGuid(), DefaultEncodingProfiles.GetDefault(EncoderKind.X264),
            System.IO.Path.Combine(Path, "source.y4m"), output ?? System.IO.Path.Combine(Path, Guid.NewGuid() + ".264"), InputPipelineKind.Y4mFile, EncoderArchitecture.X64, OverwriteOutput: false));
        public TaskWorkload Audio() => new(TaskKind.Audio, Audio: new(Guid.NewGuid(), System.IO.Path.Combine(Path, "source.wav"), System.IO.Path.Combine(Path, Guid.NewGuid() + ".opus"),
            AudioProcessingMode.Opus, null, [], null, 2, "stereo", 192, false, OverwriteOutput: false));
        public void Dispose() => Directory.Delete(Path, true);
    }
}
