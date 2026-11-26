using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class TaskPlanningServiceTests
{
    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task ChangedSourceOrConfig_RejectsPreviouslyInspectedPlan(bool config)
    {
        using var fixture = new Deew5ExecutionTests.Fixture();
        var settings = new SharedTaskCoordinatorTests.Settings(); var tasks = new TaskCoordinator(settings);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var plans = new TaskPlanningService(tasks, settings, fixture.Probes, null!, new DeewPlanningService(fixture.Probes), null!, runner, null!, null!);
        var request = fixture.Request(new(Dialnorm: -27));
        var plan = await plans.CreateAsync(new(TaskKind.Audio, Audio: request));
        await File.AppendAllTextAsync(config ? fixture.ConfigPath : request.SourcePath, "\n ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => plans.SubmitAsync(plan.PlanId, "stale"));
        Assert.AreEqual(0, tasks.List().Count);
    }

    [TestMethod]
    public async Task ConcurrentSubmitAndLostResponse_ExecuteOnceAndRetainReceiptAfterCompletion()
    {
        using var fixture = new Deew5ExecutionTests.Fixture();
        var settings = new SharedTaskCoordinatorTests.Settings(); var tasks = new TaskCoordinator(settings);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var plans = new TaskPlanningService(tasks, settings, fixture.Probes, null!, new DeewPlanningService(fixture.Probes), null!, runner, null!, null!);
        var plan = await plans.CreateAsync(new(TaskKind.Audio, Audio: fixture.Request(new(Dialnorm: -27, NoPhaseShift: true)) with { OverwriteOutput = false }));
        CollectionAssert.AreEqual(new[] { "-sps", "off" },
            CommandArgumentTokenizer.Tokenize(plan.DisplayCommand).SkipWhile(argument => argument != "-sps").Take(2).ToArray());
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => plans.SubmitAsync(plan.PlanId, "same-request")));
        Assert.AreEqual(1, receipts.Select(r => r.TaskId).Distinct().Count());
        var taskId = receipts[0].TaskId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (tasks.HasActiveTasks) await Task.Delay(30, timeout.Token);
        Assert.AreEqual(EncodingJobState.Completed, tasks.Get(taskId).State, tasks.Get(taskId).Log);
        var repeated = await plans.SubmitAsync(plan.PlanId, "same-request");
        Assert.IsTrue(repeated.AlreadyAccepted); Assert.AreEqual(taskId, repeated.TaskId);
        Assert.AreEqual(1, tasks.List().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedPlan_DoesNotExpireAfterAcceptance_AndStillDetectsChanges(bool changeSource)
    {
        using var fixture = new Deew5ExecutionTests.Fixture();
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var settings = new SharedTaskCoordinatorTests.Settings();
        var tasks = new TaskCoordinator(settings);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var clock = new TestClock();
        var plans = new TaskPlanningService(tasks, settings, fixture.Probes, null!, new DeewPlanningService(fixture.Probes), null!, runner, null!, null!, clock);
        var releaseSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingTask = tasks.RunAsync(root.Video(), async (_, token) =>
        {
            await releaseSlot.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            return 0;
        }, _ => new(EncodingJobState.Completed, "", "", []));
        var request = fixture.Request(new(Dialnorm: -27)) with { OverwriteOutput = false };
        Directory.CreateDirectory(request.OutputPath);
        File.WriteAllText(Path.Combine(request.OutputPath, "keep.txt"), "unrelated");
        var plan = await plans.CreateAsync(new(TaskKind.Audio, Audio: request));
        var receipt = await plans.SubmitAsync(plan.PlanId, "queued");
        Assert.AreEqual(EncodingJobState.Queued, tasks.Get(receipt.TaskId).State);

        clock.UtcNow = plan.ExpiresAt.AddMinutes(1);
        if (changeSource)
            await File.AppendAllTextAsync(request.SourcePath, "\n ");
        releaseSlot.SetResult();
        await blockingTask;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (tasks.HasActiveTasks) await Task.Delay(30, timeout.Token);

        var result = tasks.Get(receipt.TaskId);
        Assert.AreEqual(changeSource ? EncodingJobState.Failed : EncodingJobState.Completed, result.State, result.Log);
        if (changeSource) StringAssert.Contains(result.Error, "changed after preview");
        Assert.AreEqual(receipt.TaskId, (await plans.SubmitAsync(plan.PlanId, "queued")).TaskId);
    }

    [TestMethod]
    public async Task AcceptedPlan_ReleasesItsPendingPlanSlot()
    {
        using var fixture = new BusinessExecutionSafetyTests.Fixture("success");
        var tasks = new TaskCoordinator(fixture.Settings);
        var runner = new CoordinatedBluRayRunner(new CliBluRayDemuxRunner([fixture.DemuxRunner(BluRayDemuxBackend.DgDemux)]), tasks);
        var clock = new TestClock();
        var plans = new TaskPlanningService(tasks, fixture.Settings, fixture.Probes, null!, null!, null!, null!, runner, null!, clock);
        var request = new TaskWorkload(TaskKind.BluRay, BluRay: fixture.DemuxRequest(BluRayDemuxBackend.DgDemux));
        var first = await plans.CreateAsync(request);
        for (var i = 1; i < 500; i++)
            await plans.CreateAsync(request);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => plans.CreateAsync(request));

        var receipt = await plans.SubmitAsync(first.PlanId, "first-plan");
        var next = await plans.CreateAsync(request);
        Assert.AreEqual(receipt.TaskId, (await plans.SubmitAsync(first.PlanId, "first-plan")).TaskId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (tasks.HasActiveTasks) await Task.Delay(30, timeout.Token);
        Assert.AreEqual(EncodingJobState.Completed, tasks.Get(receipt.TaskId).State);

        clock.UtcNow = next.ExpiresAt;
        var expired = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => plans.SubmitAsync(next.PlanId, "expired"));
        StringAssert.Contains(expired.Message, "plan expired");
        await plans.CreateAsync(request);
    }

    [TestMethod]
    public async Task MkvTrackWildcard_RejectsMatchingOutputOnCreateAndSubmit()
    {
        using var fixture = new BusinessExecutionSafetyTests.Fixture("mkv-demux");
        var source = Path.Combine(fixture.Root, "source.mkv");
        File.WriteAllText(source, "input");
        var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "tracks")).FullName;
        var playlist = new BluRayPlaylistItem("source", "source", "", source, "", "", null, null);
        var track = new BluRayTrackItem("1", 1, "1", BluRayTrackKind.Audio, "Audio", "", "");
        var pattern = BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, output, playlist, track);
        var request = new TaskWorkload(TaskKind.BluRay, BluRay: new(Guid.NewGuid(), BluRayDemuxBackend.Eac3To, source,
            output, Path.Combine(output, "source"), playlist, [new(track, pattern)], OverwriteOutput: false));
        var tasks = new TaskCoordinator(fixture.Settings);
        var runner = new CoordinatedBluRayRunner(new CliBluRayDemuxRunner([fixture.DemuxRunner(BluRayDemuxBackend.Eac3To)]), tasks);
        var plans = new TaskPlanningService(tasks, fixture.Settings, fixture.Probes, null!, null!, null!, null!, runner, null!);
        File.WriteAllText(Path.Combine(output, "unrelated.flac"), "unrelated");
        var plan = await plans.CreateAsync(request);
        var conflict = Path.ChangeExtension(pattern, ".FLAC");
        File.WriteAllText(conflict, "existing track");

        var createError = await Assert.ThrowsExactlyAsync<IOException>(() => plans.CreateAsync(request));
        var submitError = await Assert.ThrowsExactlyAsync<IOException>(() => plans.SubmitAsync(plan.PlanId, "conflict"));

        StringAssert.Contains(createError.Message, conflict);
        StringAssert.Contains(submitError.Message, conflict);
        Assert.IsEmpty(tasks.List());
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "eac3to-calls.log")));
    }

    [TestMethod]
    public async Task Retry_CreatesNewTaskAndKeyCannotBeReusedForDifferentOriginal()
    {
        using var fixture = new Deew5ExecutionTests.Fixture();
        var settings = new SharedTaskCoordinatorTests.Settings(); var tasks = new TaskCoordinator(settings);
        var runner = new CoordinatedAudioRunner(fixture.Runner, tasks);
        var plans = new TaskPlanningService(tasks, settings, fixture.Probes, null!, new DeewPlanningService(fixture.Probes), null!, runner, null!, null!);
        var original = new TaskWorkload(TaskKind.Audio, Audio: fixture.Request(new(Dialnorm: -27)));
        tasks.Reserve(original); tasks.Cancel(original.TaskId);
        var retried = await plans.RetryAsync(original.TaskId, "retry-key");
        Assert.AreNotEqual(original.TaskId, retried.TaskId);
        Assert.AreEqual(original.TaskId, tasks.Get(retried.TaskId).RetryOfTaskId);
        Assert.AreEqual(retried.TaskId, (await plans.RetryAsync(original.TaskId, "retry-key")).TaskId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plans.RetryAsync(Guid.NewGuid(), "retry-key"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (tasks.HasActiveTasks) await Task.Delay(30, timeout.Token);
        Assert.AreEqual(EncodingJobState.Completed, tasks.Get(retried.TaskId).State, tasks.Get(retried.TaskId).Log);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
