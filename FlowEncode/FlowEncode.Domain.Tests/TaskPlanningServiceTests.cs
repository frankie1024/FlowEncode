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
        var plan = await plans.CreateAsync(new(TaskKind.Audio, Audio: fixture.Request(new(Dialnorm: -27)) with { OverwriteOutput = false }));
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
}
